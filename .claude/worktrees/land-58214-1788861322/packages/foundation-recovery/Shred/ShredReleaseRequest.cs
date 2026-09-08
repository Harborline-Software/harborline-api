using System;
using System.Collections.Generic;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// A human request to RELEASE a shred proposal (ADR 0137 §M-3 propose-then-release).
/// Release is the confirm-time, separation-of-duties gate: the actor who PROPOSED /
/// requested the shred must NOT be one of the approvers, and there must be ≥2 distinct
/// approvers. On a valid release the shred executes through the erasure substrate — which
/// independently re-enforces the ≥2-approver floor, the mandatory dwell window, and the
/// fail-closed legal-hold gate before any key is destroyed.
/// </summary>
/// <param name="Tenant">The tenant the subject belongs to.</param>
/// <param name="Subject">The data subject to shred.</param>
/// <param name="Proposer">The actor who proposed/requested the shred (must NOT be an approver).</param>
/// <param name="Approvers">The approval chain — the floor is ≥2 DISTINCT actors, none equal to <see cref="Proposer"/>.</param>
/// <param name="RequestedAt">When the shred was requested; the erasure service enforces the mandatory dwell from here.</param>
/// <param name="LegalBasis">The operator-supplied legal-determination reference for the audit trail.</param>
public sealed record ShredReleaseRequest(
    TenantId Tenant,
    SubjectId Subject,
    ActorId Proposer,
    IReadOnlyList<ActorId> Approvers,
    DateTimeOffset RequestedAt,
    string LegalBasis);
