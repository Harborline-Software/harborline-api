namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// One inter-company elimination the consolidation service removed from a summary
/// band of a <see cref="ConsolidatedBalanceSet"/> (ADR 0105 §3.3), retained for
/// audit / transparency — "here is what was removed, from which account, against which
/// in-scope counterparty, and why." Elimination is a <b>report-time</b> transform: NO
/// journal entry is posted to any chart (§3.3, §6), so this record is the only trace
/// of the removal.
/// </summary>
/// <param name="Band">
/// The summary band this elimination was computed for. Eliminations are scope-relative
/// (§3.2.1) — the same posted pair may appear under
/// <see cref="ConsolidationBand.CombinedTotal"/> but not
/// <see cref="ConsolidationBand.OwnedGroupSubtotal"/>.
/// </param>
/// <param name="Kind">How the matched pair was eliminated (ADR 0105 §3.2).</param>
/// <param name="AccountId">The account whose in-scope-counterparty portion was removed.</param>
/// <param name="CounterpartyEntityId">
/// The in-scope counterparty the removed portion was booked against. A tenant-internal
/// entity reference, NOT PII (ADR 0105 §6, sec-eng 0105-2).
/// </param>
/// <param name="AmountEliminated">
/// The signed balance amount (debit − credit) removed from the band for this
/// (account, counterparty).
/// </param>
public sealed record EliminationEntry(
    ConsolidationBand Band,
    EliminationKind Kind,
    GLAccountId AccountId,
    LegalEntityId CounterpartyEntityId,
    decimal AmountEliminated);
