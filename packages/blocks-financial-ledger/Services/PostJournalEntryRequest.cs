using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Input for <see cref="IAccountingService.PostEntryAsync"/>.
/// </summary>
/// <param name="TenantId">
/// Tenant scope (cohort-2 PR 0d). Stamped onto the resulting
/// <see cref="JournalEntry"/> per ADR 0092 Step 1 tenant-keying retrofit.
/// </param>
/// <param name="EntryDate">Accounting date the entry is effective for.</param>
/// <param name="Memo">Human-readable description of the transaction.</param>
/// <param name="Lines">
/// Debit/credit lines. Must not be empty. Total debits must equal total credits.
/// All referenced <see cref="JournalEntryLine.AccountId"/>s must exist.
/// </param>
/// <param name="SourceReference">
/// Optional opaque back-reference to the originating event (e.g. <c>"rent-payment:INV-123"</c>).
/// </param>
public sealed record PostJournalEntryRequest(
    TenantId TenantId,
    DateOnly EntryDate,
    string Memo,
    IReadOnlyList<JournalEntryLine> Lines,
    string? SourceReference = null);
