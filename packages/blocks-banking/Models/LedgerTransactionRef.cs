using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// A reference to a ledger transaction (journal entry) that a <see cref="MatchLink"/>
/// proposes to link a <see cref="StatementLine"/> against.
/// Per ADR 0112 fin-acct C3 — many-to-many model; a match link holds one ledger ref.
/// </summary>
/// <param name="JournalEntryId">The journal entry identifier.</param>
public readonly record struct LedgerTransactionRef(JournalEntryId JournalEntryId);
