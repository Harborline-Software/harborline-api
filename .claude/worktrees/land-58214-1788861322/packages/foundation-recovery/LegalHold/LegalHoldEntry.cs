using System;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// An append-only legal-hold record (ADR 0142 §D1). A hold is placed against a
/// <see cref="HeldRef"/> — a data subject or a coarser record/class scope — scoped
/// to a tenant, carrying the matter it serves and full who/when provenance.
/// </summary>
/// <remarks>
/// A hold has <b>no expiry</b>: it persists indefinitely until an authorized
/// <see cref="LegalHoldRelease"/> (ADR 0137 §M-4 "hold-wins, indefinitely"). The
/// registry is append-only — a release is a <em>new</em> record, never a mutation
/// or delete of the hold — mirroring the erasure registry's no-un-erase discipline
/// and preserving the discovery audit trail. <see cref="LegalHoldStatus"/> is
/// therefore <em>derived</em> from the presence of a release, not stored here.
/// </remarks>
/// <param name="HoldId">Stable id; a release references the hold by this id.</param>
/// <param name="TenantId">The tenant the hold is scoped to.</param>
/// <param name="HeldRef">What the hold covers (subject / record / class).</param>
/// <param name="Matter">The litigation / investigation / audit matter the hold serves.</param>
/// <param name="PlacedBy">The single authorized actor who placed the hold (placement is low-privilege; see <see cref="ILegalHoldService"/>).</param>
/// <param name="PlacedAtUtc">When the hold was placed.</param>
public sealed record LegalHoldEntry(
    LegalHoldId HoldId,
    TenantId TenantId,
    HeldRef HeldRef,
    string Matter,
    ActorId PlacedBy,
    DateTimeOffset PlacedAtUtc);
