using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// DES-0029 kernel-core-ck-6: the audit of an authorization or erasure write commits in the SAME transaction as
/// the write. <see cref="StageAuthorized"/> and <see cref="StageSystem"/> add the entry to the caller's context,
/// so the caller's single commit carries both or neither; <see cref="DrainAsync"/> then delivers every owed entry
/// to the kernel audit trail, idempotently by audit id, and a failed delivery stays owed until the next drain.
/// This replaces a post-commit append that could fail after the write had committed and be lost.
/// </summary>
public sealed class NodeAuditOutbox(
    IDbContextFactory<NodeLocalSearchDbContext> factory,
    IAuditTrail trail,
    ICapturedAuditTrail captured,
    IOperationSigner signer,
    TimeProvider time,
    ILogger<NodeAuditOutbox> logger) : IDisposable
{
    // Single-flight: the route's post-write drain and the daemon's tick share this singleton, and the
    // hold-check-then-append below is not atomic, so two overlapping passes could append one entry twice.
    // ponytail: in-process gate; a second host process on the same store would need a row claim instead.
    private readonly SemaphoreSlim _drain = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _drain.Dispose();

    /// <summary>
    /// Stages the audit of an act <paramref name="decision"/> allowed. The decision's authority is captured now,
    /// against the act it decided, so a mismatch refuses the whole write rather than publishing a wrong entry.
    /// </summary>
    /// <returns>The staged entry's audit id.</returns>
    public static Guid StageAuthorized(
        NodeLocalSearchDbContext db,
        AuditEventType eventType,
        AuthorizationDecision decision,
        IReadOnlyDictionary<string, string?> body,
        Guid? auditId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(body);
        var request = decision.Request;
        var snapshot = CapturedAuditAuthority.Capture(
            request.Tenant, request.Principal, request.At, request.Target, request.Act, decision);
        var entry = new Dictionary<string, string?>(body, StringComparer.Ordinal);
        if (request.CorrelationId is { } correlation) entry["correlation_id"] = correlation.ToString("D");
        var id = auditId ?? Guid.NewGuid();
        db.AuditOutbox.Add(new AuditOutboxRow
        {
            AuditId = id.ToString("D"),
            TenantId = request.Tenant.Value,
            EventType = eventType.Value,
            OccurredAt = request.At,
            Nonce = Guid.NewGuid().ToString("D"),
            BodyJson = JsonSerializer.Serialize(entry),
            Actor = request.Principal.Value,
            TargetKind = request.Target.RecordKind,
            TargetId = request.Target.RecordId,
            TargetScope = request.Target.Scope.Value,
            Act = request.Act.ToString(),
            AuthoritySnapshotJson = JsonSerializer.Serialize(snapshot),
        });
        return id;
    }

    /// <summary>Stages the audit of a system act that no request decision authorized (startup repair, pack projection).</summary>
    /// <returns>The staged entry's audit id.</returns>
    public static Guid StageSystem(
        NodeLocalSearchDbContext db,
        AuditEventType eventType,
        TenantId tenant,
        DateTimeOffset occurredAt,
        ActorId? actor,
        IReadOnlyDictionary<string, string?> body,
        Guid? auditId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(body);
        var id = auditId ?? Guid.NewGuid();
        db.AuditOutbox.Add(new AuditOutboxRow
        {
            AuditId = id.ToString("D"),
            TenantId = tenant.Value,
            EventType = eventType.Value,
            OccurredAt = occurredAt,
            Nonce = Guid.NewGuid().ToString("D"),
            BodyJson = JsonSerializer.Serialize(body),
            Actor = actor?.Value,
        });
        return id;
    }

    /// <summary>
    /// T-986: signs <paramref name="body"/> now and stages the signed envelope on <paramref name="write"/>, the
    /// context whose one save commits the change it records. Nothing is saved here; a signing fault throws, so
    /// the change fails with it rather than committing unaudited.
    /// </summary>
    /// <param name="write">The context whose one save commits the change; the entry is added to it, not saved.</param>
    /// <param name="signer">Signs the envelope now; a signing fault throws before anything is staged.</param>
    /// <param name="tenant">The tenant the entry is recorded under.</param>
    /// <param name="eventType">The event the entry records.</param>
    /// <param name="occurredAt">The instant the entry records; with <paramref name="decision"/> it must be the decided instant.</param>
    /// <param name="body">The entry body that is signed and stored.</param>
    /// <param name="decision">
    /// T-1000: the decision that allowed the change, when a request decision allowed it. Its authority is captured
    /// now against the entry's header (<see cref="CapturedAuditAuthority.Capture"/>), and the entry carries its
    /// actor, target and act, so the delivered record is the one an authorized append would have written. A
    /// header the decision does not match throws, so the change fails rather than committing a wrong entry.
    /// </param>
    /// <param name="ct">Cancels the signing.</param>
    /// <returns>The staged entry's audit id.</returns>
    public static async ValueTask<Guid> StageSignedAsync(
        DbContext write,
        IOperationSigner signer,
        TenantId tenant,
        AuditEventType eventType,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, object?> body,
        AuthorizationDecision? decision = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(body);
        var request = decision?.Request;
        var snapshot = request is null
            ? null
            : CapturedAuditAuthority.Capture(tenant, request.Principal, occurredAt, request.Target, request.Act, decision!);
        var nonce = Guid.NewGuid();
        var payload = await signer.SignAsync(new AuditPayload(body), occurredAt, nonce, ct).ConfigureAwait(false);
        var id = Guid.NewGuid();
        write.Set<AuditOutboxRow>().Add(new AuditOutboxRow
        {
            AuditId = id.ToString("D"),
            TenantId = tenant.Value,
            EventType = eventType.Value,
            OccurredAt = occurredAt,
            Nonce = nonce.ToString("D"),
            BodyJson = JsonSerializer.Serialize(body),
            SignedPayloadJson = NodeAuditRecordJson.WritePayload(payload),
            Actor = request?.Principal.Value,
            TargetKind = request?.Target.RecordKind,
            TargetId = request?.Target.RecordId,
            TargetScope = request?.Target.Scope.Value,
            Act = request?.Act.ToString(),
            AuthoritySnapshotJson = snapshot is null ? null : JsonSerializer.Serialize(snapshot),
        });
        return id;
    }

    /// <summary>
    /// T-1048: stages <paramref name="record"/>, already signed, on <paramref name="write"/>, with the authority of
    /// <paramref name="decision"/> captured now. A caller that appends the same record after the commit leaves the
    /// drain only a mark to make; a crash before that append leaves the entry owed, and the drain delivers it.
    /// </summary>
    public static void StageRecord(DbContext write, AuditRecord record, AuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(decision);
        if (record.Actor is not { } actor || record.Target is not { } target || record.Act is not { } act)
            throw new ArgumentException("An authorized audit record names its actor, target and act.", nameof(record));
        var snapshot = CapturedAuditAuthority.Capture(record.TenantId, actor, record.OccurredAt, target, act, decision);
        write.Set<AuditOutboxRow>().Add(new AuditOutboxRow
        {
            AuditId = record.AuditId.ToString("D"),
            TenantId = record.TenantId.Value,
            EventType = record.EventType.Value,
            OccurredAt = record.OccurredAt,
            Nonce = record.Payload.Nonce.ToString("D"),
            BodyJson = JsonSerializer.Serialize(record.Payload.Payload.Body),
            SignedPayloadJson = NodeAuditRecordJson.WritePayload(record.Payload),
            Actor = actor.Value,
            TargetKind = target.RecordKind,
            TargetId = target.RecordId,
            TargetScope = target.Scope.Value,
            Act = act.ToString(),
            AuthoritySnapshotJson = JsonSerializer.Serialize(snapshot),
        });
    }

    /// <summary>
    /// Delivers owed entries by occurrence time, honoring persisted ceremony predecessors. An entry the trail already holds (a crash between
    /// its append and its mark) is marked without a second append. A failed entry records its error and stays owed.
    /// </summary>
    /// <returns>The number of entries delivered or found delivered by this pass.</returns>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        await _drain.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DrainOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _drain.Release();
        }
    }

    private async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        List<AuditOutboxRow> owed;
        await using (var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            owed = await db.AuditOutbox.AsNoTracking()
                .Where(row => row.PublishedAtUnixMs == null)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var delivered = 0;
        var pending = owed.OrderBy(row => row.OccurredAt).ToList();
        while (pending.Count != 0)
        {
            var attempted = false;
            foreach (var row in pending.ToArray())
            {
                try
                {
                    var tenant = new TenantId(row.TenantId);
                    // Equal admission instants do not imply order. A ceremony's dependent entry remains owed
                    // until its persisted predecessor is actually on the trail, including after a failed delivery.
                    if (row.PredecessorAuditId is { } predecessor
                        && !await HoldsAsync(tenant, Guid.Parse(predecessor), ct).ConfigureAwait(false))
                        continue;
                    pending.Remove(row);
                    attempted = true;
                    var auditId = Guid.Parse(row.AuditId);
                    if (!await HoldsAsync(tenant, auditId, ct).ConfigureAwait(false))
                        await AppendAsync(row, tenant, auditId, ct).ConfigureAwait(false);
                    await MarkAsync(row.AuditId, publishedAt: time.GetUtcNow().ToUnixTimeMilliseconds(), error: null, ct)
                        .ConfigureAwait(false);
                    delivered++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    pending.Remove(row);
                    attempted = true;
                    logger.LogError(exception, "Audit outbox entry {AuditId} ({EventType}) is still owed to the audit trail.",
                        row.AuditId, row.EventType);
                    try
                    {
                        await MarkAsync(row.AuditId, publishedAt: null, exception.Message, ct).ConfigureAwait(false);
                    }
                    catch (Exception markFailure) when (markFailure is not OperationCanceledException)
                    {
                        // Recording the failure is best effort: the entry stays owed either way, and throwing here
                        // would end the pass before the entries behind it are attempted.
                        logger.LogError(markFailure, "Audit outbox entry {AuditId} could not record its delivery failure.", row.AuditId);
                    }
                }
            }
            if (!attempted) break;
        }

        return delivered;
    }

    private async Task<bool> HoldsAsync(TenantId tenant, Guid auditId, CancellationToken ct)
    {
        await foreach (var _ in trail.QueryAsync(new AuditQuery(tenant, AuditId: auditId), ct).ConfigureAwait(false))
            return true;
        return false;
    }

    private async Task AppendAsync(AuditOutboxRow row, TenantId tenant, Guid auditId, CancellationToken ct)
    {
        var payload = row.SignedPayloadJson is { } signed
            ? NodeAuditRecordJson.ReadPayload(signed)
            : await SignAsync(row, ct).ConfigureAwait(false);
        var record = new AuditRecord(
            auditId,
            tenant,
            new AuditEventType(row.EventType),
            row.OccurredAt,
            payload,
            [],
            Actor: row.Actor is null ? (ActorId?)null : new ActorId(row.Actor),
            Target: row.TargetKind is null
                ? (AuthorizationTarget?)null
                : new AuthorizationTarget(row.TargetKind, row.TargetId!, ScopeExpression.Parse(row.TargetScope!)),
            Act: row.Act is null ? (PermissionAtom?)null : PermissionAtom.Parse(row.Act));
        if (row.AuthoritySnapshotJson is null)
        {
            await trail.AppendAsync(record, ct).ConfigureAwait(false);
            return;
        }

        var snapshot = JsonSerializer.Deserialize<AuthoritySnapshot>(row.AuthoritySnapshotJson)
            ?? throw new InvalidOperationException("Audit outbox entry has an empty authority snapshot.");
        await captured.AppendCapturedAsync(record with { AuthoritySnapshot = snapshot }, ct).ConfigureAwait(false);
    }

    private async Task<SignedOperation<AuditPayload>> SignAsync(AuditOutboxRow row, CancellationToken ct)
    {
        var body = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.BodyJson)
            ?? throw new InvalidOperationException("Audit outbox entry has no body.");
        return await signer.SignAsync(
            new AuditPayload(body.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)),
            row.OccurredAt, Guid.Parse(row.Nonce), ct).ConfigureAwait(false);
    }

    private async Task MarkAsync(string auditId, long? publishedAt, string? error, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AuditOutbox.Where(row => row.AuditId == auditId).ExecuteUpdateAsync(setters =>
        {
            if (publishedAt is { } at)
                setters.SetProperty(row => row.PublishedAtUnixMs, at).SetProperty(row => row.LastError, (string?)null);
            else
                setters.SetProperty(row => row.Attempts, row => row.Attempts + 1).SetProperty(row => row.LastError, error);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>Drains the audit outbox on an interval, so an entry whose first delivery failed is delivered later.</summary>
public sealed class NodeAuditOutboxDrainDaemon(NodeAuditOutbox outbox, TimeProvider time, ILogger<NodeAuditOutboxDrainDaemon> logger)
    : BackgroundService
{
    /// <summary>The drain interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await outbox.DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Audit outbox drain failed; it will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
