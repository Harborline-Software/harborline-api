using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory <see cref="IGeneralLedgerReadModel"/> backed by
/// <see cref="IJournalStore.Snapshot"/>. Computes signed balances by
/// scanning the snapshot + summing per-account
/// (<see cref="JournalEntryLine.Debit"/> - <see cref="JournalEntryLine.Credit"/>)
/// across all <em>committed</em> entries — <see cref="JournalEntryStatus.Posted"/>
/// and <see cref="JournalEntryStatus.Reversed"/> — whose
/// <see cref="JournalEntry.EntryDate"/> is on or before the
/// caller-supplied as-of date and whose
/// <see cref="JournalEntry.ChartId"/> matches the requested chart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reversal semantics (BUG-002 fix — Admiral ruling 2026-06-13T0700Z OPTION A):</b>
/// Standard double-entry: a <see cref="JournalEntryStatus.Reversed"/> entry stays
/// in the balance sum. The offsetting reversal entry (which is
/// <see cref="JournalEntryStatus.Posted"/>) cancels it — both entries are visible,
/// net effect is zero, full audit trail preserved. <c>Reversed</c> status exists
/// only to prevent a second reversal (the F-89-A guard), NOT to remove the entry
/// from the ledger balance. Excluding <c>Reversed</c> entries would double-cancel:
/// the original's effect disappears AND the reversal entry's opposite-sign lines
/// shift the balance further, producing a net of −amount instead of 0.
/// </para>
/// <para>
/// Only <see cref="JournalEntryStatus.Draft"/> entries are excluded; they have not
/// been committed to the books.
/// </para>
/// <para>
/// Suitable for Phase 1 (small tenant, in-memory journal). A SQLite-
/// backed implementation lands in a follow-on hand-off when the
/// journal store moves off in-memory; this contract is stable so the
/// swap is host-only.
/// </para>
/// </remarks>
public sealed class InMemoryGeneralLedgerReadModel : IGeneralLedgerReadModel
{
    private readonly IJournalStore _journals;

    /// <summary>Construct bound to a journal store.</summary>
    public InMemoryGeneralLedgerReadModel(IJournalStore journals)
        => _journals = journals ?? throw new System.ArgumentNullException(nameof(journals));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<GLAccountId, decimal>> GetAccountBalancesAsOfAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        System.DateOnly asOf,
        string snapshotMarker,
        CancellationToken cancellationToken = default)
    {
        // snapshotMarker accepted + ignored in Phase 1 (no per-tenant snapshot isolation in the in-memory store).
        var balances = new Dictionary<GLAccountId, decimal>();
        foreach (var entry in _journals.Snapshot(tenantId))
        {
            // Include Posted AND Reversed entries. Draft entries are uncommitted and excluded.
            // Reversed entries must stay in the balance so the offsetting reversal entry can
            // cancel them — net zero on both. Excluding Reversed would double-cancel:
            // original removed from balance AND reversal adds opposite-sign → net -amount.
            // See class remarks for the full rationale (BUG-002 / OPTION A ruling).
            if (entry.Status == JournalEntryStatus.Draft) continue;
            if (entry.ChartId is null || entry.ChartId.Value != chartId) continue;
            if (entry.EntryDate > asOf) continue;

            foreach (var line in entry.Lines)
            {
                if (!balances.TryGetValue(line.AccountId, out var running)) running = 0m;
                balances[line.AccountId] = running + line.Debit - line.Credit;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<GLAccountId, decimal>>(balances);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>>
        GetIntercompanyBalancesAsOfAsync(
            TenantId tenantId,
            ChartOfAccountsId chartId,
            System.DateOnly asOf,
            string snapshotMarker,
            CancellationToken cancellationToken = default)
    {
        // Same committed-only / chart / as-of filter as GetAccountBalancesAsOfAsync; the
        // ONLY difference is that lines carrying a CounterpartyLegalEntityId stamp are
        // kept broken down by counterparty (the aggregate read collapses them).
        // Includes Posted AND Reversed — see GetAccountBalancesAsOfAsync remarks for rationale.
        var byAccount = new Dictionary<GLAccountId, Dictionary<LegalEntityId, decimal>>();
        foreach (var entry in _journals.Snapshot(tenantId))
        {
            if (entry.Status == JournalEntryStatus.Draft) continue;
            if (entry.ChartId is null || entry.ChartId.Value != chartId) continue;
            if (entry.EntryDate > asOf) continue;

            foreach (var line in entry.Lines)
            {
                if (line.CounterpartyLegalEntityId is not { } counterparty) continue;

                if (!byAccount.TryGetValue(line.AccountId, out var byCounterparty))
                {
                    byCounterparty = new Dictionary<LegalEntityId, decimal>();
                    byAccount[line.AccountId] = byCounterparty;
                }
                if (!byCounterparty.TryGetValue(counterparty, out var running)) running = 0m;
                byCounterparty[counterparty] = running + line.Debit - line.Credit;
            }
        }

        var result = new Dictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>();
        foreach (var (accountId, byCounterparty) in byAccount)
        {
            result[accountId] = byCounterparty;
        }
        return Task.FromResult<IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>>(result);
    }
}
