using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Default <see cref="IErpnextAccountImporter"/>. Maps the ERPNext
/// <c>account_type</c> string to the local
/// <see cref="GLAccountType"/> + <see cref="AccountSubtype"/> pair per
/// the migration-importer spec §3.2 enum-mapping table. Idempotent on
/// <c>ExternalRef == source.Name</c>. Returns the canonical
/// <c>Harborline.Api.Foundation.Import</c> <see cref="ImportOutcome{T}"/>
/// discriminated union (ADR 0100 C2/OQ-A — the per-cluster copy is retired).
/// </summary>
public sealed class ErpnextAccountImporter : IErpnextAccountImporter
{
    /// <summary>The ERPNext DocType this importer consumes — for census provenance.</summary>
    public const string DocType = "Account";

    private readonly InMemoryAccountResolver _accounts;
    private readonly TimeProvider _time;

    public ErpnextAccountImporter(InMemoryAccountResolver accounts, TimeProvider time)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public Task<ImportOutcome<GLAccount>> UpsertFromErpnextAsync(
        ErpnextAccountSource source,
        ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default)
    {
        var at = new Instant(_time.GetUtcNow());
        ArgumentNullException.ThrowIfNull(source);

        var existing = _accounts.SeededAccounts
            .FirstOrDefault(a => string.Equals(a.ExternalRef, source.Name, StringComparison.Ordinal));

        if (existing is not null)
        {
            // The stored source-version key is round-tripped through Description
            // (no dedicated version field yet). ERPNext "modified" is ISO-8601 so
            // string equality on a re-import is the "same version" / Skipped path.
            if (string.Equals(source.Modified, existing.Description, StringComparison.Ordinal))
            {
                return Task.FromResult<ImportOutcome<GLAccount>>(
                    new ImportOutcome<GLAccount>.Skipped(existing, "same source version"));
            }

            var updated = existing with
            {
                Name = source.AccountName,
                Code = source.AccountNumber ?? existing.Code,
                IsPostable = !source.IsGroup,
                IsActive = !source.Disabled,
                Description = source.Modified,
                UpdatedAtUtc = at,
            };
            _accounts.Upsert(updated);
            return Task.FromResult<ImportOutcome<GLAccount>>(
                new ImportOutcome<GLAccount>.Updated(updated, $"version {source.Modified}"));
        }

        var (type, subtype) = MapAccountType(source.AccountType);
        var parentId = ResolveParentByExternalRef(source.ParentAccountName);
        var account = GLAccount.Create(
            id:              GLAccountId.NewId(),
            chartId:         targetChart,
            code:            source.AccountNumber ?? source.Name,
            name:            source.AccountName,
            type:            type,
            subtype:         subtype,
            currency:        "USD",
            createdAtUtc:    at,
            parentAccountId: parentId,
            isPostable:      !source.IsGroup,
            description:     source.Modified, // store source version in Description for back-compat (no dedicated field yet)
            externalRef:     source.Name) with
        {
            IsActive = !source.Disabled,
        };
        _accounts.Upsert(account);
        return Task.FromResult<ImportOutcome<GLAccount>>(
            new ImportOutcome<GLAccount>.Inserted(account));
    }

    private GLAccountId? ResolveParentByExternalRef(string? parentName)
    {
        if (parentName is null) return null;
        var parent = _accounts.SeededAccounts
            .FirstOrDefault(a => string.Equals(a.ExternalRef, parentName, StringComparison.Ordinal));
        return parent?.Id;
    }

    /// <summary>
    /// ERPNext <c>account_type</c> → (<see cref="GLAccountType"/>,
    /// <see cref="AccountSubtype"/>) per migration-importer spec §3.2. An empty /
    /// unknown <c>account_type</c> falls back to <c>Asset / OtherAsset</c>; the
    /// orchestrator's parent-walk (<see cref="ErpnextChartImportPass"/>) refines
    /// the top-level category from a populated ancestor before this fallback bites.
    /// </summary>
    public static (GLAccountType Type, AccountSubtype Subtype) MapAccountType(string? accountType)
    {
        return accountType switch
        {
            // Assets
            "Bank"                      => (GLAccountType.Asset,     AccountSubtype.BankAccount),
            "Cash"                      => (GLAccountType.Asset,     AccountSubtype.BankAccount),
            "Receivable"                => (GLAccountType.Asset,     AccountSubtype.AccountsReceivable),
            "Stock"                     => (GLAccountType.Asset,     AccountSubtype.InventoryAsset),
            "Fixed Asset"               => (GLAccountType.Asset,     AccountSubtype.FixedAsset),
            "Accumulated Depreciation"  => (GLAccountType.Asset,     AccountSubtype.AccumulatedDepreciation),
            "Current Asset"             => (GLAccountType.Asset,     AccountSubtype.CurrentAsset),

            // Liabilities
            "Payable"                   => (GLAccountType.Liability, AccountSubtype.AccountsPayable),
            "Tax"                       => (GLAccountType.Liability, AccountSubtype.TaxesPayable),
            "Current Liability"         => (GLAccountType.Liability, AccountSubtype.CurrentLiability),
            "Liability"                 => (GLAccountType.Liability, AccountSubtype.CurrentLiability),

            // Equity
            "Equity"                    => (GLAccountType.Equity,    AccountSubtype.OwnersEquity),

            // Income
            "Income Account"            => (GLAccountType.Revenue,   AccountSubtype.OperatingIncome),
            "Income"                    => (GLAccountType.Revenue,   AccountSubtype.OperatingIncome),

            // Expense
            "Cost of Goods Sold"        => (GLAccountType.Expense,   AccountSubtype.CostOfGoodsSold),
            "Expense Account"           => (GLAccountType.Expense,   AccountSubtype.OperatingExpense),
            "Expense"                   => (GLAccountType.Expense,   AccountSubtype.OperatingExpense),
            "Depreciation"              => (GLAccountType.Expense,   AccountSubtype.DepreciationExpense),
            "Round Off"                 => (GLAccountType.Expense,   AccountSubtype.OtherExpense),

            _                           => (GLAccountType.Asset,     AccountSubtype.OtherAsset),
        };
    }
}
