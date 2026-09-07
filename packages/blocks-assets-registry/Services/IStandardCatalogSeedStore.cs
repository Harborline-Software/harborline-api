using Harborline.Api.Blocks.Assets.Registry.Model.Scoring;
using Harborline.Api.Foundation.Forms.Models;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Registry of the <see cref="StandardCatalogSeed"/>s a pack/base ships, plus the tenant scoring overrides
/// layered on top (ADR 0101 Rev 3.1 Wave 3a, annex §3.8 D-P). It is the immutable-seed / mutable-override
/// split for living standards — the same F2 shape <see cref="IEntityTypeRegistry"/> uses for types.
/// </summary>
/// <remarks>
/// Seeds are SHARED (not tenant-scoped) and immutable; overrides are tenant-scoped scoring overlays that
/// NEVER mutate the seed. <see cref="ResolveScoring"/> merges [seed overlay, tenant overlay] through the
/// <see cref="IScoringCascadeResolver"/>, so a seed-declared critical/safety floor is raise-strictness-only
/// (F1). The store does NOT compute the composite roll-up — that is Wave-3b owner-side compute.
/// </remarks>
public interface IStandardCatalogSeedStore
{
    /// <summary>Registers an immutable shared catalog seed (append-only; a seed layer only; F2 invariant 1).</summary>
    /// <exception cref="InvalidOperationException">A seed with the same key already exists (seeds are immutable).</exception>
    void Seed(StandardCatalogSeed seed);

    /// <summary>The shared seed for <paramref name="key"/>, or null when none is registered.</summary>
    StandardCatalogSeed? Get(string key);

    /// <summary>Every shared catalog seed (order unspecified).</summary>
    IReadOnlyList<StandardCatalogSeed> List();

    /// <summary>
    /// Registers a tenant scoring OVERRIDE for a seeded catalog — a separate tenant-scoped overlay that
    /// tunes per-field metadata WITHOUT mutating the shared seed (F2). The overlay must be for the seed's
    /// own form and sit at a non-seed layer (Tenant/Instance).
    /// </summary>
    /// <exception cref="InvalidOperationException">No seed exists for the key.</exception>
    /// <exception cref="ArgumentException">The overlay is at a seed layer or targets a different form.</exception>
    void RegisterTenantOverride(TenantId tenant, string key, FieldScoringOverlay tenantOverlay);

    /// <summary>
    /// The effective per-field scoring metadata for <paramref name="key"/> under <paramref name="tenant"/>
    /// — the cascade merge of the seed overlay with any tenant override, keyed by field pointer. This is
    /// METADATA resolution, never a computed composite score.
    /// </summary>
    /// <exception cref="InvalidOperationException">No seed exists for the key.</exception>
    IReadOnlyDictionary<string, FieldScoringMetadata> ResolveScoring(TenantId tenant, string key);
}
