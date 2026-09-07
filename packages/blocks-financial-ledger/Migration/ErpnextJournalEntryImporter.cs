using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Default <see cref="IErpnextJournalEntryImporter"/>. Looks up
/// account FKs by external-ref name, builds a draft
/// <see cref="JournalEntry"/>, posts via the canonical
/// <see cref="IJournalPostingService"/>. Idempotent on
/// <c>ExternalRef == source.Name</c>; posted entries are immutable. Returns the
/// canonical <c>Harborline.Api.Foundation.Import</c> <see cref="ImportOutcome{T}"/> DU
/// (ADR 0100 C2/OQ-A): unresolved-account / imbalanced / post-rejected map to
/// the <see cref="ImportOutcome{T}.Rejected"/> arm with a structured
/// <see cref="ImportFailure"/> rather than a record-less <c>Skipped</c>.
/// </summary>
public sealed class ErpnextJournalEntryImporter : IErpnextJournalEntryImporter
{
    /// <summary>The ERPNext DocType this importer consumes — for census provenance.</summary>
    public const string DocType = "Journal Entry";

    private readonly IAccountResolver _accounts;
    private readonly IJournalPostingService _posting;
    private readonly IJournalStore _store;

    public ErpnextJournalEntryImporter(
        IAccountResolver accounts,
        IJournalPostingService posting,
        IJournalStore store)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _posting  = posting  ?? throw new ArgumentNullException(nameof(posting));
        _store    = store    ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task<ImportOutcome<JournalEntry>> UpsertFromErpnextAsync(
        TenantId tenantId,
        ErpnextJournalEntrySource source,
        ChartOfAccountsId targetChart,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Idempotency check. Posted entries are immutable.
        var existing = _store.Snapshot(tenantId)
            .FirstOrDefault(e => string.Equals(e.ExternalRef, source.Name, StringComparison.Ordinal));
        if (existing is not null)
        {
            return new ImportOutcome<JournalEntry>.Skipped(
                existing, "already imported (posted entries are immutable per spec §5.2)");
        }

        // Resolve account FKs by ExternalRef. The ErpnextJournalEntryLineSource
        // carries account NAMES (the ERPNext stable id); resolve to the
        // local GLAccountId via the InMemoryAccountResolver's
        // ExternalRef index, surfaced through GetByExternalRef on the
        // typed concrete (covers PR 6 scope; full IAccountResolver
        // extension lands in a follow-on).
        var lines = new List<JournalEntryLine>(source.Lines.Count);
        foreach (var ln in source.Lines)
        {
            var acct = await ResolveAccountByExternalRefAsync(ln.AccountName, cancellationToken).ConfigureAwait(false);
            if (acct is null)
            {
                return new ImportOutcome<JournalEntry>.Rejected(
                    ImportFailure.Of(
                        externalRef: source.Name,
                        docType: DocType,
                        reason: ImportRejectReason.UnresolvedReference,
                        fieldName: "account",
                        ruleViolated: $"unknown account name '{ln.AccountName}'"));
            }
            lines.Add(new JournalEntryLine(
                accountId: acct.Id,
                debit:     ln.DebitInAccountCurrency,
                credit:    ln.CreditInAccountCurrency,
                notes:     ln.UserRemark));
        }

        // Map VoucherType → JournalEntrySource. IsOpening overrides.
        var sourceKind = source.IsOpening
            ? JournalEntrySource.Migration
            : MapVoucherType(source.VoucherType);

        JournalEntry draft;
        try
        {
            draft = new JournalEntry(
                id:              JournalEntryId.NewId(),
                tenantId:        tenantId,
                entryDate:       source.PostingDate,
                memo:            source.Memo,
                lines:           lines,
                createdAtUtc:    new Instant(authority.At),
                sourceReference: source.Name) with
            {
                ChartId      = targetChart,
                SourceKind   = sourceKind,
                ExternalRef  = source.Name,
                Status       = JournalEntryStatus.Draft,
            };
        }
        catch (ArgumentException ex)
        {
            return new ImportOutcome<JournalEntry>.Rejected(
                ImportFailure.Of(
                    externalRef: source.Name,
                    docType: DocType,
                    reason: ImportRejectReason.ConstraintViolation,
                    fieldName: "lines",
                    ruleViolated: $"imbalanced source: {ex.Message}"));
        }

        var result = await _posting.PostAsync(draft, authority, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new ImportOutcome<JournalEntry>.Rejected(
                ImportFailure.Of(
                    externalRef: source.Name,
                    docType: DocType,
                    reason: ImportRejectReason.ConstraintViolation,
                    ruleViolated: $"post rejected: {result.Error} ({result.Detail})"));
        }

        return new ImportOutcome<JournalEntry>.Inserted(result.Entry!);
    }

    private async Task<GLAccount?> ResolveAccountByExternalRefAsync(string externalRef, CancellationToken ct)
    {
        if (_accounts is InMemoryAccountResolver inMem)
        {
            return inMem.SeededAccounts
                .FirstOrDefault(a => string.Equals(a.ExternalRef, externalRef, StringComparison.Ordinal));
        }
        await Task.CompletedTask;
        return null;
    }

    /// <summary>
    /// ERPNext voucher_type → JournalEntrySource per the migration-importer
    /// spec §3.3.
    /// </summary>
    public static JournalEntrySource MapVoucherType(string voucherType) => voucherType switch
    {
        "Opening Entry"      => JournalEntrySource.Migration,
        "Bank Entry"         => JournalEntrySource.Payment,
        "Cash Entry"         => JournalEntrySource.Payment,
        "Credit Card Entry"  => JournalEntrySource.Payment,
        "Contra Entry"       => JournalEntrySource.Manual,
        "Depreciation Entry" => JournalEntrySource.Depreciation,
        "Excise Entry"       => JournalEntrySource.Adjusting,
        "Journal Entry"      => JournalEntrySource.Manual,
        "Write Off Entry"    => JournalEntrySource.Adjusting,
        _                    => JournalEntrySource.Manual,
    };
}
