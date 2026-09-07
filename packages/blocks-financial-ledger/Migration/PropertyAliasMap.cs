namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// One entry in the CO-authored cost-center → property alias map
/// (migration-importer spec §3.4). The CO authors a one-time
/// <c>property-aliases.json</c> file in the export root before import so a
/// cost-center whose name does not auto-match a Property DocType still resolves
/// to the right <c>Property.id</c> WITHOUT a code change.
/// </summary>
/// <param name="CostCenterName">The ERPNext <c>cost_center_name</c> to match (exact, case-insensitive).</param>
/// <param name="PropertyId">The local <c>Property.id</c> the cost-center resolves to.</param>
public sealed record PropertyAliasEntry(string CostCenterName, string PropertyId);

/// <summary>
/// The cost-center → property alias map (migration-importer spec §3.4) — a thin,
/// case-insensitive lookup over the CO-authored <c>property-aliases.json</c>
/// entries. Built in-memory from a parsed entry list; the JSON parse itself is
/// the CLI driver's (A7) responsibility, so this type stays free of any file/IO
/// dependency and is trivially fixture-testable.
/// </summary>
public sealed class PropertyAliasMap
{
    private readonly IReadOnlyDictionary<string, string> _byCostCenterName;

    /// <summary>An empty alias map — every lookup misses (the no-alias-file path).</summary>
    public static PropertyAliasMap Empty { get; } = new(Array.Empty<PropertyAliasEntry>());

    /// <summary>
    /// Builds the map from a list of alias entries. A later entry for the same
    /// cost-center name wins (last-write-wins), matching a hand-edited JSON file's
    /// intuitive semantics.
    /// </summary>
    public PropertyAliasMap(IEnumerable<PropertyAliasEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CostCenterName) || string.IsNullOrWhiteSpace(entry.PropertyId))
            {
                continue;
            }

            map[entry.CostCenterName] = entry.PropertyId;
        }

        _byCostCenterName = map;
    }

    /// <summary>
    /// Attempts to resolve <paramref name="costCenterName"/> to a configured
    /// <c>Property.id</c>. Returns <see langword="false"/> on a miss.
    /// </summary>
    public bool TryResolve(string costCenterName, out string propertyId)
    {
        if (!string.IsNullOrWhiteSpace(costCenterName)
            && _byCostCenterName.TryGetValue(costCenterName, out var id))
        {
            propertyId = id;
            return true;
        }

        propertyId = string.Empty;
        return false;
    }
}
