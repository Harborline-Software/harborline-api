using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// Repository for <see cref="Employee"/> records.
/// All methods are tenant-scoped per ADR 0092 conventions.
/// </summary>
public interface IEmployeeRepository : ITenantScopedRepository<Employee, EmployeeId>
{
    /// <summary>
    /// Get an employee by id. Returns null when missing or scoped to a
    /// different tenant (uniform-404; no diagnostic leak per ADR 0092).
    /// </summary>
    Task<Employee?> GetAsync(
        TenantId tenantId,
        EmployeeId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert or update an employee record.
    /// Throws <see cref="ArgumentException"/> when <c>entity.TenantId</c>
    /// does not match <paramref name="tenantId"/>.
    /// </summary>
    Task UpsertAsync(
        TenantId tenantId,
        Employee entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all active employees for <paramref name="tenantId"/>.
    /// </summary>
    Task<IReadOnlyList<Employee>> ListActiveAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all employees (including inactive) for <paramref name="tenantId"/>.
    /// </summary>
    Task<IReadOnlyList<Employee>> ListAllAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);
}
