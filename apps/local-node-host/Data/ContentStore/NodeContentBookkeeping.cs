using System.Globalization;
using System.Text.Json;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Harborline.Kernel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>The bookkeeping port over the node's one storage model (ADR 0113 decision 1).</summary>
/// <remarks>
/// A transaction the store opens takes the node's write lock with <see cref="HomeEpochFenceTransaction"/>, so its conditional
/// statements decide against settled rows and a row it read stays held until it ends. The wait is bounded by the connection's
/// <c>busy_timeout</c> (<see cref="SqlCipherConnectionInterceptor"/>), and running out surfaces as a failed write.
/// </remarks>
public sealed class NodeContentBookkeeping(IDbContextFactory<LocalNodeDbContext> contexts) : IContentBookkeeping
{
    // A reader kept the last checkpoint from finishing, so a deleted key may still be in the WAL. The next commit tries again.
    private volatile bool _checkpointOwed;

    /// <inheritdoc />
    public async ValueTask<IContentBookkeepingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new Transaction(this, context, await HomeEpochFenceTransaction.BeginAsync(context, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A session in the transaction a caller already holds on <paramref name="context"/>, so the bookkeeping commits or rolls back
    /// with the caller's record (ADR 0113 decision 1). The caller owns the commit. After a committed removal that reclaimed an
    /// item, it ends the batch with <see cref="CheckpointAsync(CancellationToken)"/>.
    /// </summary>
    public static IContentBookkeepingSession Join(LocalNodeDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("A content-store session needs the caller's open transaction.");
        return new Session(context);
    }

    /// <summary>
    /// Ends a reclamation batch by moving the WAL into the database and truncating it, so no copy of a deleted key stays there
    /// (ADR 0114 decision 4). False means a reader held it past the wait, which SQLite reports in its result row and not as an
    /// error; the next commit on this port tries again.
    /// </summary>
    /// <remarks>
    /// A commit runs this after it has committed, so it waits for a reader at most <see cref="CheckpointWaitMilliseconds"/>, not
    /// the connection's 30-second <c>busy_timeout</c>, and puts that timeout back before the connection is reused.
    /// </remarks>
    public async ValueTask<bool> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = context.Database.GetDbConnection().CreateCommand();
            // Microsoft.Data.Sqlite retries SQLITE_BUSY until the command timeout, in seconds, so that is bounded too.
            command.CommandTimeout = 1;
            command.CommandText = "PRAGMA busy_timeout;";
            var prior = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            command.CommandText = $"PRAGMA busy_timeout = {CheckpointWaitMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var result = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var finished = await result.ReadAsync(cancellationToken).ConfigureAwait(false) && result.GetInt64(0) == 0;
                _checkpointOwed = !finished;
                return finished;
            }
            finally
            {
                command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {prior};");
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>How long the post-commit checkpoint waits for a reader before it is left owed.</summary>
    internal const int CheckpointWaitMilliseconds = 1000;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ContentStoreItem>> StagedCreatedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var ticks = cutoff.UtcTicks;
            var rows = await context.Set<ContentStagedRow>().AsNoTracking().Where(row => row.CreatedUtcTicks <= ticks).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(row => ToItem(row, []))];
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> NamesBytesAsync(Guid itemKey, CancellationToken cancellationToken = default)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.Set<ContentStagedRow>().AnyAsync(row => row.ItemKey == itemKey, cancellationToken).ConfigureAwait(false)
                || await context.Set<ContentItemRow>().AnyAsync(row => row.ItemKey == itemKey, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ContentStoreItem?> FindItemAsync(ContentStorageItemKey key, CancellationToken cancellationToken = default)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await ReadItemAsync(context, key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ContentIntegrityEntry?> FindIntegrityAsync(TenantId tenant, Guid itemKey, CancellationToken cancellationToken = default)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await ReadIntegrityAsync(context, tenant, itemKey, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ReclamationEntry> Reclamations
    {
        get
        {
            using var context = contexts.CreateDbContext();
            return [.. context.Set<ContentReclamationRow>().AsNoTracking().OrderBy(row => row.Id).AsEnumerable()
                .Select(row => new ReclamationEntry(new(row.ContentId), new(row.TenantId), row.Cause, row.Run))];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentIntegrityEntry> IntegrityEntries
    {
        get
        {
            using var context = contexts.CreateDbContext();
            return [.. context.Set<ContentIntegrityRow>().AsNoTracking().AsEnumerable().Select(ToEntry)];
        }
    }

    /// <inheritdoc />
    public int StagedCount
    {
        get
        {
            using var context = contexts.CreateDbContext();
            return context.Set<ContentStagedRow>().Count();
        }
    }

    /// <inheritdoc />
    public int CommittedCount
    {
        get
        {
            using var context = contexts.CreateDbContext();
            return context.Set<ContentItemRow>().Count();
        }
    }

    private static async ValueTask<ContentStoreItem?> ReadItemAsync(LocalNodeDbContext context, ContentStorageItemKey key, CancellationToken cancellationToken)
    {
        var row = await context.Set<ContentItemRow>().AsNoTracking()
            .FirstOrDefaultAsync(item => item.TenantId == key.TenantId.Value && item.ContentId == key.ContentId.Value, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        var owners = await context.Set<ContentOwnerRow>().AsNoTracking().Where(owner => owner.ItemKey == row.ItemKey).OrderBy(owner => owner.Id)
            .Select(owner => new ContentOwner(owner.Record, owner.Element)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToItem(row, owners);
    }

    private static async ValueTask<ContentIntegrityEntry?> ReadIntegrityAsync(LocalNodeDbContext context, TenantId tenant, Guid itemKey, CancellationToken cancellationToken)
    {
        var row = await context.Set<ContentIntegrityRow>().AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.TenantId == tenant.Value && entry.ItemKey == itemKey, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    private static ContentStoreItem ToItem(ContentItemColumns row, IReadOnlyList<ContentOwner> owners) =>
        new(row.ItemKey, new(row.TenantId), new(row.ContentId), row.Length, row.MediaType, Instant(row.CreatedUtcTicks),
            row.WrappedDataKey, row.WrapNonce, row.WrapTag, row.NoncePrefix, row.SegmentSize, row.EpochId)
        {
            Owners = owners,
            State = (ContentLifecycleState)row.State,
        };

    private static ContentIntegrityEntry ToEntry(ContentIntegrityRow row) =>
        new(row.ItemKey, new(row.TenantId), Instant(row.FirstSeenUtcTicks), Instant(row.LastSeenUtcTicks), row.Count, row.SegmentIndex, row.Active,
            row.TraceId, row.OperationId, row.PrincipalId, new(row.OwnerRecord, row.OwnerElement));

    private static DateTimeOffset Instant(long ticks) => new(ticks, TimeSpan.Zero);

    private class Session(LocalNodeDbContext context) : IContentBookkeepingSession
    {
        private readonly SortedSet<string> _tenants = new(StringComparer.Ordinal);
        private ContentStoreOperation? _operation;

        protected bool Reclaimed { get; private set; }
        protected LocalNodeDbContext Context => context;

        public async ValueTask StageOperationAsync(KernelOperationIdentity operation, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var inserted = await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_operations ("IdempotencyKey", "CommandId", "Fingerprint")
                VALUES ({operation.IdempotencyKey}, {operation.CommandId}, {operation.Fingerprint})
                ON CONFLICT ("IdempotencyKey") DO NOTHING
                """, cancellationToken).ConfigureAwait(false);
            if (inserted == 0) throw new InvalidOperationException("That idempotency key was already used.");
        }

        public ValueTask StageRecordAsync(ContentStoreOperation record, CancellationToken cancellationToken = default)
        {
            _operation = record;
            return ValueTask.CompletedTask;
        }

        // Each write has its own row in the node's audit chain, in this transaction (the owner's ruling of 2026-11-05, 17:47 UTC).
        public async ValueTask StageAuditAsync(KernelAuditEvidence audit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audit);
            var operation = _operation ?? throw new InvalidOperationException("A content-store write must be staged before its audit.");
            // The chain has no column for these, and copying them into the payload is forbidden, so nothing is dropped silently.
            if (!audit.AuthoritySnapshot.IsEmpty || audit.OriginatorId is not null)
                throw new InvalidOperationException("The content store's audit row cannot carry an authority snapshot or an originator.");
            if (_tenants.Count == 0)
            {
                // A sweep that only deletes bytes no entry names touched no tenant's rows, so no tenant's chain has anything to record.
                if (operation == ContentStoreOperation.SweepExpired) return;
                throw new InvalidOperationException("A content-store write named no tenant.");
            }

            var eventType = "ContentStore." + operation;
            var occurredAt = DateTimeOffset.Parse(audit.RecordedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
            // The chain is per tenant, and the column scopes who may read the row (Q36); the payload names no tenant or content id.
            // Only a sweep spans tenants, so it leaves one row in each chain it touched.
            var payload = JsonSerializer.Serialize(new { event_type = eventType, evidence = Convert.ToBase64String(audit.Payload.Span) });
            foreach (var tenant in _tenants)
            {
                var auditId = _tenants.Count == 1 ? audit.AuditId : audit.AuditId + ":" + tenant;
                var previous = await context.Set<NodeAuditEventRow>().FromSql($"""
                    SELECT * FROM node_audit_events WHERE "TenantId" = {tenant} ORDER BY rowid DESC LIMIT 1
                    """).AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                context.Set<NodeAuditEventRow>().Add(new NodeAuditEventRow
                {
                    AuditId = auditId, TenantId = tenant, EventType = eventType, OccurredAt = occurredAt, Actor = audit.ActorId, Payload = payload, PrevHash = previous?.Hash,
                    Hash = NodeAuditHashChain.ComputeHash(previous?.Hash, auditId, eventType, audit.ActorId, tenant, occurredAt, payload),
                });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _tenants.Clear();
        }

        public ValueTask<ContentStoreItem?> FindItemAsync(ContentStorageItemKey key, CancellationToken cancellationToken = default) =>
            ReadItemAsync(context, key, cancellationToken);

        // The transaction already holds the node's write lock, so the row stays held until it ends.
        public ValueTask<ContentStoreItem?> FindItemForUpdateAsync(ContentStorageItemKey key, CancellationToken cancellationToken = default)
        {
            _tenants.Add(key.TenantId.Value);
            return ReadItemAsync(context, key, cancellationToken);
        }

        public async ValueTask<ContentStoreItem?> FindStagedAsync(Guid stageId, CancellationToken cancellationToken = default)
        {
            var row = await context.Set<ContentStagedRow>().AsNoTracking().FirstOrDefaultAsync(staged => staged.ItemKey == stageId, cancellationToken).ConfigureAwait(false);
            if (row is null) return null;
            _tenants.Add(row.TenantId); // A commit past its expiry only removes the staged row, and its audit still belongs to that tenant.
            return ToItem(row, []);
        }

        // The transaction already holds the node's write lock, so what this reads is what a promotion moves.
        public ValueTask<ContentStoreItem?> FindStagedForUpdateAsync(Guid stageId, CancellationToken cancellationToken = default) =>
            FindStagedAsync(stageId, cancellationToken);

        public ValueTask<ContentIntegrityEntry?> FindIntegrityAsync(TenantId tenant, Guid itemKey, CancellationToken cancellationToken = default) =>
            ReadIntegrityAsync(context, tenant, itemKey, cancellationToken);

        // Staged entries first, then committed ones, the order a promotion locks in (the port's contract).
        public async ValueTask<IReadOnlyList<ContentStoreItem>> ItemsUnderEpochAsync(TenantId tenant, Guid epochId, int limit, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            var staged = await context.Set<ContentStagedRow>().AsNoTracking()
                .Where(row => row.TenantId == tenant.Value && row.EpochId == epochId).OrderBy(row => row.ItemKey).Take(limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var items = new List<ContentStoreItem>(staged.Select(row => ToItem(row, [])));
            if (items.Count < limit)
            {
                var committed = await context.Set<ContentItemRow>().AsNoTracking()
                    .Where(row => row.TenantId == tenant.Value && row.EpochId == epochId).OrderBy(row => row.ItemKey).Take(limit - items.Count)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                items.AddRange(committed.Select(row => ToItem(row, [])));
            }

            return items;
        }

        // A transaction this port opens holds the node's write lock, so no retirement can run until it ends: the pin is a read
        // of the durable marker. ponytail: the node's one SQLite writer is the lock; a provider with row locks needs FOR SHARE here.
        // A pin names the tenant whose chain the transaction's audit row joins, as a rewrap's does even when its batch is empty.
        public async ValueTask<bool> PinEpochAsync(TenantId tenant, Guid epochId, CancellationToken cancellationToken = default)
        {
            _tenants.Add(tenant.Value);
            return !await context.Set<ContentEpochRow>().AnyAsync(row => row.TenantId == tenant.Value && row.EpochId == epochId, cancellationToken).ConfigureAwait(false);
        }

        // Under the same write lock no pin is open, so retiring is a check for naming rows and then the durable marker.
        public async ValueTask<bool> RetireEpochIfUnnamedAsync(TenantId tenant, Guid epochId, CancellationToken cancellationToken = default)
        {
            _tenants.Add(tenant.Value);
            if (await context.Set<ContentStagedRow>().AnyAsync(row => row.TenantId == tenant.Value && row.EpochId == epochId, cancellationToken).ConfigureAwait(false)
                || await context.Set<ContentItemRow>().AnyAsync(row => row.TenantId == tenant.Value && row.EpochId == epochId, cancellationToken).ConfigureAwait(false))
                return false;
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_epochs ("TenantId", "EpochId") VALUES ({tenant.Value}, {epochId})
                ON CONFLICT ("TenantId", "EpochId") DO NOTHING
                """, cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async ValueTask<bool> ReplaceWrapAsync(ContentStoreItem item, Guid epochId, byte[] wrappedDataKey, byte[] wrapNonce, byte[] wrapTag, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            _tenants.Add(item.Tenant.Value);
            var from = item.EpochId;
            var changed = item.State == ContentLifecycleState.Staged
                ? await context.Set<ContentStagedRow>().Where(row => row.ItemKey == item.StageId && row.TenantId == item.Tenant.Value && row.EpochId == from)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpochId, epochId).SetProperty(row => row.WrappedDataKey, wrappedDataKey)
                        .SetProperty(row => row.WrapNonce, wrapNonce).SetProperty(row => row.WrapTag, wrapTag), cancellationToken).ConfigureAwait(false)
                : await context.Set<ContentItemRow>().Where(row => row.ItemKey == item.StageId && row.TenantId == item.Tenant.Value && row.EpochId == from)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpochId, epochId).SetProperty(row => row.WrappedDataKey, wrappedDataKey)
                        .SetProperty(row => row.WrapNonce, wrapNonce).SetProperty(row => row.WrapTag, wrapTag), cancellationToken).ConfigureAwait(false);
            // A rewrap overwrites a wrap in place; the old one may still be in the WAL until a TRUNCATE checkpoint (ADR 0116 decision 5).
            if (changed > 0) Reclaimed = true;
            return changed > 0;
        }

        public async ValueTask AddStagedAsync(ContentStoreItem item, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            _tenants.Add(item.Tenant.Value);
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_staged ("ItemKey", "TenantId", "ContentId", "Length", "MediaType", "CreatedUtcTicks", "WrappedDataKey", "WrapNonce", "WrapTag", "NoncePrefix", "SegmentSize", "State", "EpochId")
                VALUES ({item.StageId}, {item.Tenant.Value}, {item.ContentId.Value}, {item.Length}, {item.MediaType}, {item.CreatedUtc.UtcTicks}, {item.WrappedDataKey}, {item.WrapNonce}, {item.WrapTag}, {item.NoncePrefix}, {item.SegmentSize}, {(int)item.State}, {item.EpochId})
                """, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<bool> RemoveStagedAsync(Guid stageId, CancellationToken cancellationToken = default) =>
            await context.Set<ContentStagedRow>().Where(row => row.ItemKey == stageId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;

        public async ValueTask<ContentQuotaReservation> TryReserveQuotaAsync(TenantId tenant, string record, long bytes, long recordLimit, long tenantLimit, CancellationToken cancellationToken = default)
        {
            _tenants.Add(tenant.Value);
            if (!await TryAddAsync(tenant.Value, false, record, bytes, recordLimit, cancellationToken).ConfigureAwait(false))
                return new(ContentQuotaGrain.Record, await CountAsync(tenant.Value, false, record, cancellationToken).ConfigureAwait(false) + bytes);
            if (await TryAddAsync(tenant.Value, true, "", bytes, tenantLimit, cancellationToken).ConfigureAwait(false)) return new(null, 0);
            await SubtractAsync(tenant.Value, false, record, bytes, cancellationToken).ConfigureAwait(false);
            return new(ContentQuotaGrain.Tenant, await CountAsync(tenant.Value, true, "", cancellationToken).ConfigureAwait(false) + bytes);
        }

        public async ValueTask ReleaseQuotaAsync(TenantId tenant, string record, long bytes, CancellationToken cancellationToken = default)
        {
            _tenants.Add(tenant.Value);
            await SubtractAsync(tenant.Value, false, record, bytes, cancellationToken).ConfigureAwait(false);
            await SubtractAsync(tenant.Value, true, "", bytes, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ContentStoreItem?> TryCreateItemAsync(ContentStoreItem item, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            _tenants.Add(item.Tenant.Value);
            var created = await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_items ("ItemKey", "TenantId", "ContentId", "Length", "MediaType", "CreatedUtcTicks", "WrappedDataKey", "WrapNonce", "WrapTag", "NoncePrefix", "SegmentSize", "State", "EpochId")
                VALUES ({item.StageId}, {item.Tenant.Value}, {item.ContentId.Value}, {item.Length}, {item.MediaType}, {item.CreatedUtc.UtcTicks}, {item.WrappedDataKey}, {item.WrapNonce}, {item.WrapTag}, {item.NoncePrefix}, {item.SegmentSize}, {(int)item.State}, {item.EpochId})
                ON CONFLICT ("TenantId", "ContentId") DO NOTHING
                """, cancellationToken).ConfigureAwait(false);
            if (created > 0)
            {
                foreach (var owner in item.Owners) await InsertOwnerAsync(item.StageId, owner, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await ReadItemAsync(context, new(item.Tenant, item.ContentId), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<bool> AddOwnerAsync(ContentStorageItemKey key, ContentOwner owner, CancellationToken cancellationToken = default)
        {
            _tenants.Add(key.TenantId.Value);
            var itemKey = await ItemKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (itemKey is null) return false;
            await InsertOwnerAsync(itemKey.Value, owner, cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async ValueTask<bool> RemoveOwnerAsync(ContentStorageItemKey key, ContentOwner owner, CancellationToken cancellationToken = default)
        {
            _tenants.Add(key.TenantId.Value);
            var itemKey = await ItemKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (itemKey is null) return false;
            var occurrence = await context.Set<ContentOwnerRow>()
                .Where(row => row.ItemKey == itemKey && row.Record == owner.Record && row.Element == owner.Element)
                .OrderBy(row => row.Id).Select(row => (long?)row.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (occurrence is null) return false;
            return await context.Set<ContentOwnerRow>().Where(row => row.Id == occurrence).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
        }

        public async ValueTask<bool> DeleteItemIfUnownedAsync(ContentStorageItemKey key, CancellationToken cancellationToken = default)
        {
            _tenants.Add(key.TenantId.Value);
            var owners = context.Set<ContentOwnerRow>();
            return await context.Set<ContentItemRow>()
                .Where(row => row.TenantId == key.TenantId.Value && row.ContentId == key.ContentId.Value && !owners.Any(owner => owner.ItemKey == row.ItemKey))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
        }

        public async ValueTask AppendReclamationAsync(ReclamationEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Reclaimed = true;
            _tenants.Add(entry.TenantId.Value);
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_reclamations ("ContentId", "TenantId", "Cause", "Run") VALUES ({entry.ContentId.Value}, {entry.TenantId.Value}, {entry.Cause}, {entry.Run})
                """, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask SetIntegrityAsync(ContentIntegrityEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _tenants.Add(entry.TenantId.Value);
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_integrity ("TenantId", "ItemKey", "FirstSeenUtcTicks", "LastSeenUtcTicks", "Count", "SegmentIndex", "Active", "TraceId", "OperationId", "PrincipalId", "OwnerRecord", "OwnerElement")
                VALUES ({entry.TenantId.Value}, {entry.ItemKey}, {entry.FirstSeenUtc.UtcTicks}, {entry.LastSeenUtc.UtcTicks}, {entry.Count}, {entry.SegmentIndex}, {entry.Active}, {entry.TraceId}, {entry.OperationId}, {entry.PrincipalId}, {entry.Owner.Record}, {entry.Owner.Element})
                ON CONFLICT ("TenantId", "ItemKey") DO UPDATE SET
                    "FirstSeenUtcTicks" = excluded."FirstSeenUtcTicks", "LastSeenUtcTicks" = excluded."LastSeenUtcTicks", "Count" = excluded."Count", "SegmentIndex" = excluded."SegmentIndex",
                    "Active" = excluded."Active", "TraceId" = excluded."TraceId", "OperationId" = excluded."OperationId", "PrincipalId" = excluded."PrincipalId",
                    "OwnerRecord" = excluded."OwnerRecord", "OwnerElement" = excluded."OwnerElement"
                """, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<Guid?> ItemKeyAsync(ContentStorageItemKey key, CancellationToken cancellationToken) =>
            await context.Set<ContentItemRow>().Where(row => row.TenantId == key.TenantId.Value && row.ContentId == key.ContentId.Value)
                .Select(row => (Guid?)row.ItemKey).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        private async ValueTask InsertOwnerAsync(Guid itemKey, ContentOwner owner, CancellationToken cancellationToken) =>
            await context.Database.ExecuteSqlAsync(
                $"""INSERT INTO content_store_owners ("ItemKey", "Record", "Element") VALUES ({itemKey}, {owner.Record}, {owner.Element})""", cancellationToken).ConfigureAwait(false);

        // The conditional update that decides quota (ADR 0113 decision 2): a total equal to the limit fits (ck-8, eng-5).
        private async ValueTask<bool> TryAddAsync(string tenant, bool tenantGrain, string record, long bytes, long limit, CancellationToken cancellationToken)
        {
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO content_store_quota ("TenantId", "TenantGrain", "Record", "Bytes") VALUES ({tenant}, {tenantGrain}, {record}, {0L})
                ON CONFLICT ("TenantId", "TenantGrain", "Record") DO NOTHING
                """, cancellationToken).ConfigureAwait(false);
            return await context.Set<ContentQuotaRow>()
                .Where(row => row.TenantId == tenant && row.TenantGrain == tenantGrain && row.Record == record && row.Bytes + bytes <= limit)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Bytes, row => row.Bytes + bytes), cancellationToken).ConfigureAwait(false) > 0;
        }

        private async ValueTask SubtractAsync(string tenant, bool tenantGrain, string record, long bytes, CancellationToken cancellationToken)
        {
            var changed = await context.Set<ContentQuotaRow>()
                .Where(row => row.TenantId == tenant && row.TenantGrain == tenantGrain && row.Record == record && row.Bytes >= bytes)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Bytes, row => row.Bytes - bytes), cancellationToken).ConfigureAwait(false);
            if (changed == 0) throw new InvalidOperationException("A content-store release exceeded the bytes it counted.");
        }

        private async ValueTask<long> CountAsync(string tenant, bool tenantGrain, string record, CancellationToken cancellationToken) =>
            await context.Set<ContentQuotaRow>().Where(row => row.TenantId == tenant && row.TenantGrain == tenantGrain && row.Record == record)
                .Select(row => row.Bytes).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Transaction(NodeContentBookkeeping owner, LocalNodeDbContext context, HomeEpochFenceTransaction.Held fence) : Session(context), IContentBookkeepingTransaction
    {
        public async ValueTask<bool> CommitAsync(CancellationToken cancellationToken = default)
        {
            await fence.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!Reclaimed && !owner._checkpointOwed) return true;
            try
            {
                await owner.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // The removal is committed and must not read as failed (CWE-460), so the next commit tries again.
                owner._checkpointOwed = true;
            }

            return true;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => new(fence.RollbackAsync(cancellationToken));

        public async ValueTask DisposeAsync()
        {
            await fence.DisposeAsync().ConfigureAwait(false);
            await Context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
