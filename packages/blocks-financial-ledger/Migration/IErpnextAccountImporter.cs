using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Pass 1 of the ERPNext → Harborline-native migration: idempotent upsert
/// of <see cref="GLAccount"/> records from <see cref="ErpnextAccountSource"/>.
/// Lookup by <c>GLAccount.ExternalRef == source.Name</c>. Returns the
/// <see cref="ImportOutcome{T}.Skipped"/> arm when the local version is
/// at-or-newer than the source (ADR 0100 C2 — the canonical
/// <c>Harborline.Api.Foundation.Import</c> discriminated union, not the retired
/// per-cluster copy).
/// </summary>
public interface IErpnextAccountImporter
{
    Task<ImportOutcome<GLAccount>> UpsertFromErpnextAsync(
        ErpnextAccountSource source,
        ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default);
}
