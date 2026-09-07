using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// Append-and-correct repository for <see cref="StatementLine"/> entities.
/// Per ADR 0092 tenant-keyed seam + ADR 0112 Part 1 §2.
/// </summary>
/// <remarks>
/// <para>
/// Statement lines are <strong>append-and-correct, not deletable</strong>.
/// There is no <c>DeleteAsync</c> — a mis-imported line is corrected via
/// <see cref="UpdateAsync"/> to set <see cref="ReconciliationState.Excluded"/>.
/// </para>
/// <para>
/// <strong>Dedup responsibility:</strong> callers MUST check for existing lines
/// before calling <see cref="AddAsync"/> or <see cref="AddRangeAsync"/>.
/// The dedup key logic (provider id vs content hash + batch ordinal per
/// ADR 0112 fin-acct N1) lives in the import pipeline, not this repository.
/// </para>
/// </remarks>
public interface IStatementLineRepository
{
    /// <summary>Returns a statement line by id (tenant-scoped), or null if not found.</summary>
    Task<StatementLine?> GetByIdAsync(TenantId tenantId, StatementLineId id, CancellationToken ct = default);

    /// <summary>Returns all lines for a given account (tenant-scoped).</summary>
    Task<IReadOnlyList<StatementLine>> ListByAccountAsync(
        TenantId tenantId, BankAccountId accountId, CancellationToken ct = default);

    /// <summary>Returns all lines for a given import batch (tenant-scoped).</summary>
    Task<IReadOnlyList<StatementLine>> ListByBatchAsync(
        TenantId tenantId, string batchId, CancellationToken ct = default);

    /// <summary>
    /// Returns any existing line matching the provider dedup key
    /// <c>(accountId, providerTxnId)</c> (tenant-scoped).
    /// Returns null if no match.
    /// </summary>
    Task<StatementLine?> FindByProviderTxnIdAsync(
        TenantId tenantId, BankAccountId accountId, string providerTxnId, CancellationToken ct = default);

    /// <summary>
    /// Returns any existing line matching the file-import content-hash dedup key
    /// <c>(accountId, batchId, ordinalWithinBatch)</c> (tenant-scoped).
    /// Used to prevent re-import duplicates while preserving intra-statement identical lines.
    /// Returns null if no match (ADR 0112 fin-acct N1).
    /// </summary>
    Task<StatementLine?> FindByBatchOrdinalAsync(
        TenantId tenantId, BankAccountId accountId, string batchId, int ordinal, CancellationToken ct = default);

    /// <summary>Appends a new statement line.</summary>
    Task AddAsync(StatementLine line, CancellationToken ct = default);

    /// <summary>Appends a batch of new statement lines.</summary>
    Task AddRangeAsync(IEnumerable<StatementLine> lines, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing statement line (state transitions: e.g. Unmatched → Excluded).
    /// Never removes lines; callers use state transitions.
    /// </summary>
    Task UpdateAsync(StatementLine line, CancellationToken ct = default);
}
