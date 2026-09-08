namespace Harborline.Api.Foundation.Assets.Common;

/// <summary>
/// Static helper(s) for <see cref="TenantId"/>. Per ADR 0091 R2 amendment 3,
/// <see cref="FromString"/> centralizes the string → <see cref="TenantId"/>
/// conversion path so the reserved-prefix guard (per ADR 0084 §1) fires once
/// in a known location, rather than scattered across 14+ endpoint boundary
/// points.
/// </summary>
public readonly partial record struct TenantId
{
    /// <summary>
    /// Parses a string into a <see cref="TenantId"/>. Throws
    /// <see cref="System.ArgumentException"/> for values starting with the
    /// reserved <c>"__"</c> prefix (per ADR 0084 §1). Use this helper rather
    /// than the implicit string conversion at boundary points (endpoint
    /// binding, query-string parsing) so the guard fires once, audibly, in a
    /// known location. Per ADR 0091 R2 amendment 3.
    /// </summary>
    public static TenantId FromString(string value) => new TenantId(value);
}
