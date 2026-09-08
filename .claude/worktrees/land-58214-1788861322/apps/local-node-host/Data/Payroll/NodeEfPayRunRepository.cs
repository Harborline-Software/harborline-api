using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.Payroll.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// EF Core–backed <see cref="IPayRunRepository"/> for the embedded local node
/// (T4 local-first sweep — the payroll node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// Persists pay runs + their lines into the recoverable SQLCipher <c>local-node.db</c> via the
/// node-exclusive <see cref="NodeLocalPayrollDbContext"/>. The <see cref="PayRunPostingService"/>
/// (reused verbatim from the block) calls <see cref="UpsertAsync"/> to persist a posted/reversed run;
/// the post path also writes the balanced journal entry through the node <c>IJournalPostingService</c>
/// (recoverable <c>NodeEfJournalStore</c>) — the GL side of the pay-run post is therefore in the SAME
/// recoverable store, never a seed-keyed KV (SC-4).
/// </para>
/// <para>
/// <b>Line collection upsert.</b> A pay run owns an ordered <c>PayRunLine</c> collection. On upsert the
/// existing child line rows are deleted and re-inserted from the domain run (delete-then-insert) so a
/// re-supplied line set round-trips faithfully — the line rows have no stable domain identity.
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> Uniform-404 on cross-tenant/missing reads (explicit
/// <c>WHERE tenant_id</c>); <see cref="ArgumentException"/> on a tenant-mismatched write. The routes pass
/// the active-team-derived tenant (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>;
/// ADR 0032 identity layer), not a fixed <c>"local"</c> sentinel — the <c>WHERE tenant_id</c> is the
/// per-org isolation predicate. Singleton-safe via the
/// injected <see cref="IDbContextFactory{NodeLocalPayrollDbContext}"/>.
/// </para>
/// </remarks>
public sealed class NodeEfPayRunRepository : IPayRunRepository
{
    private readonly IDbContextFactory<NodeLocalPayrollDbContext> _contextFactory;

    /// <summary>Construct bound to the node-local payroll EF context factory.</summary>
    public NodeEfPayRunRepository(IDbContextFactory<NodeLocalPayrollDbContext> contextFactory)
        => _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<PayRun?> GetAsync(TenantId tenantId, PayRunId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var idValue = id.Value;
        var tenantValue = tenantId.Value;
        var record = await ctx.PayRuns
            .AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == idValue && r.TenantId == tenantValue, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : PayrollRecordMapping.ToDomain(record);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(TenantId tenantId, PayRun entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"PayRun '{entity.Id.Value}' has TenantId '{entity.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entity));

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var idValue = entity.Id.Value;
        var existing = await ctx.PayRuns
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == idValue, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var record = new PayRunRecord { Id = entity.Id.Value };
            PayrollRecordMapping.CopyInto(entity, record);
            record.Lines = PayrollRecordMapping.ToLineRecords(entity);
            ctx.PayRuns.Add(record);
        }
        else
        {
            PayrollRecordMapping.CopyInto(entity, existing);
            // Replace the line collection (delete-then-insert) — lines have no stable domain id.
            ctx.PayRunLines.RemoveRange(existing.Lines);
            existing.Lines = PayrollRecordMapping.ToLineRecords(entity);
        }

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PayRun>> ListAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenantValue = tenantId.Value;
        var records = await ctx.PayRuns
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => r.TenantId == tenantValue)
            .OrderByDescending(r => r.PostingDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(PayrollRecordMapping.ToDomain).ToList();
    }
}
