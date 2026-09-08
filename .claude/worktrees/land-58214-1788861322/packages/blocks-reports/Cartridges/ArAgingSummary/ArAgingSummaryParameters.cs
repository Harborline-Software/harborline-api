using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ArAgingSummary;

/// <summary>
/// Parameters for the <see cref="ArAgingSummaryCartridge"/>.
/// </summary>
/// <remarks>
/// W#72 PR 3 — AR Aging Summary cartridge per Stage 02 §4.14.
/// Either <see cref="AsOfDate"/> or today-from-context is used as the
/// snapshot date; no period binding (AR aging is date-relative, not
/// period-locked).
/// </remarks>
public sealed record ArAgingSummaryParameters
{
    /// <summary>The AR book to summarise. Required.</summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// Optional as-of date. When null the cartridge defaults to the
    /// context's <see cref="ReportExecutionContext.AsOfUtc"/> date in
    /// UTC, matching the runner's wall-clock.
    /// </summary>
    public System.DateOnly? AsOfDate { get; init; }

    /// <summary>
    /// Optional filter — only include rows for these customers.
    /// When null all customers in the book are included.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<PartyId>? CustomerIds { get; init; }

    /// <summary>
    /// Optional filter — only include rows whose
    /// <c>Invoice.PropertyId</c> matches one of these strings.
    /// When null all properties are included.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string>? PropertyIds { get; init; }

    /// <summary>
    /// How many top-90+-bucket customers to surface.
    /// Default 10; capped at 100. Pass 0 to suppress the list.
    /// </summary>
    public int TopDelinquentN { get; init; } = 10;
}
