using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Reports.Cartridges.RentRoll;

/// <summary>
/// Parameters for the <see cref="RentRollCartridge"/>.
/// Per Stage 02 §4.1 and W#72 hand-off §"PR 6 — Parameters".
/// </summary>
public sealed record RentRollParameters
{
    /// <summary>
    /// Chart scope for AR aging lookup.
    /// Required for delinquency-bucket derivation via <c>IArAgingService</c>.
    /// </summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// As-of date for the snapshot. Defaults to today (UTC) when null.
    /// Used to determine which leases are "active" and for AR aging bucket classification.
    /// </summary>
    public DateOnly? AsOfDate { get; init; }

    /// <summary>
    /// Optional filter: restrict output to these property authority keys.
    /// A property key is the <c>Authority</c> segment of a unit's <see cref="EntityId"/>
    /// (i.e. the <c>authority</c> in <c>unit:authority/localPart</c>).
    ///
    /// <para>
    /// <b>Substrate note (D1).</b> <see cref="Harborline.Api.Blocks.Leases.Models.Lease"/> carries
    /// a <c>UnitId</c> (<see cref="EntityId"/>) but no explicit <c>PropertyId</c> field.
    /// The <c>UnitId.Authority</c> segment is used as the property grouping key.
    /// A dedicated <c>PropertyId</c> field will be added when the property-management
    /// cluster ships; this cartridge will be updated at that point.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? PropertyAuthorityKeys { get; init; }

    /// <summary>
    /// Lookahead window in days for the "expiring soon" flag.
    /// A lease whose <c>EndDate &lt;= AsOfDate + ExpiringWindowDays</c> is flagged.
    /// Must be &gt;= 0. Default 90.
    /// </summary>
    public int ExpiringWindowDays { get; init; } = 90;

    /// <summary>
    /// When <see langword="true"/>, include unit rows with no active lease (vacant).
    /// When <see langword="false"/>, only occupied units appear. Default <see langword="true"/>.
    /// </summary>
    public bool IncludeVacant { get; init; } = true;
}
