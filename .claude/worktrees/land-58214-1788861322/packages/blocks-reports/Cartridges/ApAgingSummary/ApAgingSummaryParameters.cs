using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ApAgingSummary;

/// <summary>
/// Parameters for the <see cref="ApAgingSummaryCartridge"/>.
/// </summary>
/// <remarks>
/// AP aging summary — mirrors AR's <see cref="Cartridges.ArAgingSummary.ArAgingSummaryParameters"/>
/// with vendor/supplier semantics instead of customer semantics.
/// Either <see cref="AsOfDate"/> or today-from-context is used as the
/// snapshot date; no period binding (AP aging is date-relative, not
/// period-locked).
/// </remarks>
public sealed record ApAgingSummaryParameters
{
    /// <summary>The AP book to summarise. Required.</summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// Optional as-of date. When null the cartridge defaults to the
    /// context's <see cref="ReportExecutionContext.AsOfUtc"/> date in
    /// UTC, matching the runner's wall-clock.
    /// </summary>
    public System.DateOnly? AsOfDate { get; init; }

    /// <summary>
    /// Optional filter — only include rows for these vendors.
    /// When null all vendors in the book are included.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<PartyId>? VendorIds { get; init; }

    /// <summary>
    /// Optional filter — only include rows whose
    /// <c>Bill.PropertyId</c> matches one of these strings.
    /// When null all properties are included.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string>? PropertyIds { get; init; }

    /// <summary>
    /// How many top-overdue-bucket vendors to surface.
    /// Default 10; capped at 100. Pass 0 to suppress the list.
    /// </summary>
    public int TopOverdueN { get; init; } = 10;
}
