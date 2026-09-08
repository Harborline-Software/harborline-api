using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;

/// <summary>
/// The composition-root adapter the orchestrator (A7) wires <see cref="IReconciliationApplier"/> to:
/// a thin pass-through onto the runtime <see cref="IPaymentApplicationService"/>, which owns the
/// validation, state-machine transitions, balance recomputation, and GL posting. Pass 5 decides
/// WHICH payment applies to WHICH target at WHICH amount; this adapter performs the apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration applies carry no discount/write-off.</b> An ERPNext payment-entry reference row maps
/// to a straight application of <c>amountApplied</c>; discount and write-off are always
/// <c>0</c> on the migration path (any discount/write-off was already baked into the source
/// invoice/bill balances). The acting party is the run's <c>ErpnextImportRequest.Actor</c>,
/// captured at construction.
/// </para>
/// <para>
/// <b>Success mapping.</b> The pass's boolean contract is "did the apply land?": this maps
/// <see cref="ApplyError.None"/> → <see langword="true"/> and every other <see cref="ApplyError"/>
/// → <see langword="false"/>, which the pass records as a distinct unapplied/failed outcome. The
/// adapter neither logs nor throws on a non-<c>None</c> error — the error discriminant never
/// carries PII, but surfacing it is the pass's job, not the adapter's.
/// </para>
/// <para>
/// <b>Tenant.</b> <see cref="IReconciliationApplier.ApplyAsync"/> carries a <c>tenantId</c>
/// for callers that need it, but <see cref="IPaymentApplicationService"/> resolves the tenant from
/// its ambient context (the composition root scopes that context to the run's tenant), so the
/// adapter does not forward it. This keeps the adapter a pure pass-through and avoids a second,
/// divergent tenant source.
/// </para>
/// </remarks>
public sealed class PaymentApplicationReconciliationApplier : IReconciliationApplier
{
    private readonly IPaymentApplicationService _paymentApplicationService;
    private readonly PartyId _actor;

    /// <summary>Create the adapter over the runtime payment-application service, stamping <paramref name="actor"/> on every apply.</summary>
    /// <param name="paymentApplicationService">The service that owns apply validation + persistence + GL posting.</param>
    /// <param name="actor">The run's acting party (audit attribution), from <c>ErpnextImportRequest.Actor</c>.</param>
    public PaymentApplicationReconciliationApplier(
        IPaymentApplicationService paymentApplicationService,
        PartyId actor)
    {
        _paymentApplicationService = paymentApplicationService;
        _actor = actor;
    }

    /// <inheritdoc />
    public async Task<bool> ApplyAsync(
        TenantId tenantId,
        PaymentId paymentId,
        AppliedTo appliedTo,
        string targetId,
        decimal amountApplied,
        CancellationToken cancellationToken = default)
    {
        ApplyResult result = await _paymentApplicationService.ApplyAsync(
            paymentId,
            appliedTo,
            targetId,
            amountApplied,
            discountAmount: 0m,
            writeoffAmount: 0m,
            _actor,
            cancellationToken).ConfigureAwait(false);

        return result.Error == ApplyError.None;
    }
}
