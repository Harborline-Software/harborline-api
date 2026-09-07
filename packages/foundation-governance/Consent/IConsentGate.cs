using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Governance.Consent;

/// <summary>Why a subject-consent act was refused, or <see cref="None"/> when it was allowed.</summary>
public enum ConsentRefusal
{
    /// <summary>Allowed — an active record matched.</summary>
    None = 0,
    /// <summary>No consent record exists for this tenant at all.</summary>
    NoRecord = 1,
    /// <summary>Records exist, but none for this subject.</summary>
    SubjectMismatch = 2,
    /// <summary>Records exist for the subject, but none for this purpose.</summary>
    PurposeMismatch = 3,
    /// <summary>A matching record exists but its scope does not cover the act.</summary>
    OutOfScope = 4,
    /// <summary>The matching record's effective window has closed.</summary>
    Expired = 5,
    /// <summary>The subject withdrew the matching record.</summary>
    Revoked = 6,
    /// <summary>The matching record was requested and never activated (or its window has not opened yet).</summary>
    NotActive = 7,
}

/// <summary>One subject-consent act: who, for what, over which scope, at which instant.</summary>
public sealed record ConsentRequest(
    TenantId Tenant,
    SubjectId Subject,
    string Purpose,
    ScopeExpression Scope,
    DateTimeOffset At);

/// <summary>
/// The single decision a consent check produces, carried end to end. A refusal names the record it was
/// decided against when there was one, so the caller records the SAME decision it refused on.
/// </summary>
public sealed record ConsentDecision(ConsentRequest Request, ConsentRefusal Refusal, string? RecordId)
{
    /// <summary>Whether the act may proceed.</summary>
    public bool Allowed => Refusal == ConsentRefusal.None;
}

/// <summary>
/// The point-of-use gate predicate for subject-consent acts (ticket 213, ledger L646). It reads the
/// EFFECTIVE tenant consent record at the moment of the act — like every other gate in the model, at the
/// point of use rather than from an ambient bit set earlier (ticket 205's posture).
/// </summary>
/// <remarks>
/// Deliberately takes no <c>HttpContext</c> and no route: a subject-consent act is not a route act. The
/// enforcer calls it directly and so may any other caller; the only inputs are the act's own four values
/// and the instant it happens.
/// </remarks>
public interface IConsentGate
{
    /// <summary>Decide <paramref name="request"/> against the tenant's effective consent records.</summary>
    ValueTask<ConsentDecision> DecideAsync(ConsentRequest request, CancellationToken ct = default);
}

/// <summary>
/// The tenant's consent records, durably. A read returns every record for the tenant — the gate does the
/// matching, so "a record exists but for another subject" stays distinguishable from "no record at all".
/// </summary>
public interface ITenantConsentStore
{
    /// <summary>Every consent record on file for <paramref name="tenant"/>.</summary>
    ValueTask<IReadOnlyList<TenantConsentRecord>> ReadAsync(TenantId tenant, CancellationToken ct = default);

    /// <summary>Writes <paramref name="record"/>, replacing any prior version of the same id.</summary>
    ValueTask SaveAsync(TenantConsentRecord record, CancellationToken ct = default);

    /// <summary>
    /// Every tenant with at least one record on file. It exists for the expiry sweep, which has no request
    /// to take a tenant from: a background pass over "what is on file" needs the store to say whose records
    /// those are. A reader that wants ONE tenant's records still uses <see cref="ReadAsync"/>.
    /// </summary>
    ValueTask<IReadOnlyList<TenantId>> TenantsAsync(CancellationToken ct = default);
}
