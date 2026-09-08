using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// Tenant-keyed repository for <see cref="BankAccount"/> masters.
/// Per ADR 0092 tenant-keyed repository seam + ADR 0112 Part 1 §1.
/// </summary>
/// <remarks>
/// Default list queries MUST exclude archived accounts (non-null
/// <see cref="BankAccount.ArchivedAt"/>) unless <c>includeArchived</c>
/// is explicitly set — this is the load-bearing invariant from ADR 0108.
/// </remarks>
public interface IBankAccountRepository
{
    /// <summary>Returns a bank account by id (tenant-scoped), or null if not found.</summary>
    Task<BankAccount?> GetByIdAsync(TenantId tenantId, BankAccountId id, CancellationToken ct = default);

    /// <summary>
    /// Returns all bank accounts for the tenant.
    /// Excludes archived accounts by default; pass <c>includeArchived = true</c>
    /// to include them (ADR 0108 default-list exclusion invariant).
    /// </summary>
    Task<IReadOnlyList<BankAccount>> ListAsync(TenantId tenantId, bool includeArchived = false, CancellationToken ct = default);

}

/// <summary>Unregistered raw persistence face held only by the admitted bank-account writer.</summary>
public interface IBankAccountMutationRepository : IBankAccountRepository
{
    /// <summary>Persists a new bank account.</summary>
    Task AddAsync(BankAccount account, CancellationToken ct = default);

    /// <summary>Updates an existing bank account (full record replacement).</summary>
    Task UpdateAsync(BankAccount account, CancellationToken ct = default);
}
