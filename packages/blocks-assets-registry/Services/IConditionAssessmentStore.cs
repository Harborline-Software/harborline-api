using Harborline.Api.Blocks.Assets.Registry.Model;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Tenant-scoped store for <see cref="ConditionAssessment"/> records — the generic condition
/// history keyed off <see cref="RegistryEntityId"/> (A4). History reads take an explicit as-of
/// clock (A5c).
/// </summary>
public interface IConditionAssessmentStore
{
    /// <summary>
    /// Records an assessment, emitting a <see cref="Audit.RegistryOp.ConditionAssessed"/> event at
    /// the assessment's <see cref="ConditionAssessment.ObservedAt"/> instant.
    /// </summary>
    Task RecordAsync(ConditionAssessment assessment, string? actorRef = null, CancellationToken cancellationToken = default);

    /// <summary>Reads an assessment by id within a tenant; null when absent.</summary>
    Task<ConditionAssessment?> GetByIdAsync(TenantId tenant, ConditionAssessmentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The condition history of an entity, ordered by <see cref="ConditionAssessment.ObservedAt"/>.
    /// When <paramref name="asOf"/> is supplied, only assessments observed at or before it are
    /// returned (as-of clock — never ambient now).
    /// </summary>
    Task<IReadOnlyList<ConditionAssessment>> GetHistoryAsync(
        TenantId tenant, RegistryEntityId entity, Instant? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>The most recent assessment for an entity at or before <paramref name="asOf"/>, or null.</summary>
    Task<ConditionAssessment?> GetLatestAsAtAsync(
        TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default);
}
