using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.Payroll.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// EF Core–backed <see cref="IEmployeeRepository"/> for the embedded local node
/// (T4 local-first sweep — the payroll node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns payroll data.</b> Payroll had NO durable persistence anywhere before T4 —
/// the block ships only <c>InMemory*</c> repos and the Bridge wires <c>AddInMemoryPayroll()</c>. This
/// repo (over a node-exclusive <see cref="NodeLocalPayrollDbContext"/> on the recoverable SQLCipher
/// <c>local-node.db</c>) gives payroll its first durable home, so a single-device install no longer
/// needs signal-bridge for payroll (and the data survives a restart).
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> Every read carries an explicit <c>WHERE tenant_id = @t</c>
/// (uniform-404: missing OR foreign-tenant both return null), every write asserts
/// <c>entity.TenantId == tenantId</c>. The routes pass the active-team-derived tenant
/// (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), so
/// the explicit <c>WHERE tenant_id</c> is the per-org isolation predicate, no fixed <c>"local"</c>
/// sentinel. Audit emission is the durable-layer concern (the row's presence in the keyed
/// store is the record) — the cross-tenant <c>TenantBoundaryViolation</c> audit the Bridge emits is
/// unnecessary on the single-tenant loopback node (matching the node banking / bill / invoice repos).
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{NodeLocalPayrollDbContext}"/> (mirrors the node banking / bill repos).
/// </para>
/// </remarks>
public sealed class NodeEfEmployeeRepository : IEmployeeRepository
{
    private readonly IDbContextFactory<NodeLocalPayrollDbContext> _contextFactory;

    /// <summary>Construct bound to the node-local payroll EF context factory.</summary>
    public NodeEfEmployeeRepository(IDbContextFactory<NodeLocalPayrollDbContext> contextFactory)
        => _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<Employee?> GetAsync(TenantId tenantId, EmployeeId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var idValue = id.Value;
        var tenantValue = tenantId.Value;
        var record = await ctx.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == idValue && e.TenantId == tenantValue, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : PayrollRecordMapping.ToDomain(record);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(TenantId tenantId, Employee entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"Employee '{entity.Id.Value}' has TenantId '{entity.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entity));

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var idValue = entity.Id.Value;
        var existing = await ctx.Employees
            .FirstOrDefaultAsync(e => e.Id == idValue, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            ctx.Employees.Add(PayrollRecordMapping.ToRecord(entity));
        }
        else
        {
            PayrollRecordMapping.CopyInto(entity, existing);
        }

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Employee>> ListActiveAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenantValue = tenantId.Value;
        var records = await ctx.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantValue && e.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(PayrollRecordMapping.ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Employee>> ListAllAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenantValue = tenantId.Value;
        var records = await ctx.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantValue)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(PayrollRecordMapping.ToDomain).ToList();
    }
}
