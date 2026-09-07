using Harborline.Api.Foundation.Taxonomy.Models;

namespace Harborline.Api.Foundation.Taxonomy.Services;

/// <summary>
/// A pre-bundled definition + node set, used by
/// <see cref="ITaxonomyRegistry.RegisterCorePackageAsync"/> to seed
/// Harborline-shipped Authoritative taxonomies (e.g., Sunfish.Signature.Scopes)
/// at host bootstrap.
/// </summary>
public sealed record TaxonomyCorePackage
{
    /// <summary>The definition record.</summary>
    public required TaxonomyDefinition Definition { get; init; }

    /// <summary>The full node set (roots and children) belonging to <see cref="Definition"/>.</summary>
    public required IReadOnlyList<TaxonomyNode> Nodes { get; init; }
}
