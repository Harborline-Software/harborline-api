using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// EF Core–backed <see cref="IStatementLineRepository"/> for the embedded local node
/// (T3 local-first sweep — the banking node-flip).
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-and-correct.</b> Statement lines are never hard-deleted; a mis-imported line is
/// corrected via <see cref="UpdateAsync"/> to <see cref="ReconciliationState.Excluded"/>. There is
/// no <c>DeleteAsync</c> on the interface.
/// </para>
/// <para>
/// <b>RawProviderBlob / credential handling (ADR 0112 sec-eng C2).</b> <c>RawProviderBlob</c> is an
/// opaque sealed column; this repository NEVER emits it in log messages, exception detail, or error
/// projections. On the node it lives inside the SQLCipher <c>local-node.db</c> (encrypted at rest —
/// stronger than the Bridge plaintext-column posture).
/// </para>
/// <para>
/// <b>Dedup-key queries.</b> <see cref="FindByProviderTxnIdAsync"/> checks
/// <c>(AccountId, ProviderTxnId)</c>; <see cref="FindByBatchOrdinalAsync"/> /
/// <see cref="ListByBatchAsync"/> filter on the JSON-stored <see cref="ImportSourceRef"/> in memory
/// (EF cannot translate the value-object struct into a JSON path query — same posture as the Bridge
/// repo). On the single-device node these candidate sets are small.
/// </para>
/// <para>
/// <b>Singleton-safe</b> — short-lived context per call from the injected factory.
/// </para>
/// </remarks>
public sealed class NodeEfStatementLineRepository : IStatementLineRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfStatementLineRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<StatementLine?> GetByIdAsync(TenantId tenantId, StatementLineId id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<StatementLine>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StatementLine>> ListByAccountAsync(
        TenantId tenantId, BankAccountId accountId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<StatementLine>()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.AccountId == accountId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StatementLine>> ListByBatchAsync(
        TenantId tenantId, string batchId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Source (ImportSourceRef) is JSON-stored; filter by tenant at the DB then refine on
        // Source.BatchId in memory (EF cannot translate the struct property into a JSON path query).
        var lines = await ctx.Set<StatementLine>()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return lines.Where(s => s.Source.BatchId == batchId).ToList();
    }

    /// <inheritdoc />
    public async Task<StatementLine?> FindByProviderTxnIdAsync(
        TenantId tenantId, BankAccountId accountId, string providerTxnId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<StatementLine>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                     && s.AccountId == accountId
                     && s.ProviderTxnId == providerTxnId,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StatementLine?> FindByBatchOrdinalAsync(
        TenantId tenantId, BankAccountId accountId, string batchId, int ordinal, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var candidates = await ctx.Set<StatementLine>()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.AccountId == accountId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return candidates
            .FirstOrDefault(s => s.Source.BatchId == batchId && s.Source.OrdinalWithinBatch == ordinal);
    }

    /// <inheritdoc />
    public async Task AddAsync(StatementLine line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<StatementLine>().Add(line);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IEnumerable<StatementLine> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<StatementLine>().AddRange(lines);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(StatementLine line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.Set<StatementLine>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == line.Id, ct)
            .ConfigureAwait(false);

        if (existing is null)
            throw new InvalidOperationException($"StatementLine '{line.Id.Value}' not found for update.");

        if (!existing.TenantId.Equals(line.TenantId))
            throw new ArgumentException(
                $"StatementLine id '{line.Id.Value}' belongs to a different tenant.",
                nameof(line));

        ctx.Set<StatementLine>().Update(line);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
