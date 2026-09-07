using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

/// <summary>
/// CRUD surface over the payment-application substrate.
///
/// <para>
/// <b>Important:</b> This repository stores and retrieves
/// <see cref="PaymentApplication"/> rows only — it does NOT update
/// <see cref="Payment.UnappliedAmount"/>, Invoice balances, or Bill balances.
/// Balance mutation is the responsibility of <c>IPaymentApplicationService</c>.
/// </para>
///
/// <para>
/// <b>Cohort-2 PR 0c tenant-keying retrofit.</b> Every method takes
/// <see cref="TenantId"/> as the first positional parameter. Read methods
/// filter by tenant (uniform-404). Write methods assert
/// <c>application.TenantId == tenantId</c>.
/// </para>
/// </summary>
public interface IPaymentApplicationRepository : ITenantScopedRepository<PaymentApplication, PaymentApplicationId>
{
    /// <summary>Insert a new application record.</summary>
    Task AddAsync(
        TenantId tenantId, PaymentApplication application, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Get an application by id. Returns null when missing OR cross-tenant.</summary>
    Task<PaymentApplication?> GetAsync(
        TenantId tenantId, PaymentApplicationId id, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Immutably reverse an application by retaining the original row, adding a linked contra row,
    /// and stamping the original's reversal linkage.
    /// <para>
    /// Ticket 095: the outcome is discriminated, not nullable. <c>NotFound</c> when the id is
    /// unknown, cross-tenant, or identifies a contra row; <c>Recorded</c> when THIS call wrote the
    /// contra evidence; <c>AlreadyReversed</c> when an earlier call did and this one wrote nothing.
    /// Only <c>Recorded</c> licenses a caller to restore a balance.
    /// </para>
    /// </summary>
    Task<PaymentApplicationReversalResult> ReverseAsync(
        TenantId tenantId,
        PaymentApplicationId id,
        Instant reversedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>List all applications for a given payment, scoped to <paramref name="tenantId"/>.</summary>
    Task<IReadOnlyList<PaymentApplication>> ListByPaymentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken = default);

    /// <summary>List all applications targeting a given Invoice or Bill (identified by string id), scoped to <paramref name="tenantId"/>.</summary>
    Task<IReadOnlyList<PaymentApplication>> ListByTargetAsync(TenantId tenantId, string targetId, CancellationToken cancellationToken = default);
}
