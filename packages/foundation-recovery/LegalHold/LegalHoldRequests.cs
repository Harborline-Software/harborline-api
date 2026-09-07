using System;
using System.Collections.Generic;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// A request to PLACE a legal hold (ADR 0142 §D2). Placement is conservative and
/// low-privilege — a single authorized actor places a hold immediately, because
/// over-holding is the safe failure direction and a litigation hold often must land
/// fast. The guard is on release, not placement.
/// </summary>
/// <param name="Tenant">The tenant the hold is scoped to.</param>
/// <param name="HeldRef">What the hold covers (subject / record / class).</param>
/// <param name="Matter">The litigation / investigation / audit matter the hold serves.</param>
/// <param name="PlacedBy">The authorized actor placing the hold.</param>
public sealed record LegalHoldPlaceRequest(
    TenantId Tenant,
    HeldRef HeldRef,
    string Matter,
    ActorId PlacedBy);

/// <summary>
/// A request to RELEASE a legal hold (ADR 0142 §D2). Release is the guarded action:
/// it requires ≥2 distinct approvers (reusing the ADR 0068 §3.1 multi-actor floor the
/// erasure service applies) plus a reason, because release re-exposes the held data
/// to crypto-shred.
/// </summary>
/// <param name="Tenant">The tenant the hold is scoped to.</param>
/// <param name="HoldId">The hold to release.</param>
/// <param name="Approvers">The approval chain — the floor is ≥2 DISTINCT actors.</param>
/// <param name="Reason">Why the hold is being released.</param>
public sealed record LegalHoldReleaseRequest(
    TenantId Tenant,
    LegalHoldId HoldId,
    IReadOnlyList<ActorId> Approvers,
    string Reason);
