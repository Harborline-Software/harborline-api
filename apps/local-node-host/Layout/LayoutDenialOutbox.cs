using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Layout;
using Harborline.Blocks.LayoutRuntime;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>One denial recorded at the act: its id (also the gate-log audit id), tenant, instant and fields.</summary>
public sealed record LayoutDenialOutboxEntry(Guid Id, TenantId Tenant, DateTimeOffset OccurredAt, LayoutRelatedDenial Denial);

/// <summary>
/// T-731, owner ruling 2 of 2026-09-24: the durable local outbox Layout denials are written to at the act,
/// on the form-submit outbox pattern (<see cref="Data.Forms.NodeEfFormSubmitOutbox"/>). A denial leaves the
/// outbox only when its signed record is in the gate log.
/// </summary>
public sealed class NodeEfLayoutDenialOutbox(IDbContextFactory<LocalNodeDbContext> contextFactory)
{
    /// <summary>Records <paramref name="entry"/> as <see cref="LayoutDenialOutboxState.Pending"/>.</summary>
    public async Task EnqueueAsync(LayoutDenialOutboxEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Set<LayoutDenialOutboxRow>().Add(new LayoutDenialOutboxRow
        {
            EntryId = entry.Id,
            TenantId = entry.Tenant.Value,
            OccurredAt = entry.OccurredAt,
            DenialJson = JsonSerializer.Serialize(entry.Denial),
            State = LayoutDenialOutboxState.Pending,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks an entry appended.</summary>
    public Task MarkAppendedAsync(Guid entryId, CancellationToken ct = default)
        => UpdateAsync(entryId, setters => setters
            .SetProperty(row => row.State, LayoutDenialOutboxState.Appended)
            .SetProperty(row => row.LastError, (string?)null), ct);

    /// <summary>Marks an entry failed, counting the attempt.</summary>
    public Task MarkFailedAsync(Guid entryId, string error, CancellationToken ct = default)
        => UpdateAsync(entryId, setters => setters
            .SetProperty(row => row.State, LayoutDenialOutboxState.Failed)
            .SetProperty(row => row.Attempts, row => row.Attempts + 1)
            .SetProperty(row => row.LastError, error), ct);

    /// <summary>Every entry not yet appended, oldest first, across tenants (a system recovery sweep).</summary>
    public async Task<IReadOnlyList<(LayoutDenialOutboxEntry Entry, LayoutDenialOutboxState State)>> ListUnresolvedAsync(
        CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Set<LayoutDenialOutboxRow>().AsNoTracking()
            .Where(row => row.State != LayoutDenialOutboxState.Appended)
            .OrderBy(row => row.Sequence)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => (new LayoutDenialOutboxEntry(row.EntryId, new TenantId(row.TenantId), row.OccurredAt,
            JsonSerializer.Deserialize<LayoutRelatedDenial>(row.DenialJson)!), row.State)).ToArray();
    }

    private async Task UpdateAsync(
        Guid entryId,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<LayoutDenialOutboxRow>> setters,
        CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.Set<LayoutDenialOutboxRow>().Where(row => row.EntryId == entryId)
                .ExecuteUpdateAsync(setters, ct).ConfigureAwait(false) == 0)
            throw new InvalidOperationException($"Layout denial outbox entry '{entryId}' does not exist.");
    }
}

/// <summary>
/// The in-process alarm state behind <see cref="LayoutDenialHealthCheck"/>: the faults that are not visible
/// in the outbox itself (a denial the outbox could not take, a resolution that overran its floor).
/// </summary>
public sealed class LayoutDenialAlarms
{
    private long _outboxWriteFailures;
    private long _floorOverruns;

    /// <summary>Denials lost because the outbox write itself failed since the process started.</summary>
    public long OutboxWriteFailures => Interlocked.Read(ref _outboxWriteFailures);

    /// <summary>Resolutions that finished after the response floor since the process started.</summary>
    public long FloorOverruns => Interlocked.Read(ref _floorOverruns);

    internal void OutboxWriteFailed() => Interlocked.Increment(ref _outboxWriteFailures);

