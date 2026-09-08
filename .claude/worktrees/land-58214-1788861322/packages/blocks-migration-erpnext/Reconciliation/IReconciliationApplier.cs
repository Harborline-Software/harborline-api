using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;

/// <summary>
/// Abstracts the act of applying a payment to an invoice/bill so
/// <see cref="ErpnextReconciliationPass"/> stays test-driveable without needing to
/// stand up the full <c>IPaymentApplicationService</c> + tenant context every time.
/// </summary>
/// <remarks>
/// The composition root wires this to a thin adapter over the runtime
/// <c>IPaymentApplicationService.ApplyAsync</c> (which owns validation,
/// state-machine transitions, and GL posting). Pass 5's only responsibility is to
/// decide WHICH payment goes against WHICH invoice/bill at WHICH amount; the
/// applier owns the persistence + invariants.
/// </remarks>
public interface IReconciliationApplier
{
    /// <summary>
    /// Apply <paramref name="amountApplied"/> of <paramref name="paymentId"/> to the
    /// target identified by <paramref name="appliedTo"/> + <paramref name="targetId"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the apply succeeded; <see langword="false"/> if the
    /// applier rejected it (e.g., target balance changed mid-pass). The pass records
    /// either as a distinct <see cref="PaymentReconciliationOutcome"/> kind.
    /// </returns>
    Task<bool> ApplyAsync(
        TenantId tenantId,
        PaymentId paymentId,
        AppliedTo appliedTo,
        string targetId,
        decimal amountApplied,
        CancellationToken cancellationToken = default);
}
