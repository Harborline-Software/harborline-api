using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>A recorded guard refusal before a gate decision; no four-step decision was made.</summary>
    PreDecisionRefusal = 3,
}

/// <summary>The public reason recorded by a pre-decision guard, without its classified diagnostic.</summary>
public sealed record AuthorizationPreDecisionRefusal(string Code, string Detail, string Remediation);

/// <summary>
/// The answer to "why was this decided that way?" for ONE recorded decision: the four ordered public steps
/// and the counterfactual, exactly as they were stored with the audit entry.
/// </summary>
public sealed record AuthorizationTraceRead(
    AuthorizationTraceAvailability Availability,
    int? Version,
    IReadOnlyList<AuthorityTraceStepSnapshot> Steps,
    AuthorityCounterfactualSnapshot? Counterfactual,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AuthorizationPreDecisionRefusal? Refusal = null);

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
        => (await ReadWithDecisionAsync(tenant, caller, auditId, at, ct).ConfigureAwait(false)).Read;

    /// <summary>Returns the same read and its gate decision for HTTP refusal rendering and audit.</summary>
    public async ValueTask<(AuthorizationTraceRead Read, AuthorizationDecision Decision)> ReadWithDecisionAsync(
        TenantId tenant, ActorId caller, Guid auditId, DateTimeOffset at, CancellationToken ct = default)
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
            return (new AuthorizationTraceRead(AuthorizationTraceAvailability.Refused, null, [], null), decision);

        // Read the signed guard report only after audit:read succeeds. It is pre-decision evidence,
        // so preserve that distinction instead of inventing a four-step authorization decision.
        if (entry?.EventType.Value == "AuthorizationRefused"
            && entry.Payload.Payload.Body.TryGetValue("preDecisionRefusal", out var stored) && stored is not null)
        {
            var value = JsonSerializer.SerializeToElement(stored);
            if (value.ValueKind == JsonValueKind.Object
                && TryReadReportString(value, nameof(AuthorizationPreDecisionRefusal.Code), out var code)
                && TryReadReportString(value, nameof(AuthorizationPreDecisionRefusal.Detail), out var detail)
                && TryReadReportString(value, nameof(AuthorizationPreDecisionRefusal.Remediation), out var remedy))
                return (new AuthorizationTraceRead(AuthorizationTraceAvailability.PreDecisionRefusal,
                    null, [], null, new(code, detail, remedy)), decision);
        }

        // Keyed by ORDINAL, not by stage: an entry whose act also went through the separation-of-duty
        // engine stores that decision's four steps under ordinals 5..8 with the same four stage names, and
        // this read answers for the gate decision the entry records.
        var steps = (snapshot?.Trace ?? [])
            .Where(step => step.Ordinal is >= 1 and <= AuthorizationDecisionEvidence.StepCount)
            .OrderBy(step => step.Ordinal)
            .ToArray();
        return (steps.Length == AuthorizationDecisionEvidence.StepCount
            ? new AuthorizationTraceRead(
                AuthorizationTraceAvailability.Available,
                snapshot!.TraceVersion,
                steps,
                snapshot.Counterfactual)
            : new AuthorizationTraceRead(AuthorizationTraceAvailability.NotAvailable, null, [], null), decision);
    }

    // These single-word fields differ only in case under the audit writers' default, camelCase,
    // and snake_case policies. Read the stored spelling without changing the signed payload.
    private static bool TryReadReportString(JsonElement value, string name, out string text)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                text = property.Value.GetString()!;
                return true;
            }
        }
        text = string.Empty;
        return false;
    }

    // Ticket 331 slice 2: the query names the one entry, so the trail answers by its own index
    // (InMemoryAuditTrail) or its own filter rather than handing this read the whole trail to scan — this
    // route is reachable over HTTP by anyone the gate admits.
    private async ValueTask<AuditRecord?> FindAsync(TenantId tenant, Guid auditId, CancellationToken ct)
    {
        await foreach (var record in _trail
            .QueryAsync(new AuditQuery(tenant, AuditId: auditId), ct).ConfigureAwait(false))
        {
            if (record.AuditId == auditId) return record;
        }

        return null;
    }
}
