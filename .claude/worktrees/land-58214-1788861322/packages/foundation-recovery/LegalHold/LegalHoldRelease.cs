using System;
using System.Collections.Immutable;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// The append-only record that ENDS a <see cref="LegalHoldEntry"/> (ADR 0142 §D1
/// / §D2). Releasing a hold is the dangerous, guarded action — it re-exposes the
/// held subject to crypto-shred — so it carries the ≥2 distinct approvers
/// (ADR 0068 §3.1 floor) and the reason under which the hold was lifted.
/// </summary>
/// <param name="HoldId">The hold this release ends.</param>
/// <param name="TenantId">The tenant the hold is scoped to.</param>
/// <param name="Approvers">The ≥2 distinct actors who approved the release.</param>
/// <param name="Reason">Why the hold was released.</param>
/// <param name="ReleasedAtUtc">When the hold was released.</param>
public sealed record LegalHoldRelease(
    LegalHoldId HoldId,
    TenantId TenantId,
    ImmutableArray<ActorId> Approvers,
    string Reason,
    DateTimeOffset ReleasedAtUtc);

/// <summary>The derived lifecycle state of a <see cref="LegalHoldEntry"/>.</summary>
public enum LegalHoldStatus
{
    /// <summary>The hold has been placed and not yet released — it blocks shred.</summary>
    Active,

    /// <summary>The hold has been released by an authorized ≥2-approver act — it no longer blocks shred.</summary>
    Released,
}
