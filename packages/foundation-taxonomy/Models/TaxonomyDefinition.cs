using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Taxonomy.Models;

/// <summary>
/// Top-level taxonomy product (a versioned, governed bundle of nodes).
/// </summary>
public sealed record TaxonomyDefinition
{
    private DefinitionEnvelope<TaxonomyDefinitionId, TaxonomyVersion, ActorId, TaxonomyLineage?> _envelope =
        new(
            default,
            default,
            ActorId.System,
            CascadeLayer.Tenant,
            Provenance: null,
            Array.Empty<DefinitionRequirement>());

    /// <summary>The definition's single control-metadata authority.</summary>
    public DefinitionEnvelope<TaxonomyDefinitionId, TaxonomyVersion, ActorId, TaxonomyLineage?> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Three-part identity (Vendor.Domain.TaxonomyName).</summary>
    public required TaxonomyDefinitionId Id
    {
        get => Envelope.Identity;
        init => _envelope = Envelope with { Identity = value };
    }

    /// <summary>Semver version of this definition.</summary>
    public required TaxonomyVersion Version
    {
        get => Envelope.Version;
        init => _envelope = Envelope with { Version = value };
    }

    /// <summary>Governance posture (Civilian / Enterprise / Authoritative).</summary>
    public required TaxonomyGovernanceRegime Governance { get; init; }

    /// <summary>Free-text description of the taxonomy's purpose.</summary>
    public required string Description { get; init; }

    /// <summary>Owning actor. Authoritative-regime definitions must be owned by <see cref="ActorId.Harborline"/>; Civilian/Enterprise are tenant-scoped actors.</summary>
    public required ActorId Owner
    {
        get => Envelope.Tenant;
        init => _envelope = Envelope with { Tenant = value };
    }

    /// <summary>Wall-clock time the version was published.</summary>
    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>Wall-clock time the version was retired; null while the version is current.</summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>Lineage record describing how this definition was derived; null for <see cref="TaxonomyLineageOp.InitialPublication"/> definitions.</summary>
    public TaxonomyLineage? DerivedFrom
    {
        get => Envelope.Provenance;
        init => _envelope = Envelope with { Provenance = value };
    }
}
