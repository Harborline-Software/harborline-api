using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// In-memory <see cref="IEmployeeRepository"/> suitable for v1 testing and demos.
/// Thread-safe for single-process use.
/// </summary>
public sealed class InMemoryEmployeeRepository : IEmployeeRepository
{
    private readonly List<Employee> _store = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<Employee?> GetAsync(
        TenantId tenantId,
        EmployeeId id,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _store.FirstOrDefault(e => e.TenantId.Equals(tenantId) && e.Id.Equals(id));
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(
        TenantId tenantId,
        Employee entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"Employee '{entity.Id.Value}' has TenantId '{entity.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entity));

        lock (_gate)
        {
            var idx = _store.FindIndex(e => e.TenantId.Equals(tenantId) && e.Id.Equals(entity.Id));
            if (idx >= 0)
                _store[idx] = entity;
            else
                _store.Add(entity);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Employee>> ListActiveAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Employee> result = _store
                .Where(e => e.TenantId.Equals(tenantId) && e.IsActive)
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Employee>> ListAllAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Employee> result = _store
                .Where(e => e.TenantId.Equals(tenantId))
                .ToList();
            return Task.FromResult(result);
        }
    }
}
