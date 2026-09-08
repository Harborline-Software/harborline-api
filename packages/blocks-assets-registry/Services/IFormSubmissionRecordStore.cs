using Harborline.Api.Blocks.Assets.Registry.Model;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Tenant-scoped store for <see cref="FormSubmissionRecord"/> links — the generic "forms submitted into
/// this record" history keyed off <see cref="RegistryEntityId"/> (#144 runtime form-fill). Mirrors the
/// shape of <see cref="IConditionAssessmentStore"/> (audited, tenant-scoped, deterministic-id upsert).
/// </summary>
public interface IFormSubmissionRecordStore
{
    /// <summary>
    /// Records (upserts) a submission link, emitting a
    /// <see cref="Audit.RegistryOp.FormSubmissionRecorded"/> event at the record's
    /// <see cref="FormSubmissionRecord.SubmittedAt"/> instant. The record's deterministic id makes a
    /// replay (an at-least-once outbox re-run) upsert the same row rather than duplicating it.
    /// </summary>
    Task RecordAsync(FormSubmissionRecord record, string? actorRef = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every submission link for an entity, most-recent first (by
    /// <see cref="FormSubmissionRecord.SubmittedAt"/>). Tenant-scoped; the sentinel tenant is rejected.
    /// </summary>
    Task<IReadOnlyList<FormSubmissionRecord>> ListForEntityAsync(
        TenantId tenant, RegistryEntityId entity, CancellationToken cancellationToken = default);
}
