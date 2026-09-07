using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// Reverse-not-delete repository for <see cref="MatchLink"/> entities.
/// Per ADR 0092 tenant-keyed seam + ADR 0112 fin-acct C3.
/// </summary>
/// <remarks>
/// Match links are <strong>reverse-not-delete</strong>. Un-matching transitions
/// the link to <see cref="MatchLinkState.Reversed"/> via <see cref="UpdateAsync"/>;
/// there is no <c>DeleteAsync</c>. No reversal provenance is persisted — ADR 0112
/// fin-acct N4 requires it and it is UNIMPLEMENTED (earlier repository ticket #3465).
/// </remarks>
public interface IMatchLinkRepository
{
    /// <summary>Returns a match link by id (tenant-scoped), or null if not found.</summary>
    Task<MatchLink?> GetByIdAsync(TenantId tenantId, MatchLinkId id, CancellationToken ct = default);

    /// <summary>
    /// Returns all match links for a given statement line (tenant-scoped).
    /// Includes all states (Proposed, Accepted, Reversed) so callers can compute
    /// reconciliation state and sum of accepted amounts.
    /// </summary>
    Task<IReadOnlyList<MatchLink>> ListByStatementLineAsync(
        TenantId tenantId, StatementLineId statementLineId, CancellationToken ct = default);

    /// <summary>
    /// Returns all match links for a given ledger transaction (tenant-scoped).
    /// Includes all states so callers can determine which statement lines
    /// reference a particular journal entry.
    /// </summary>
    Task<IReadOnlyList<MatchLink>> ListByLedgerTransactionAsync(
        TenantId tenantId, LedgerTransactionRef ledgerTransaction, CancellationToken ct = default);

    /// <summary>Persists a new match link (typically in Proposed state).</summary>
    Task AddAsync(MatchLink link, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing match link (state transitions: Proposed → Accepted or Reversed).
    /// Never removes links; callers use Reversed state.
    /// </summary>
    Task UpdateAsync(MatchLink link, CancellationToken ct = default);
}
