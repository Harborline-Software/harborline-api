using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Kernel.Core;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// DES-0029 kernel-core-ck-6: the node journal store's port onto the Platform <see cref="KernelTransactionBoundary"/>.
/// <see cref="BeginAsync"/> takes the single <c>BEGIN IMMEDIATE</c> fence before the command is prepared, so the
/// home-epoch fence read, the audit-chain tip read and a reversal's read of its original all run under the write
/// lock. <see cref="IKernelPreparedTransaction{TRecord,TResult}.CommitAsync"/> is the only journal commit point;
/// disposing an uncommitted transaction rolls everything back.
/// </summary>
internal sealed class NodeJournalKernelTransactionPort(LocalNodeDbContext context)
    : IKernelPreparedTransactionPort<JournalEntry, JournalEntry>
{
    /// <inheritdoc />
    public async ValueTask<IKernelPreparedTransaction<JournalEntry, JournalEntry>> BeginAsync(
        CancellationToken cancellationToken = default) =>
        new Transaction(context, await HomeEpochFenceTransaction.BeginAsync(context, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The kernel command for a prepared journal unit: the entry, its operation identity and the audit row the
    /// audit adapter staged. A post with no staged audit row is refused: record and audit commit together.
    /// </summary>
    public static KernelCommand<JournalEntry> Command(LocalNodeDbContext context, JournalEntry entry)
    {
        var audit = context.ChangeTracker.Entries<NodeAuditEventRow>()
            .Where(row => row.State == EntityState.Added && row.Entity.EventType == NodeAuditWriteEnlister.JournalPostedEventType)
            .Select(row => row.Entity)
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"JournalEntry '{entry.Id.Value}' staged no audit row; a journal post commits its record and audit together.");
        // Kernel audit evidence requires an actor; the journal audit adapter always records the admitting principal.
        if (string.IsNullOrWhiteSpace(audit.Actor))
            throw new InvalidOperationException(
                $"JournalEntry '{entry.Id.Value}' staged an audit row that names no actor; kernel audit evidence requires one.");
        return new(
            new(entry.Id.Value, IdempotencyKey(entry), Fingerprint(entry)),
            entry,
            new(audit.AuditId, audit.Actor, audit.OccurredAt, Encoding.UTF8.GetBytes(audit.Payload)));
    }

    /// <summary>
    /// The durable replay key: the entry's source reference (a subledger event or a client Idempotency-Key, held by
    /// the tenant-scoped unique index), or for an unkeyed manual entry its own id.
    /// </summary>
    private static string IdempotencyKey(JournalEntry entry) =>
        entry.SourceReference is { Length: > 0 } sourceReference ? sourceReference : "journal-entry:" + entry.Id.Value;

    private static string Fingerprint(JournalEntry entry) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Tenant = entry.TenantId.Value,
        entry.EntryDate,
        entry.Memo,
        Chart = entry.ChartId?.Value,
        ReversalOf = entry.ReversalOf?.Value,
        Lines = entry.Lines.Select(line => new { Account = line.AccountId.Value, line.Debit, line.Credit }),
    })));

    private sealed class Transaction(LocalNodeDbContext context, HomeEpochFenceTransaction.Held fence)
        : IKernelPreparedTransaction<JournalEntry, JournalEntry>
    {
        private KernelOperationIdentity? _operation;
        private JournalEntry? _entry;
        private bool _audited;

        public ValueTask StageOperationAsync(KernelOperationIdentity operation, CancellationToken cancellationToken = default)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            return ValueTask.CompletedTask;
        }

        public async ValueTask StageRecordAsync(JournalEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var operation = _operation ?? throw new InvalidOperationException("The operation must be staged before its record.");
            if (operation.CommandId != entry.Id.Value)
                throw new InvalidOperationException("The journal entry does not match its kernel operation identity.");

            if (entry.ReversalOf is { } originalId)
            {
                // The original's Posted -> Reversed transition commits with the reversing entry and its audit row, or
                // none of them does. The read runs under the held write lock, so two reversals cannot both see Posted.
                var original = await context.Set<JournalEntry>()
                    .SingleOrDefaultAsync(e => e.TenantId == entry.TenantId && e.Id == originalId, cancellationToken)
                    .ConfigureAwait(false);
                if (original?.Status != JournalEntryStatus.Posted)
                {
                    throw new JournalEntryNotReversibleException(
                        $"JournalEntry '{originalId.Value}' is {original?.Status.ToString() ?? "absent"}; only a Posted entry can be reversed.");
                }

                var tracked = context.Entry(original);
                tracked.Property(e => e.Status).CurrentValue = JournalEntryStatus.Reversed;
                tracked.Property(e => e.ReversedBy).CurrentValue = entry.Id;
            }

            context.Set<JournalEntry>().Add(entry);
            _entry = entry;
        }

        public ValueTask StageAuditAsync(KernelAuditEvidence audit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audit);
            if (_entry is null) throw new InvalidOperationException("The record must be staged before its audit.");
            _audited = context.ChangeTracker.Entries<NodeAuditEventRow>().Any(row =>
                row.State == EntityState.Added && row.Entity.AuditId == audit.AuditId && row.Entity.Actor == audit.ActorId);
            if (!_audited) throw new InvalidOperationException("The journal audit evidence does not match a staged audit row.");
            return ValueTask.CompletedTask;
        }

        public async ValueTask<JournalEntry> CommitAsync(CancellationToken cancellationToken = default)
        {
            var entry = _entry;
            if (entry is null || !_audited) throw new InvalidOperationException("A complete record and audit set is required.");
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_operation!.IdempotencyKey == entry.SourceReference && NodePersistenceConflict.IsDuplicate(ex))
            {
                // Another commit already holds this operation's idempotency key: the caller replays the first result.
                throw new JournalSourceReferenceConflictException(
                    $"JournalEntry '{entry.Id.Value}': source reference '{entry.SourceReference}' is already posted.", ex);
            }

            await fence.CommitAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) =>
            new(fence.RollbackAsync(cancellationToken));

        public ValueTask DisposeAsync() => fence.DisposeAsync();
    }
}