    internal void FloorOverrun() => Interlocked.Increment(ref _floorOverruns);
}

/// <summary>
/// NIST SP 800-53 AU-5 for Layout denials (T-731): Degraded while any denial has failed its gate-log append
/// and not yet been retried into it, when an outbox write lost a denial, or when a resolution overran its
/// timing floor. T-735 registers it on the host's health-check chain.
/// </summary>
public sealed class LayoutDenialHealthCheck(NodeEfLayoutDenialOutbox outbox, LayoutDenialAlarms alarms) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failed = (await outbox.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false))
            .Count(item => item.State == LayoutDenialOutboxState.Failed);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["failedAppends"] = failed,
            ["outboxWriteFailures"] = alarms.OutboxWriteFailures,
            ["floorOverruns"] = alarms.FloorOverruns,
        };
        return failed == 0 && alarms.OutboxWriteFailures == 0 && alarms.FloorOverruns == 0
            ? HealthCheckResult.Healthy("Every Layout denial is in the gate log and no resolution overran its floor.", data)
            : HealthCheckResult.Degraded(
                $"Layout denials: {failed} failed gate-log append(s), {alarms.OutboxWriteFailures} lost outbox write(s), "
                + $"{alarms.FloorOverruns} timing-floor overrun(s).", data: data);
    }
}

/// <summary>
/// Moves Layout denials from the outbox into the authorization gate log as signed
/// <see cref="LayoutDenialGateLog.LayoutRelatedDeniedEventType"/> records. Idempotent: the record's audit id
/// is the entry id, so a retry after a crash between append and mark never appends twice. Never throws.
/// </summary>
public sealed class LayoutDenialAppender(
    NodeEfLayoutDenialOutbox outbox, IAuditTrail trail, IOperationSigner signer, ILogger logger)
{
    private static readonly AuthorizationOperation RecordsRead = AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead);
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _inFlight = new();

    /// <summary>Appends one entry, or joins the append already running for it.</summary>
    public Task AppendAsync(LayoutDenialOutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var run = _inFlight.GetOrAdd(entry.Id, _ => new Lazy<Task>(() => RunAsync(entry)));
        return run.Value;
    }

    /// <summary>Completes every append now running. Tests and the drain wait on it.</summary>
    public Task IdleAsync() => Task.WhenAll(_inFlight.Values.Select(run => run.Value));

    /// <summary>Retries every denial not yet in the gate log. T-735 schedules it, as the form-submit
    /// reconciler daemon schedules its sweep.</summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        foreach (var (entry, _) in await outbox.ListUnresolvedAsync(ct).ConfigureAwait(false))
            await AppendAsync(entry).ConfigureAwait(false);
    }

    private async Task RunAsync(LayoutDenialOutboxEntry entry)
    {
        await Task.Yield();
        try
        {
            if (!await AlreadyAppendedAsync(entry).ConfigureAwait(false))
            {
                var denial = entry.Denial;
                // The denied act, typed: reading the target record, by the acting principal, at the act.
                var request = new AuthorizationWriteContext(new ActorId(denial.PrincipalId), entry.Tenant, entry.OccurredAt)
                    .Request(RecordsRead, AuthorizationGate.RecordKindFor(RecordsRead), denial.Target.RecordId);
                var body = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestId"] = denial.RequestId,
                    ["principalId"] = denial.PrincipalId,
                    ["blockId"] = denial.BlockId,
                    ["bindingKind"] = denial.BindingKind,
                    ["relationshipKey"] = denial.RelationshipKey,
                    ["targetRecordTypeId"] = denial.Target.RecordTypeId,
                    ["targetRecordId"] = denial.Target.RecordId,
                    ["code"] = denial.Code,
                    ["pointer"] = denial.Pointer,
                };
                var payload = await signer.SignAsync(new AuditPayload(body), entry.OccurredAt, entry.Id).ConfigureAwait(false);
                await trail.AppendAsync(new AuditRecord(
                    entry.Id, entry.Tenant, LayoutDenialGateLog.LayoutRelatedDeniedEventType, entry.OccurredAt, payload, [],
                    Actor: request.Principal, Target: request.Target, Act: request.Act)).ConfigureAwait(false);
            }
            await outbox.MarkAppendedAsync(entry.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AU-5: the entry stays in the outbox as Failed, which LayoutDenialHealthCheck reports until a
            // drain appends it. Nothing is thrown: the caller already answered.
            logger.LogError(ex, "Layout denial gate-log append FAILED for outbox entry {EntryId}; it stays in the outbox for retry.", entry.Id);
            try
            {
                await outbox.MarkFailedAsync(entry.Id, ex.Message).ConfigureAwait(false);
            }
            catch (Exception markFault) when (markFault is not OperationCanceledException)
            {
                logger.LogError(markFault, "Layout denial outbox entry {EntryId} could not be marked failed; it stays Pending for the drain.", entry.Id);
            }
        }
        finally
        {
            _inFlight.TryRemove(entry.Id, out _);
        }
    }

    private async Task<bool> AlreadyAppendedAsync(LayoutDenialOutboxEntry entry)
    {
        await foreach (var record in trail.QueryAsync(new AuditQuery(entry.Tenant, AuditId: entry.Id)).ConfigureAwait(false))
            if (record.AuditId == entry.Id) return true;
        return false;
    }
}
