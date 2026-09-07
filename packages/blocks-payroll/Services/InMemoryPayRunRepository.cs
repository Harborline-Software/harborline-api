using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// In-memory <see cref="IPayRunRepository"/> suitable for v1 testing and demos.
/// Thread-safe for single-process use.
/// </summary>
public sealed class InMemoryPayRunRepository : IPayRunRepository
{
    private readonly List<PayRun> _store = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<PayRun?> GetAsync(
        TenantId tenantId,
        PayRunId id,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _store.FirstOrDefault(r => r.TenantId.Equals(tenantId) && r.Id.Equals(id));
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(
        TenantId tenantId,
        PayRun entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"PayRun '{entity.Id.Value}' has TenantId '{entity.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entity));

        lock (_gate)
        {
            var idx = _store.FindIndex(r => r.TenantId.Equals(tenantId) && r.Id.Equals(entity.Id));
            if (idx >= 0)
                _store[idx] = entity;
            else
                _store.Add(entity);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PayRun>> ListAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<PayRun> result = _store
                .Where(r => r.TenantId.Equals(tenantId))
                .OrderByDescending(r => r.PostingDate)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
