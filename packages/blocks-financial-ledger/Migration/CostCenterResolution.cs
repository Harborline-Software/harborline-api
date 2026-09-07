using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// The outcome of resolving ONE ERPNext cost-center through the §3.4 heuristic
/// (migration-importer spec §3.4): either it resolved to a known
/// <see cref="Models.PropertyId"/> (via the CO-authored alias map or a custom
/// Property-DocType-name match), OR — when neither matched — a free-form
/// <see cref="Models.Classification"/> was created to preserve the dimensional tag.
/// </summary>
/// <remarks>
/// A closed two-arm union: exactly one of <see cref="PropertyId"/> /
/// <see cref="Classification"/> is non-null, distinguished by <see cref="Kind"/>.
/// The cost-center never vanishes — every cost-center yields exactly one
/// resolution, recorded in the migration report's cost-center-resolution section
/// (the A6 report §"cost-center resolution").
/// </remarks>
public sealed record CostCenterResolution
{
    private CostCenterResolution(
        CostCenterResolutionKind kind,
        string externalRef,
        PropertyId? propertyId,
        Classification? classification)
    {
        Kind = kind;
        ExternalRef = externalRef;
        PropertyId = propertyId;
        Classification = classification;
    }

    /// <summary>Which arm resolved — a known property or a created classification.</summary>
    public CostCenterResolutionKind Kind { get; }

    /// <summary>The ERPNext cost-center <c>name</c> natural key (trace-back).</summary>
    public string ExternalRef { get; }

    /// <summary>The resolved property id when <see cref="Kind"/> is <see cref="CostCenterResolutionKind.ResolvedToProperty"/>; otherwise <see langword="null"/>.</summary>
    public PropertyId? PropertyId { get; }

    /// <summary>The created classification when <see cref="Kind"/> is <see cref="CostCenterResolutionKind.CreatedClassification"/>; otherwise <see langword="null"/>.</summary>
    public Classification? Classification { get; }

    /// <summary>The cost-center matched a known property (alias map or Property-DocType name).</summary>
    public static CostCenterResolution ToProperty(string externalRef, PropertyId propertyId) =>
        new(CostCenterResolutionKind.ResolvedToProperty, externalRef, propertyId, classification: null);

    /// <summary>No property matched — a free-form classification was created to preserve the tag.</summary>
    public static CostCenterResolution ToClassification(string externalRef, Classification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return new(CostCenterResolutionKind.CreatedClassification, externalRef, propertyId: null, classification);
    }
}

/// <summary>The two arms of <see cref="CostCenterResolution"/> (migration-importer spec §3.4).</summary>
public enum CostCenterResolutionKind
{
    /// <summary>The cost-center resolved to a known <c>Property.id</c>.</summary>
    ResolvedToProperty,

    /// <summary>No property matched; a free-form <c>Classification</c> was created.</summary>
    CreatedClassification,
}
