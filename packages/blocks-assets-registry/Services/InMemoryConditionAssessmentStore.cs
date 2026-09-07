using System.Collections.Concurrent;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="IConditionAssessmentStore"/>. Keyed by <c>(tenant, id)</c>;
/// every record rides the audit log. Condition history is assembled per generic entity ref with an
/// explicit as-of clock.
/// </summary>
public sealed class InMemoryConditionAssessmentStore : IConditionAssessmentStore
{
    private readonly ConcurrentDictionary<(TenantId Tenant, ConditionAssessmentId Id), ConditionAssessment> _store = new();
    private readonly IRegistryAuditLog _audit;

    /// <summary>Creates a store wired to the given audit log.</summary>
    public InMemoryConditionAssessmentStore(IRegistryAuditLog audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public Task RecordAsync(ConditionAssessment assessment, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        RegistryTenantGuard.Require(assessment.TenantId);

        _store[(assessment.TenantId, assessment.Id)] = assessment;
        _audit.Append(assessment.TenantId, assessment.Id.Value, RegistryOp.ConditionAssessed,
            assessment.ObservedAt, actorRef, detail: $"entity={assessment.Entity};grade={assessment.Rating}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ConditionAssessment?> GetByIdAsync(TenantId tenant, ConditionAssessmentId id, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        _store.TryGetValue((tenant, id), out var assessment);
        return Task.FromResult(assessment);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConditionAssessment>> GetHistoryAsync(
        TenantId tenant, RegistryEntityId entity, Instant? asOf = null, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        IReadOnlyList<ConditionAssessment> result = _store.Values
            .Where(a => a.TenantId.Equals(tenant)
                && a.Entity.Equals(entity)
                && (asOf is null || a.ObservedAt.Value <= asOf.Value.Value))
            .OrderBy(a => a.ObservedAt.Value)
            .ThenBy(a => a.Id.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<ConditionAssessment?> GetLatestAsAtAsync(
        TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        var latest = _store.Values
            .Where(a => a.TenantId.Equals(tenant) && a.Entity.Equals(entity) && a.ObservedAt.Value <= asOf.Value)
            .OrderByDescending(a => a.ObservedAt.Value)
            .ThenByDescending(a => a.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }
}
