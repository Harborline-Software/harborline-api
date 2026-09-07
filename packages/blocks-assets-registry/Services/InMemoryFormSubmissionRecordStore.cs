using System.Collections.Concurrent;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="IFormSubmissionRecordStore"/>. Keyed by <c>(tenant, id)</c>; every
/// record rides the audit log. Submission history is assembled per generic entity ref, most-recent first.
/// Mirrors <see cref="InMemoryConditionAssessmentStore"/>.
/// </summary>
public sealed class InMemoryFormSubmissionRecordStore : IFormSubmissionRecordStore
{
    private readonly ConcurrentDictionary<(TenantId Tenant, FormSubmissionRecordId Id), FormSubmissionRecord> _store = new();
    private readonly IRegistryAuditLog _audit;

    /// <summary>Creates a store wired to the given audit log.</summary>
    public InMemoryFormSubmissionRecordStore(IRegistryAuditLog audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public Task RecordAsync(FormSubmissionRecord record, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        RegistryTenantGuard.Require(record.TenantId);

        _store[(record.TenantId, record.Id)] = record;
        _audit.Append(record.TenantId, record.Id.Value, RegistryOp.FormSubmissionRecorded,
            record.SubmittedAt, actorRef, detail: $"entity={record.Entity};form={record.FormId};instance={record.InstanceId}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FormSubmissionRecord>> ListForEntityAsync(
        TenantId tenant, RegistryEntityId entity, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        IReadOnlyList<FormSubmissionRecord> result = _store.Values
            .Where(r => r.TenantId.Equals(tenant) && r.Entity.Equals(entity))
            .OrderByDescending(r => r.SubmittedAt.Value)
            .ThenBy(r => r.Id.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }
}
