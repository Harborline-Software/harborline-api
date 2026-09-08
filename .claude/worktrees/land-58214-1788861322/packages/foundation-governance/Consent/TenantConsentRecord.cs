using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Governance.Consent;

/// <summary>
/// The ordinary tenant consent record's lifecycle (ticket 213, ledger L646). Four states and one
/// direction: <c>requested -&gt; active -&gt; expired | revoked</c>. <see cref="Expired"/> and
/// <see cref="Revoked"/> are terminal — consent is never un-revoked, it is re-requested as a NEW record,
/// which is what keeps the trail of who consented to what and when intact.
/// </summary>
public enum ConsentLifecycleState
{
    /// <summary>Asked for, not yet given. Authorizes nothing.</summary>
    Requested = 0,
    /// <summary>Given and in force within its effective dates.</summary>
    Active = 1,
    /// <summary>Its effective window closed.</summary>
    Expired = 2,
    /// <summary>Withdrawn by the subject before the window closed.</summary>
    Revoked = 3,
}

/// <summary>Thrown when a lifecycle transition is refused — BEFORE anything is persisted.</summary>
public sealed class ConsentLifecycleTransitionException(ConsentLifecycleState from, ConsentLifecycleState to)
    : Exception($"A consent record cannot move from {from} to {to}.")
{
    /// <summary>The state the record is in.</summary>
    public ConsentLifecycleState From { get; } = from;

    /// <summary>The state the caller asked for.</summary>
    public ConsentLifecycleState To { get; } = to;
}

/// <summary>
/// One tenant consent record: this <see cref="Subject"/> consented to this <see cref="Purpose"/> over this
/// <see cref="Scope"/>, for this window. It is a LIFECYCLE RECORD, not a grant — it confers no role and no
/// operation; it only removes the subject-consent objection to an act the authorization gate has already
/// allowed (glossary: <em>Grant</em> is the row that gives a principal a role; this is not one).
/// </summary>
/// <remarks>
/// <see cref="SignatureConsentRecordId"/> is the reconciliation with the signature-specific
/// <c>Harborline.Api.Kernel.Signatures.Models.ConsentRecord</c> (ADR 0054's UETA/E-SIGN evidence): the
/// tenant record may NAME that record, by id, as the evidence it was founded on. The naming is one-way and
/// by reference — the legal record keeps its own dates, its own revocation and its verbatim affirmation
/// text, and nothing here reads or weakens it.
/// </remarks>
public sealed record TenantConsentRecord
{
    /// <summary>Stable id, unique within the tenant.</summary>
    public required string Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required TenantId Tenant { get; init; }

    /// <summary>The data subject whose consent this is.</summary>
    public required SubjectId Subject { get; init; }

    /// <summary>The purpose consented to. Compared exactly; a near-miss is a refusal, never a match.</summary>
    public required string Purpose { get; init; }

    /// <summary>The scope the consent covers. An act outside it is refused even for the right purpose.</summary>
    public required ScopeExpression Scope { get; init; }

    /// <summary>Current lifecycle state as last written.</summary>
    public required ConsentLifecycleState State { get; init; }

    /// <summary>When the record was requested.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When consent became effective; null while merely requested.</summary>
    public DateTimeOffset? EffectiveFrom { get; init; }

    /// <summary>When the window closes; null means open-ended.</summary>
    public DateTimeOffset? EffectiveUntil { get; init; }

    /// <summary>When the subject withdrew consent; null unless <see cref="ConsentLifecycleState.Revoked"/>.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The signature-specific consent record this was founded on, by id. Evidence, not authority.</summary>
    public string? SignatureConsentRecordId { get; init; }

    /// <summary>A newly requested record. Nothing about it authorizes anything yet.</summary>
    public static TenantConsentRecord Request(
        string id,
        TenantId tenant,
        SubjectId subject,
        string purpose,
        ScopeExpression scope,
        DateTimeOffset at,
        DateTimeOffset? effectiveUntil = null,
        string? signatureConsentRecordId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(scope);
        return new TenantConsentRecord
        {
            Id = id,
            Tenant = tenant,
            Subject = subject,
            Purpose = purpose,
            Scope = scope,
            State = ConsentLifecycleState.Requested,
            RequestedAt = at,
            EffectiveUntil = effectiveUntil,
            SignatureConsentRecordId = signatureConsentRecordId,
        };
    }

    /// <summary>requested -&gt; active. Any other origin is refused before persistence.</summary>
    public TenantConsentRecord Activate(DateTimeOffset at) =>
        Moved(ConsentLifecycleState.Active) with { EffectiveFrom = at };

    /// <summary>active -&gt; expired.</summary>
    public TenantConsentRecord Expire(DateTimeOffset at) =>
        Moved(ConsentLifecycleState.Expired) with { EffectiveUntil = EffectiveUntil ?? at };

    /// <summary>active -&gt; revoked.</summary>
    public TenantConsentRecord Revoke(DateTimeOffset at) =>
        Moved(ConsentLifecycleState.Revoked) with { RevokedAt = at };

    /// <summary>
    /// The state the record is ACTUALLY in at <paramref name="at"/>. A stored <c>active</c> row whose window
    /// has closed reads as <see cref="ConsentLifecycleState.Expired"/> without anyone having run a sweep, and
    /// one whose window has not opened reads as <see cref="ConsentLifecycleState.Requested"/> — so the gate's
    /// answer never depends on a background job having got there first.
    /// </summary>
    public ConsentLifecycleState StateAt(DateTimeOffset at)
    {
        if (State != ConsentLifecycleState.Active) return State;
        if (EffectiveUntil is { } until && at >= until) return ConsentLifecycleState.Expired;
        if (EffectiveFrom is { } from && at < from) return ConsentLifecycleState.Requested;
        return ConsentLifecycleState.Active;
    }

    private TenantConsentRecord Moved(ConsentLifecycleState to)
    {
        var permitted = (State, to) switch
        {
            (ConsentLifecycleState.Requested, ConsentLifecycleState.Active) => true,
            (ConsentLifecycleState.Active, ConsentLifecycleState.Expired) => true,
            (ConsentLifecycleState.Active, ConsentLifecycleState.Revoked) => true,
            _ => false,
        };
        if (!permitted) throw new ConsentLifecycleTransitionException(State, to);
        return this with { State = to };
    }
}
