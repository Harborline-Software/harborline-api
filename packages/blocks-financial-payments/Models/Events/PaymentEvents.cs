using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.FinancialPayments.Models.Events;

/// <summary>
/// Canonical event-type strings emitted by <c>DefaultPaymentApplicationService</c>
/// via <see cref="Harborline.Api.Foundation.Events.IDomainEventPublisher"/>. Producer
/// cluster = <c>Financial</c> (mirrors <c>blocks-financial-ar</c> /
/// <c>blocks-financial-ap</c> naming).
/// </summary>
public static class PaymentEventNames
{
    /// <summary>A payment application was created (cash applied against an Invoice or Bill).</summary>
    public const string PaymentApplied = "Financial.PaymentApplied";

    /// <summary>A payment application was reversed (correction path).</summary>
    public const string PaymentUnapplied = "Financial.PaymentUnapplied";
}

/// <summary>Payload for <see cref="PaymentEventNames.PaymentApplied"/>.</summary>
public sealed record PaymentAppliedPayload(
    PaymentApplicationId ApplicationId,
    PaymentId PaymentId,
    PaymentDirection Direction,
    AppliedTo AppliedTo,
    string TargetId,
    decimal AmountApplied,
    decimal DiscountAmount,
    decimal WriteoffAmount,
    PartyId Actor);

/// <summary>Payload for <see cref="PaymentEventNames.PaymentUnapplied"/>.</summary>
public sealed record PaymentUnappliedPayload(
    PaymentApplicationId ApplicationId,
    PaymentId PaymentId,
    AppliedTo AppliedTo,
    string TargetId,
    decimal AmountApplied,
    PartyId Actor);
