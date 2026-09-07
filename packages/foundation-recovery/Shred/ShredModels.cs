using System;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// A subject whose retention window the caller has resolved (through the retention
/// cascade — base → pack → tenant policy, per record-class/data-class), presented to
/// the propose-only scheduler for shred-eligibility evaluation.
/// </summary>
/// <remarks>
/// <see cref="RetainedUntil"/> and <see cref="Disposition"/> are the OUTPUT of the
/// retention cascade (<c>IRetentionPolicyResolver</c> + the per-class disposition),
/// resolved by the caller/composition root. The scheduler stays free of any
/// security-policy dependency: it reasons purely over the resolved verdict + the
/// legal-hold gate.
/// </remarks>
/// <param name="Tenant">The tenant the subject belongs to.</param>
/// <param name="Subject">The data subject (the crypto-shred unit).</param>
/// <param name="RetainedUntil">The instant the subject's retention floor lapses (from the cascade).</param>
/// <param name="Disposition">What the retention rule prescribes once the floor lapses.</param>
public sealed record ShredCandidate(
    TenantId Tenant,
    SubjectId Subject,
    DateTimeOffset RetainedUntil,
    RetentionDisposition Disposition);

/// <summary>
/// A propose-only shred recommendation awaiting a human RELEASE (ADR 0137 §D8 /
/// §M-3). A proposal destroys nothing — it names a subject whose retention floor has
/// lapsed, whose disposition is <see cref="RetentionDisposition.Shred"/>, and that is
/// under no active legal hold. A ≥2-approver release (proposer ≠ approver) is required
/// to execute it via the erasure substrate.
/// </summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Subject">The data subject.</param>
/// <param name="Reason">Why the subject is eligible (human-readable).</param>
public sealed record ShredProposal(TenantId Tenant, SubjectId Subject, string Reason);
