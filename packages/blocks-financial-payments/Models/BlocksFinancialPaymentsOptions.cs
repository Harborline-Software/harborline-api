namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// Host-supplied configuration for the payments cluster. Passed through
/// <see cref="DependencyInjection.PaymentsServiceCollectionExtensions.AddHarborlineFinancialPayments"/>.
/// Mirrors the shape of <c>BlocksFinancialArOptions</c> and
/// <c>BlocksFinancialApOptions</c>.
/// </summary>
public sealed class BlocksFinancialPaymentsOptions
{
    /// <summary>
    /// Polling interval used by background reconciliation jobs, if any.
    /// Defaults to 5 minutes. Hosts may shorten this for real-time
    /// reconciliation or lengthen it to reduce load.
    /// </summary>
    public TimeSpan FallbackPollingInterval { get; init; } = TimeSpan.FromMinutes(5);
}
