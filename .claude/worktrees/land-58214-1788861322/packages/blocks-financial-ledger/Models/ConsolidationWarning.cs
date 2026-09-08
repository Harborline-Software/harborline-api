namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// A real accounting signal surfaced by a consolidation (ADR 0105 §3.3): a residual
/// the design renders <b>visibly</b> rather than silently correcting. A consolidation
/// is always produced WITH its warnings — never blocked by them, and the consolidated
/// balance sheet is NEVER forced to tie by a balancing plug (financial F-3,
/// load-bearing). Each warning is a cause the owner / CPA should chase.
/// </summary>
/// <param name="Band">
/// The summary band the residual was computed over. Warnings are scope-relative
/// (§3.2.1): a pair that nets clean in one band can leave a residual in another.
/// </param>
/// <param name="Kind">What class of residual this is (see <see cref="ConsolidationWarningKind"/>).</param>
/// <param name="Message">
/// Human-readable description for the report face. Carries account / entity ids and
/// amounts only — never PII (ADR 0105 §6, sec-eng 0105-2).
/// </param>
/// <param name="ResidualAmount">The signed residual amount — the value that did NOT net to zero.</param>
public sealed record ConsolidationWarning(
    ConsolidationBand Band,
    ConsolidationWarningKind Kind,
    string Message,
    decimal ResidualAmount);
