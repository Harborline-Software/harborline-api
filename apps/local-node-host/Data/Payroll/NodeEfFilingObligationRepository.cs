using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.Payroll.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// EF Core–backed <see cref="IFilingObligationRepository"/> for the embedded local node
/// (T4 local-first sweep — the payroll node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// Persists filing-obligation rows into the recoverable SQLCipher <c>local-node.db</c> via the
/// node-exclusive <see cref="NodeLocalPayrollDbContext"/>. v1 is the "when is this due" surface only
/// (no submission/calculation — those are v2 category-providers). Uniform-404 on cross-tenant/missing
/// reads; <see cref="ArgumentException"/> on a tenant-mismatched write. Singleton-safe via the injected
/// <see cref="IDbContextFactory{NodeLocalPayrollDbContext}"/>.
/// </remarks>
public sealed class NodeEfFilingObligationRepository : IFilingObligationRepository
{
    private readonly IDbContextFactory<NodeLocalPayrollDbContext> _contextFactory;

    /// <summary>Construct bound to the node-local payroll EF context factory.</summary>
    public NodeEfFilingObligationRepository(IDbContextFactory<NodeLocalPayrollDbContext> contextFactory)
        => _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<FilingObligation?> GetAsync(TenantId tenantId, FilingObligationId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var idValue = id.Value;
        var tenantValue = tenantId.Value;
        var record = await ctx.FilingObligations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == idValue && o.TenantId == tenantValue, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : PayrollRecordMapping.ToDomain(record);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(TenantId tenantId, FilingObligation entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"FilingObligation '{entity.Id.Value}' has TenantId '{entity.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entity));

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var idValue = entity.Id.Value;
        var existing = await ctx.FilingObligations
            .FirstOrDefaultAsync(o => o.Id == idValue, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
            ctx.FilingObligations.Add(PayrollRecordMapping.ToRecord(entity));
        else
            PayrollRecordMapping.CopyInto(entity, existing);

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilingObligation>> ListByDueDateAsync(
        TenantId tenantId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenantValue = tenantId.Value;
        var fromStr = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var toStr = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // due_date is stored as a sortable ISO date string (yyyy-MM-dd), so a lexical range == a date range.
        var records = await ctx.FilingObligations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantValue
                && string.Compare(o.DueDate, fromStr, StringComparison.Ordinal) >= 0
                && string.Compare(o.DueDate, toStr, StringComparison.Ordinal) <= 0)
            .OrderBy(o => o.DueDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(PayrollRecordMapping.ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilingObligation>> ListOutstandingAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenantValue = tenantId.Value;
        var records = await ctx.FilingObligations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantValue && !o.IsComplete)
            .OrderBy(o => o.DueDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(PayrollRecordMapping.ToDomain).ToList();
    }
}
