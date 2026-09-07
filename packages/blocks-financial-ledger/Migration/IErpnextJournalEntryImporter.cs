using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Pass 3 of the ERPNext → Harborline-native migration: idempotent upsert
/// of <see cref="JournalEntry"/> records from
/// <see cref="ErpnextJournalEntrySource"/>. Posted entries are
/// immutable per the migration-importer spec §5.2 — re-import of a
/// previously-imported entry returns the <see cref="ImportOutcome{T}.Skipped"/>
/// arm (with a warning detail string if any field has drifted). A record that
/// cannot be imported (unresolved account, imbalanced lines, post rejected)
/// returns the <see cref="ImportOutcome{T}.Rejected"/> arm carrying a structured
/// <see cref="ImportFailure"/> (ADR 0100 C2/OQ-A).
/// </summary>
public interface IErpnextJournalEntryImporter
{
    Task<ImportOutcome<JournalEntry>> UpsertFromErpnextAsync(
        TenantId tenantId,
        ErpnextJournalEntrySource source,
        ChartOfAccountsId targetChart,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}
