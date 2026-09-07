using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Kernel.Audit;

/// <summary>Whether a trace could be handed to this caller, and if not, why not.</summary>
public enum AuthorizationTraceAvailability
{
    /// <summary>The stored trace is returned.</summary>
    Available = 0,

    /// <summary>The caller may read this entry, but the entry carries no evidence — it is either not an
    /// authorized act or it predates ticket 212. Not an error: the answer is "this is not recorded".</summary>
    NotAvailable = 1,

    /// <summary>The gate refused the read. Nothing about the entry is disclosed, its existence included.</summary>
    Refused = 2,
}

/// <summary>
/// The answer to "why was this decided that way?" for ONE recorded decision: the four ordered public steps
/// and the counterfactual, exactly as they were stored with the audit entry.
/// </summary>
public sealed record AuthorizationTraceRead(
    AuthorizationTraceAvailability Availability,
    int? Version,
    IReadOnlyList<AuthorityTraceStepSnapshot> Steps,
    AuthorityCounterfactualSnapshot? Counterfactual);

/// <summary>
/// The authorized first-class production read of ticket 212's four-step trace (ledger L651/L652), and the
/// surface ticket 163's "Why can I do this?" question is answered from.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never re-evaluates.</b> Every value it returns was projected and derived by the deciding path and
/// frozen into the audit entry's authority snapshot at append time (ADR 0068 clause 1). This class reads
/// that snapshot and nothing else: no gate replay, no closure read, no clock beyond the instant the caller
/// dates its own read act with. A grant revoked after the fact therefore cannot change the answer.
/// </para>
/// <para>
/// <b>It is production, not debug.</b> There is no flag to turn it on, no header, no log channel; a caller
/// with no route reaches it directly. And it returns only the stored step facts and the counterfactual
/// text: never a payload, an attesting signature, a key or a token.
/// </para>
/// <para>
/// <b>Who may read.</b> Two acts, resolved at the point of use through the gate (ticket 205), against the
/// audit entry the read addresses:
/// <list type="bullet">
///   <item>The person the decision was ABOUT reads their own — <c>audit:trace-read</c>. 163 asks the
///     question in the first person, so a member holds it.</item>
///   <item>Anyone else reads it as what it is, a read of the audit trail about another person —
///     <c>audit:read</c>, which on a sealed platform Auditor is that role's single capability (ticket
///     217). No second capability is offered to the Auditor, and none is needed.</item>
/// </list>
/// The entry is located before the act is chosen, because which of the two applies depends on whose
/// decision it was; but a caller who holds neither is refused identically whether the entry exists or not,
/// so the refusal discloses nothing.
/// </para>
/// </remarks>
public sealed class AuthorizationTraceReader(IAuditTrail trail, AuthorizationGate gate)
{
    private readonly IAuditTrail _trail = trail ?? throw new ArgumentNullException(nameof(trail));
    private readonly AuthorizationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>Reads the stored trace of <paramref name="auditId"/> for <paramref name="caller"/>.</summary>
    public async ValueTask<AuthorizationTraceRead> ReadAsync(
        TenantId tenant,
        ActorId caller,
        Guid auditId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var entry = await FindAsync(tenant, auditId, ct).ConfigureAwait(false);
        var snapshot = entry?.AuthoritySnapshot;
        var ownDecision = snapshot?.Principal is { } subject
            && ActorId.SameActor(subject, caller.Value);
        var operation = ownDecision ? Permission.AuditTraceRead : Permission.AuditRead;

        var request = new AuthorizationWriteContext(caller, tenant, at).Request(
            AuthorizationOperation.Parse(operation),
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(operation)),
            auditId.ToString());
        var decision = await _gate.DecideAsync(request, ct).ConfigureAwait(false);
        if (decision.Verdict is not AuthorizationVerdict.Allowed)
            return new AuthorizationTraceRead(AuthorizationTraceAvailability.Refused, null, [], null);

        // Keyed by ORDINAL, not by stage: an entry whose act also went through the separation-of-duty
        // engine stores that decision's four steps under ordinals 5..8 with the same four stage names, and
        // this read answers for the gate decision the entry records.
        var steps = (snapshot?.Trace ?? [])
            .Where(step => step.Ordinal is >= 1 and <= AuthorizationDecisionEvidence.StepCount)
            .OrderBy(step => step.Ordinal)
            .ToArray();
        return steps.Length == AuthorizationDecisionEvidence.StepCount
            ? new AuthorizationTraceRead(
                AuthorizationTraceAvailability.Available,
                snapshot!.TraceVersion,
                steps,
                snapshot.Counterfactual)
            : new AuthorizationTraceRead(AuthorizationTraceAvailability.NotAvailable, null, [], null);
    }

    // ponytail: a linear scan of the tenant's trail — IAuditTrail has no by-id query and the node-local
    // trail is one install's. Add an AuditId filter to AuditQuery if a trail ever gets large enough to
    // notice.
    private async ValueTask<AuditRecord?> FindAsync(TenantId tenant, Guid auditId, CancellationToken ct)
    {
        await foreach (var record in _trail.QueryAsync(new AuditQuery(tenant), ct).ConfigureAwait(false))
        {
            if (record.AuditId == auditId) return record;
        }

        return null;
    }
}
