using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// EF Core–backed <see cref="IMatchLinkRepository"/> for the embedded local node
/// (T3 local-first sweep — the banking node-flip).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reverse-not-delete.</b> Un-matching transitions the link to
/// <see cref="MatchLinkState.Reversed"/> via <see cref="UpdateAsync"/>; there is no
/// <c>DeleteAsync</c> (ADR 0112 fin-acct C3 + N4).
/// </para>
/// <para>
/// <b>LedgerTransactionRef storage.</b> <see cref="MatchLink.LedgerTransaction"/> is a single-field
/// readonly record struct stored as the bare <c>JournalEntryId</c> string column (per
/// <c>BankingEntityModule</c>); <see cref="ListByLedgerTransactionAsync"/> filters on that column via
/// <see cref="EF.Property{TProperty}(object, string)"/> — mirroring the Bridge repo.
/// </para>
/// <para>
/// <b>Singleton-safe</b> — short-lived context per call from the injected factory.
/// </para>
/// </remarks>
public sealed class NodeEfMatchLinkRepository : IMatchLinkRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfMatchLinkRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<MatchLink?> GetByIdAsync(TenantId tenantId, MatchLinkId id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<MatchLink>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchLink>> ListByStatementLineAsync(
        TenantId tenantId, StatementLineId statementLineId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<MatchLink>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.StatementLine == statementLineId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchLink>> ListByLedgerTransactionAsync(
        TenantId tenantId, LedgerTransactionRef ledgerTransaction, CancellationToken ct = default)
    {
        var jeId = ledgerTransaction.JournalEntryId;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // LedgerTransactionRef is stored as the bare "JournalEntryId" string column (BankingEntityModule).
        return await ctx.Set<MatchLink>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && EF.Property<string>(m, "JournalEntryId") == jeId.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(MatchLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<MatchLink>().Add(link);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(MatchLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.Set<MatchLink>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == link.Id, ct)
            .ConfigureAwait(false);

        if (existing is null)
            throw new InvalidOperationException($"MatchLink '{link.Id.Value}' not found for update.");

        if (!existing.TenantId.Equals(link.TenantId))
            throw new ArgumentException(
                $"MatchLink id '{link.Id.Value}' belongs to a different tenant.",
                nameof(link));

        ctx.Set<MatchLink>().Update(link);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
