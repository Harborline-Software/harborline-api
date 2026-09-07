using System.Text.Json;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Governance.Consent;

/// <summary>
/// The record-backed <see cref="IConsentGate"/> and the writer of its lifecycle transitions. One type,
/// because the reading and the writing must agree about what "effective" means: the same
/// <see cref="TenantConsentRecord.StateAt"/> reading decides the act and dates the transition.
/// </summary>
/// <remarks>
/// No process-local state lives here: every decision is a fresh read of <see cref="ITenantConsentStore"/>,
/// so the answer survives a restart and is unchanged by one. Every transition persists and then appends its
/// audit row, in that order. An audit-log failure AFTER a persisted transition is a known gap — the record
/// is durable and its row is missing — and closing it needs an outbox, not a different ordering: appending
/// first would leave an audit row for a transition that never persisted, which is the worse lie.
/// </remarks>
public sealed class TenantConsentGate(ITenantConsentStore store, IAuditLog audit) : IConsentGate
{
    private readonly ITenantConsentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAuditLog _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    /// <inheritdoc />
    public async ValueTask<ConsentDecision> DecideAsync(ConsentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var all = await _store.ReadAsync(request.Tenant, ct).ConfigureAwait(false);
        if (all.Count == 0) return new ConsentDecision(request, ConsentRefusal.NoRecord, null);

        // Narrow one axis at a time so the refusal names the axis that failed rather than a bare "no".
        var bySubject = all.Where(r => r.Subject == request.Subject).ToArray();
        if (bySubject.Length == 0) return new ConsentDecision(request, ConsentRefusal.SubjectMismatch, null);

        var byPurpose = bySubject
            .Where(r => string.Equals(r.Purpose, request.Purpose, StringComparison.Ordinal)).ToArray();
        if (byPurpose.Length == 0) return new ConsentDecision(request, ConsentRefusal.PurposeMismatch, null);

        var inScope = byPurpose.Where(r => r.Scope.Contains(request.Scope)).ToArray();
        if (inScope.Length == 0)
            return new ConsentDecision(request, ConsentRefusal.OutOfScope, byPurpose[0].Id);

        // An allow needs only ONE active record; a refusal reports the most recently requested near-miss.
        var active = inScope.FirstOrDefault(r => r.StateAt(request.At) == ConsentLifecycleState.Active);
        if (active is not null) return new ConsentDecision(request, ConsentRefusal.None, active.Id);

        var nearest = inScope.OrderByDescending(r => r.RequestedAt).First();
        return new ConsentDecision(request, Refused(nearest.StateAt(request.At)), nearest.Id);
    }

    private static ConsentRefusal Refused(ConsentLifecycleState state) => state switch
    {
        ConsentLifecycleState.Expired => ConsentRefusal.Expired,
        ConsentLifecycleState.Revoked => ConsentRefusal.Revoked,
        _ => ConsentRefusal.NotActive,
    };

    /// <summary>Writes a newly requested record. Audited as the lifecycle's first transition.</summary>
    public Task<TenantConsentRecord> RequestAsync(
        TenantConsentRecord record, ActorId actor, CancellationToken ct = default) =>
        TransitionedAsync(Required(record), actor, "requested", record.RequestedAt, ct);

    /// <summary>requested -&gt; active. Refused before persistence when the record is not requested.</summary>
    public Task<TenantConsentRecord> ActivateAsync(
        TenantConsentRecord record, ActorId actor, DateTimeOffset at, CancellationToken ct = default) =>
        TransitionedAsync(Required(record).Activate(at), actor, "activated", at, ct);

    /// <summary>active -&gt; expired.</summary>
    public Task<TenantConsentRecord> ExpireAsync(
        TenantConsentRecord record, ActorId actor, DateTimeOffset at, CancellationToken ct = default) =>
        TransitionedAsync(Required(record).Expire(at), actor, "expired", at, ct);

    /// <summary>active -&gt; revoked.</summary>
    public Task<TenantConsentRecord> RevokeAsync(
        TenantConsentRecord record, ActorId actor, DateTimeOffset at, CancellationToken ct = default) =>
        TransitionedAsync(Required(record).Revoke(at), actor, "revoked", at, ct);

    private static TenantConsentRecord Required(TenantConsentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record;
    }

    private async Task<TenantConsentRecord> TransitionedAsync(
        TenantConsentRecord next, ActorId actor, string transition, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);
        await _store.SaveAsync(next, ct).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToDocument(new
        {
            transition,
            state = next.State.ToString(),
            subject = next.Subject.Value,
            purpose = next.Purpose,
            scope = next.Scope.Value,
            effectiveFrom = next.EffectiveFrom,
            effectiveUntil = next.EffectiveUntil,
            revokedAt = next.RevokedAt,
            signatureConsentRecordId = next.SignatureConsentRecordId,
        });
        await _audit.AppendAsync(
            new AuditAppend(
                new EntityId("consent", next.Tenant.Value, next.Id),
                VersionId: null, Op.Write, actor, next.Tenant, at, payload,
                Justification: $"consent {transition}"),
            ct).ConfigureAwait(false);
        return next;
    }
}
