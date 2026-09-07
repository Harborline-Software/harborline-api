using System;
using System.Collections.Generic;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// A manual, legal-sign-off-gated request to crypto-shred a data subject (ADR
/// 0135 GDPR direction; inherits ADR 0068 §1.3's append-only-audit +
/// legal-sign-off + manual-operator discipline). There is deliberately NO public
/// "delete" API — erasure is an operator action that passes through
/// <see cref="ISubjectErasureService"/>, which enforces the multi-actor approval
/// floor and the mandatory-minimum dwell window before the shred takes effect.
/// </summary>
/// <param name="Tenant">The tenant the subject belongs to.</param>
/// <param name="Subject">The data subject to erase.</param>
/// <param name="RequestedAt">When the erasure request was filed. The service enforces a mandatory-minimum dwell between this and execution (the ADR 0068 §1.3 minimum window) so an erasure can never be a same-instant, single-step action.</param>
/// <param name="ApprovingActors">The approval chain. Per ADR 0068 §3 the floor is ≥2 DISTINCT actors with no self-approval; the service rejects a request that does not satisfy it.</param>
/// <param name="LegalBasis">The deployer's legal-determination reference (e.g. erasure-request ticket id). Harborline does not make the legal determination (ADR 0068 §GC.1 / §5.1) — it records the operator-supplied basis for the audit.</param>
public sealed record SubjectErasureRequest(
    TenantId Tenant,
    SubjectId Subject,
    DateTimeOffset RequestedAt,
    IReadOnlyList<ActorId> ApprovingActors,
    string LegalBasis);
