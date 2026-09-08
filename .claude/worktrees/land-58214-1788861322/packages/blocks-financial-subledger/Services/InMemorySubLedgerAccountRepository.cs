using System.Collections.Concurrent;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialSubLedger.Services;

/// <summary>
/// In-memory <see cref="ISubLedgerAccountRepository"/>. Thread-safe via
/// <c>ConcurrentDictionary</c> keyed on the composite <c>(TenantId, SubLedgerAccountId)</c>
/// pair (matching the financial-cluster posture — ADR 0092 / ADR 0120).
///
/// <para>
/// <b>Tenant isolation (MANDATORY — PR-B SPOT-CHECK requirement):</b>
/// every read method filters by the <c>tenantId</c> argument before
/// returning. Cross-tenant id-guessing returns null / empty (uniform-404).
/// Write methods assert <c>account.TenantId == tenantId</c>; mismatch
/// throws <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public sealed class InMemorySubLedgerAccountRepository : ISubLedgerAccountRepository
{
    // Composite key: (TenantId.Value, SubLedgerAccountId.Value) → SubLedgerAccount
    private readonly ConcurrentDictionary<(string, string), SubLedgerAccount> _store = new();

    // ── ISubLedgerAccountRepository ────────────────────────────────────────

    /// <inheritdoc />
    public Task UpsertAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        CancellationToken cancellationToken = default)
    {
        if (account.TenantId != tenantId)
        {
            throw new ArgumentException(
                $"SubLedgerAccount.TenantId '{account.TenantId.Value}' does not match " +
                $"the supplied tenantId '{tenantId.Value}'.",
                nameof(account));
        }

        _store[(tenantId.Value, account.Id.Value)] = account;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SubLedgerAccount?> GetAsync(
        TenantId tenantId,
        SubLedgerAccountId id,
        CancellationToken cancellationToken = default)
    {
        _store.TryGetValue((tenantId.Value, id.Value), out var account);
        // TenantId check is implicit in the composite key lookup.
        return Task.FromResult(account);
    }

    /// <inheritdoc />
    public Task<SubLedgerAccount?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        var match = _store.Values
            .FirstOrDefault(a =>
                a.TenantId == tenantId &&
                a.ChartId == chartId &&
                string.Equals(a.ExternalRef, externalRef, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubLedgerAccount>> ListByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubLedgerAccount> result = _store.Values
            .Where(a => a.TenantId == tenantId && a.ChartId == chartId && a.IsActive)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubLedgerAccount>> ListAllByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubLedgerAccount> result = _store.Values
            .Where(a => a.TenantId == tenantId && a.ChartId == chartId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubLedgerAccount>> ListByPartyAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId partyId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubLedgerAccount> result = _store.Values
            .Where(a => a.TenantId == tenantId && a.ChartId == chartId && a.PartyId == partyId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubLedgerAccount>> ListByControlAccountAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        GLAccountId controlAccountId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubLedgerAccount> result = _store.Values
            .Where(a =>
                a.TenantId == tenantId &&
                a.ChartId == chartId &&
                a.ControlAccountId == controlAccountId)
            .ToList();
        return Task.FromResult(result);
    }
}
