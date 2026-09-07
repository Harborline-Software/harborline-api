using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Import;

/// <summary>
/// Intermediate representation of a parsed bank statement row, before it is
/// persisted as a <see cref="Models.StatementLine"/>.
/// </summary>
/// <remarks>
/// <para>
/// All parsers emit this normalized shape so the import pipeline can
/// dedup-check, stamp provenance, and call the repository without
/// format-specific branching downstream.
/// </para>
/// <para>
/// <strong>Description is inert data</strong> — parsers strip leading whitespace
/// but do NOT interpret, template, or evaluate the value.
/// Formula neutralization (CWE-1236) for CSV <em>export</em> paths is a separate
/// concern handled by the exporter, not during import.
/// </para>
/// </remarks>
/// <param name="PostedAt">Transaction date from the file (UTC).</param>
/// <param name="Amount">Signed decimal: positive = credit/inflow, negative = debit/outflow.</param>
/// <param name="Currency">Currency from the file; null if the file doesn't carry a currency field.</param>
/// <param name="Description">Payee/memo/description text. Inert data; never evaluated.</param>
/// <param name="Pending">True if the file marks the line as pending/tentative.</param>
/// <param name="ProviderTxnId">Format-specific unique transaction id (FITID in OFX); null for CSV/QIF.</param>
public sealed record ParsedStatementLine(
    DateTimeOffset PostedAt,
    decimal Amount,
    CurrencyCode? Currency,
    string Description,
    bool Pending,
    string? ProviderTxnId);
