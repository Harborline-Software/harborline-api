using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Import;

/// <summary>
/// Orchestrates the file-import pipeline: parse → dedup → persist.
/// Per ADR 0112 Part 1 §3 — file import pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Dedup model (ADR 0112 fin-acct N1):</strong>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Feed lines</strong> (<see cref="ParsedStatementLine.ProviderTxnId"/> non-null):
///     dedupe on <c>(accountId, providerTxnId)</c> — checked via
///     <see cref="IStatementLineRepository.FindByProviderTxnIdAsync"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>File lines</strong> (<see cref="ParsedStatementLine.ProviderTxnId"/> null):
///     dedupe on <c>(accountId, batchId, ordinalWithinBatch)</c>.
///     This preserves two genuinely-identical lines within one statement
///     (e.g. two $20 ATM withdrawals) while deduping re-imports of the same file.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>Pre-cutover exclusion (ADR 0112 fin-acct C2):</strong>
/// lines dated before <see cref="BankAccount.CutoverAsOf"/> are imported as
/// <see cref="ReconciliationState.Excluded"/>. They are never auto-matched.
/// </para>
/// <para>
/// <strong>Currency mismatch (ADR 0112 fin-acct invariant 4):</strong>
/// lines whose currency differs from the account currency are imported as
/// <see cref="ReconciliationState.Excluded"/> in v1 (multi-currency is v2).
/// </para>
/// </remarks>
public sealed class ImportPipelineService
{
    private readonly IReadOnlyList<IStatementFileParser> _parsers;
    private readonly IStatementLineRepository _lineRepo;
    private readonly IBankAccountRepository _accountRepo;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new <see cref="ImportPipelineService"/>.
    /// </summary>
    /// <param name="parsers">All registered statement file parsers.</param>
    /// <param name="lineRepo">Statement line repository.</param>
    /// <param name="accountRepo">Bank account repository.</param>
    /// <param name="time">Host clock sampled once per import act.</param>
    public ImportPipelineService(
        IEnumerable<IStatementFileParser> parsers,
        IStatementLineRepository lineRepo,
        IBankAccountRepository accountRepo,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(lineRepo);
        ArgumentNullException.ThrowIfNull(accountRepo);

        _parsers     = parsers.ToList();
        _lineRepo    = lineRepo;
        _accountRepo = accountRepo;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Imports a statement file for the given account into the repository.
    /// Returns a summary of the import batch.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="accountId">The <see cref="BankAccount"/> to import lines for.</param>
    /// <param name="stream">File content.</param>
    /// <param name="fileName">Original filename, used for format detection.</param>
    /// <param name="batchId">Caller-assigned batch identifier (opaque, typically a GUID string).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If no parser handles the file format.</exception>
    /// <exception cref="StatementParseException">If the file is rejected by the parser.</exception>
    public async Task<ImportBatchResult> ImportFileAsync(
        TenantId tenantId,
        BankAccountId accountId,
        Stream stream,
        string fileName,
        string batchId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        // Resolve the bank account (needed for cutover + currency checks)
        BankAccount? account = await _accountRepo.GetByIdAsync(tenantId, accountId, ct);
        if (account is null)
            throw new InvalidOperationException($"Bank account {accountId} not found for tenant.");

        // Find parser
        IStatementFileParser parser = _parsers.FirstOrDefault(p => p.CanHandle(fileName))
            ?? throw new InvalidOperationException(
                $"No parser handles file format for '{fileName}'. Supported: {string.Join(", ", _parsers.Select(p => p.Format))}");

        // Parse
        IReadOnlyList<ParsedStatementLine> parsed = await parser.ParseAsync(stream, fileName, ct);

        Instant now = new(_time.GetUtcNow());
        int inserted = 0, skipped = 0;

        for (int i = 0; i < parsed.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ParsedStatementLine row = parsed[i];

            // Determine initial reconciliation state
            ReconciliationState state = DetermineInitialState(row, account);

            // Dedup check
            if (row.ProviderTxnId is not null)
            {
                // Feed-style dedup: (accountId, providerTxnId)
                StatementLine? existing = await _lineRepo.FindByProviderTxnIdAsync(
                    tenantId, accountId, row.ProviderTxnId, ct);
                if (existing is not null)
                {
                    skipped++;
                    continue;
                }
            }
            else
            {
                // File-import dedup: (accountId, batchId, ordinalWithinBatch)
                StatementLine? existing = await _lineRepo.FindByBatchOrdinalAsync(
                    tenantId, accountId, batchId, i, ct);
                if (existing is not null)
                {
                    skipped++;
                    continue;
                }
            }

            // Resolve currency — use account currency if file doesn't carry one
            CurrencyCode currency = row.Currency ?? account.Currency;

            var line = new StatementLine(
                Id:             StatementLineId.NewId(),
                TenantId:       tenantId,
                AccountId:      accountId,
                ProviderTxnId:  row.ProviderTxnId,
                PostedAt:       (Instant)row.PostedAt,
                Amount:         row.Amount,
                Currency:       currency,
                Description:    row.Description,
                Pending:        row.Pending,
                State:          state,
                Source:         new ImportSourceRef(ImportSourceKind.FileImport, batchId, i),
                RawProviderBlob: null,
                CreatedAtUtc:   now);

            await _lineRepo.AddAsync(line, ct);
            inserted++;
        }

        return new ImportBatchResult(
            BatchId:   batchId,
            AccountId: accountId,
            Format:    parser.Format,
            TotalRows: parsed.Count,
            Inserted:  inserted,
            Skipped:   skipped);
    }

    private static ReconciliationState DetermineInitialState(ParsedStatementLine row, BankAccount account)
    {
        Instant postedAt = (Instant)row.PostedAt;

        // Pre-cutover lines → Excluded (ADR 0112 fin-acct C2)
        if (postedAt.Value < account.CutoverAsOf.Value)
            return ReconciliationState.Excluded;

        // Currency mismatch → Excluded in v1 (ADR 0112 fin-acct invariant 4)
        if (row.Currency is not null && row.Currency != account.Currency)
            return ReconciliationState.Excluded;

        return ReconciliationState.Unmatched;
    }
}

/// <summary>
/// Summary of a completed import batch.
/// </summary>
public sealed record ImportBatchResult(
    string BatchId,
    BankAccountId AccountId,
    string Format,
    int TotalRows,
    int Inserted,
    int Skipped)
{
    /// <summary>Rows that were skipped due to duplicate detection.</summary>
    public int Duplicates => Skipped;
}
