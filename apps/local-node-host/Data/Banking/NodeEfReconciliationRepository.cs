using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// EF Core–backed <see cref="IReconciliationRepository"/> for the embedded local node
/// (T3 local-first sweep — the banking node-flip).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reverse-not-delete.</b> Un-reconciling a locked reconciliation is an explicit reversing action
/// with provenance via <see cref="UpdateAsync"/>; there is no <c>DeleteAsync</c>.
/// </para>
/// <para>
/// <b>Bank-rec lock INDEPENDENT of fiscal-period status (ADR 0112 fin-acct C1).</b> This repo
/// persists <see cref="Reconciliation.LockState"/> as its own column and does NOT consult
/// <c>FiscalPeriodStatus</c> — period-state gating is the service layer's responsibility.
/// </para>
/// <para>
/// <b>Uniqueness invariant.</b> One reconciliation per <c>(TenantId, AccountId, PeriodId)</c> —
/// enforced by the unique index in <c>BankingEntityModule</c>.
/// </para>
/// <para>
/// <b>Singleton-safe</b> — short-lived context per call from the injected factory.
/// </para>
/// </remarks>
public sealed class NodeEfReconciliationRepository : IReconciliationRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfReconciliationRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<Reconciliation?> GetByIdAsync(TenantId tenantId, ReconciliationId id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<Reconciliation>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Reconciliation?> GetByAccountPeriodAsync(
        TenantId tenantId, BankAccountId accountId, FiscalPeriodId periodId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<Reconciliation>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId && r.AccountId == accountId && r.PeriodId == periodId,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Reconciliation>> ListByAccountAsync(
        TenantId tenantId, BankAccountId accountId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<Reconciliation>()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.AccountId == accountId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Reconciliation reconciliation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<Reconciliation>().Add(reconciliation);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Reconciliation reconciliation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.Set<Reconciliation>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reconciliation.Id, ct)
            .ConfigureAwait(false);

        if (existing is null)
            return false;

        if (!existing.TenantId.Equals(reconciliation.TenantId))
            throw new ArgumentException(
                $"Reconciliation id '{reconciliation.Id.Value}' belongs to a different tenant.",
                nameof(reconciliation));

        if (reconciliation.Version != existing.Version + 1)
            return false;

        var entry = ctx.Set<Reconciliation>().Attach(reconciliation);
        entry.State = EntityState.Modified;
        entry.Property(r => r.Version).OriginalValue = reconciliation.Version - 1;

        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
