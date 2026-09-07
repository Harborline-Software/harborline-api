using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// EF Core–backed <see cref="IBankAccountRepository"/> for the embedded local node
/// (T3 local-first sweep — the banking node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns banking data.</b> Before T3 the node carried the four banking tables
/// (contributed by <c>BankingEntityModule</c> into <see cref="LocalNodeDbContext"/>; in the
/// node <c>InitialSchema</c> migration) but had NO repository registration and no endpoints — so
/// banking was Bridge-dependent. This repo (mirroring the Bridge
/// <c>SignalBridge.Data.Banking.EfBankAccountRepository</c>) gives the node a read/write surface
/// over the SAME recoverable SQLCipher store (<c>local-node.db</c>, the C1-durable source of truth)
/// the journal store + payments + bills already use, so a single-device install no longer needs
/// signal-bridge for banking.
/// </para>
/// <para>
/// <b>Mirrors the Bridge EF repo contract</b>, simplified for the single-device node:
/// uniform-404 on cross-tenant / missing reads (via the explicit <c>WHERE TenantId</c>),
/// <see cref="ArgumentException"/> on a tenant-mismatched write, tombstone-not-delete (a closed
/// account is archived via <see cref="BankAccount.ArchivedAt"/>, ADR 0108). Audit emission is the
/// durable-layer concern (ADR 0104 §7 X-AUDIT) and is deliberately deferred — matching the node JE
/// store + bill repo: the row's presence in the keyed SQLCipher store is the audit record. The
/// cross-tenant <c>TenantBoundaryViolation</c> audit the Bridge emits is unnecessary on the
/// single-tenant loopback node.
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> <see cref="LocalNodeDbContext"/> applies NO ambient tenant
/// query filter, so every read carries an explicit <c>WHERE TenantId = @t</c> (defence-in-depth)
/// and every write asserts <c>account.TenantId == tenantId</c>. The routes pass the active-team-derived
/// tenant (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>; ADR 0032 identity
/// layer), so switching the active org switches which org's rows are visible — the explicit
/// <c>WHERE TenantId</c> is the per-org isolation predicate, no longer a fixed <c>"local"</c> sentinel.
/// </para>
/// <para>
/// <b>Default-list exclusion invariant (ADR 0108).</b> <see cref="ListAsync"/> excludes archived
/// accounts (non-null <see cref="BankAccount.ArchivedAt"/>) unless <c>includeArchived</c> is set.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/> (mirrors <see cref="Financial.NodeEfBillRepository"/>) —
/// the ambient scoped context may already be disposed at call time, so the repo never holds one.
/// </para>
/// </remarks>
public sealed class NodeEfBankAccountRepository : IBankAccountRepository, IBankAccountMutationRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfBankAccountRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<BankAccount?> GetByIdAsync(TenantId tenantId, BankAccountId id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Uniform-404: missing OR foreign-tenant both return null via the same WHERE.
        return await ctx.Set<BankAccount>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BankAccount>> ListAsync(
        TenantId tenantId, bool includeArchived = false, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = ctx.Set<BankAccount>()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId);

        if (!includeArchived)
            query = query.Where(a => a.ArchivedAt == null);

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(BankAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<BankAccount>().Add(account);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(BankAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.Set<BankAccount>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == account.Id, ct)
            .ConfigureAwait(false);

        if (existing is null)
            throw new InvalidOperationException($"BankAccount '{account.Id.Value}' not found for update.");

        if (!existing.TenantId.Equals(account.TenantId))
            throw new ArgumentException(
                $"BankAccount id '{account.Id.Value}' already exists under a different tenant.",
                nameof(account));

        ctx.Set<BankAccount>().Update(account);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
