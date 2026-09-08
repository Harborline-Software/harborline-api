using System.Collections.Generic;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Row-level summary DTO for the journal entry query read model.
/// Wire fields match the existing JournalEntriesReadEndpoints.cs ToSummaryDto output
/// so the Bridge rewire is a zero-breaking-change drop-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR 0121 Phase 3a — Account column (<c>AccountIds</c>).</b> The summary carries the
/// DISTINCT, ordered set of <c>GLAccountId</c> values referenced across the entry's lines so
/// the frontend can render the Account column without a second round-trip to fetch the detail:
/// <list type="bullet">
///   <item>exactly one id → render that account (resolved Code/Name client-side from the
///     cached chart-of-accounts map, never sent on the wire);</item>
///   <item>two or more ids → render the "— Split —" affordance.</item>
/// </list>
/// Empty only for the degenerate zero-line case (which the <see cref="Models.JournalEntry"/>
/// constructor already rejects). Account ids are opaque identifiers — no account names,
/// codes, or amounts cross the wire here (ADR 0121 §D6 code→name preference is resolved
/// client-side from the cached COA map).
/// </para>
/// </remarks>
public sealed record JournalEntrySummaryDto(
    string Id,
    string EntryDate,
    string? Memo,
    string Status,
    string SourceKind,
    int LineCount,
    double TotalDebits,
    double TotalCredits,
    string? ReversalOf,
    string? ReversedBy,
    string CreatedAt,
    IReadOnlyList<string> AccountIds);
