using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The four control-account ids the transactional passes (4.1 Sales Invoices, 4.2 Purchase
/// Invoices) post against, supplied to the orchestrator as explicit run inputs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are inputs, not derived (architecture Finding 3).</b> The spec §8 flag set has no
/// control-account flag, and the imported <see cref="GLAccount"/> preserves only the coarse
/// 5-value <c>GLAccountType</c> (Asset/Liability/Equity/Revenue/Expense) — it does NOT carry
/// ERPNext's fine-grained <c>account_type</c> ("Receivable", "Payable", …). So the orchestrator
/// cannot reliably pick "the AR control account" out of an imported chart by inspection. Rather
/// than invent a brittle from-chart heuristic, the orchestrator takes the four control accounts
/// as explicit ids and posts against exactly what the caller named.
/// </para>
/// <para>
/// <b>Population is a documented seam.</b> The CLI host resolves these at RUN time (from the chart
/// the user is importing into, or from flags in a later increment). RUN is CIC-dump-gated
/// (earlier repository ticket #270), so deferring real population to the RUN-enablement increment is safe: BUILD and
/// tests supply them directly from synthetic fixtures.
/// </para>
/// </remarks>
/// <param name="ArControlAccount">The Accounts-Receivable control account Pass 4.1 debits per sales invoice.</param>
/// <param name="ApControlAccount">The Accounts-Payable control account Pass 4.2 credits per purchase invoice.</param>
/// <param name="DefaultIncomeAccount">The income account Pass 4.1 credits when a line carries no own income account.</param>
/// <param name="DefaultExpenseAccount">The expense account Pass 4.2 debits when a line carries no own expense account.</param>
public sealed record ErpnextImportControlAccounts(
    GLAccountId ArControlAccount,
    GLAccountId ApControlAccount,
    GLAccountId DefaultIncomeAccount,
    GLAccountId DefaultExpenseAccount);
