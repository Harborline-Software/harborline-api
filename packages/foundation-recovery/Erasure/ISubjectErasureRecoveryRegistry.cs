using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// T-1048 (DES-0029 ck-6): the approval evidence an interrupted erasure needs to finish without its request: the
/// approvers and legal basis the <c>SubjectErased</c> audit records, and the instant the approved erasure took
/// effect (the tombstone's and the audit's instant). The approver ids are personal data.
/// </summary>
/// <param name="ApprovingActors">The approvers the request carried.</param>
/// <param name="LegalBasis">The operator-supplied legal basis the request carried.</param>
/// <param name="ApprovedAt">The instant the approved erasure took effect.</param>
public sealed record SubjectErasureEvidence(
    IReadOnlyList<ActorId> ApprovingActors,
    string LegalBasis,
    DateTimeOffset ApprovedAt);

/// <summary>An erasure whose registry mark committed but whose audit is not yet secured.</summary>
/// <param name="Tenant">The subject's tenant.</param>
/// <param name="Subject">The erased subject.</param>
/// <param name="Evidence">The evidence the mark recorded.</param>
public sealed record InterruptedSubjectErasure(TenantId Tenant, SubjectId Subject, SubjectErasureEvidence Evidence);

/// <summary>
/// T-1048: a durable registry that records each erasure's approval evidence with its mark, so a host recovery
/// pass finishes an interrupted erasure without a client retry. The evidence is cleared only by
/// <see cref="CompleteAsync"/>, in the same commit that durably secures the <c>SubjectErased</c> audit.
/// </summary>
public interface ISubjectErasureRecoveryRegistry : ISubjectErasureRegistry
{
    /// <summary>
    /// Marks the subject erased and records <paramref name="evidence"/> in the same commit. The mark is dated with
    /// <see cref="SubjectErasureEvidence.ApprovedAt"/>, the instant the approved erasure took effect.
    /// </summary>
    /// <returns><c>true</c> when this call performed the mark; <c>false</c> when the subject was already marked.</returns>
    ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, SubjectErasureEvidence evidence, CancellationToken ct = default);

    /// <summary>The approval evidence the subject's mark recorded, or <c>null</c> when it recorded none or was completed.</summary>
    ValueTask<SubjectErasureEvidence?> FindEvidenceAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default);

    /// <summary>Whether the subject's erasure completed: its audit secured and its evidence cleared.</summary>
    ValueTask<bool> IsCompletedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default);

    /// <summary>
    /// Durably secures <paramref name="erasedAudit"/> for delivery to the trail and clears the subject's evidence,
    /// in one commit, dated <paramref name="completedAt"/> (the completing pass's instant: a registry holds no clock,
    /// T-1057). Idempotent: an audit already secured under the same id is not secured twice.
    /// </summary>
    ValueTask CompleteAsync(SubjectId subject, AuditRecord erasedAudit, DateTimeOffset completedAt, CancellationToken ct = default);

    /// <summary>The interrupted erasures due for recovery at <paramref name="now"/>, oldest first, at most <paramref name="limit"/>.</summary>
    ValueTask<IReadOnlyList<InterruptedSubjectErasure>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>Records a failed recovery attempt and defers the next one with backoff. The evidence is kept.</summary>
    ValueTask DeferAsync(TenantId tenant, SubjectId subject, DateTimeOffset now, CancellationToken ct = default);
}
