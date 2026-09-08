using System.Collections.Concurrent;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Scoring;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="IStandardCatalogSeedStore"/>. Shared seeds live in one map; tenant
/// scoring overrides live in a tenant-keyed map — the immutable-seed / mutable-override split is
/// structural (F2 invariant 1). A persistence-backed implementation lives behind the same interface.
/// </summary>
public sealed class InMemoryStandardCatalogSeedStore : IStandardCatalogSeedStore
{
    private readonly ConcurrentDictionary<string, StandardCatalogSeed> _seeds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(TenantId Tenant, string Key), FieldScoringOverlay> _overrides = new();
    private readonly IScoringCascadeResolver _resolver;

    /// <summary>Creates the store over the scoring cascade resolver used for effective-metadata resolution.</summary>
    public InMemoryStandardCatalogSeedStore(IScoringCascadeResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public void Seed(StandardCatalogSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        seed.Validated();

        if (!_seeds.TryAdd(seed.Key, seed))
        {
            throw new InvalidOperationException(
                $"Catalog seed '{seed.Key}' already exists. Seeds are immutable shared templates and cannot be "
                + "re-seeded (F2 invariant 1); a tenant edit must go through RegisterTenantOverride.");
        }
    }

    /// <inheritdoc />
    public StandardCatalogSeed? Get(string key) => _seeds.GetValueOrDefault(key);

    /// <inheritdoc />
    public IReadOnlyList<StandardCatalogSeed> List() => _seeds.Values.ToList();

    /// <inheritdoc />
    public void RegisterTenantOverride(TenantId tenant, string key, FieldScoringOverlay tenantOverlay)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentNullException.ThrowIfNull(tenantOverlay);

        if (!_seeds.TryGetValue(key, out var seed))
        {
            throw new InvalidOperationException($"Cannot override unknown catalog seed '{key}'.");
        }

        if (tenantOverlay.Layer.IsSeed())
        {
            throw new ArgumentException(
                $"A tenant override must be declared at a non-seed layer (Tenant/Instance); got {tenantOverlay.Layer}.",
                nameof(tenantOverlay));
        }

        if (!tenantOverlay.FormDefinition.Equals(seed.FormDefinition))
        {
            throw new ArgumentException(
                $"The override overlay targets form '{tenantOverlay.FormDefinition}' but the seeded catalog's "
                + $"form is '{seed.FormDefinition}'.",
                nameof(tenantOverlay));
        }

        // The seed itself is untouched — this only writes a tenant-scoped override row.
        _overrides[(tenant, key)] = tenantOverlay;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, FieldScoringMetadata> ResolveScoring(TenantId tenant, string key)
    {
        RegistryTenantGuard.Require(tenant);

        if (!_seeds.TryGetValue(key, out var seed))
        {
            throw new InvalidOperationException($"No catalog seed '{key}' to resolve scoring for.");
        }

        var layers = new List<FieldScoringOverlay> { seed.ScoringOverlay };
        if (_overrides.TryGetValue((tenant, key), out var tenantOverlay))
        {
            layers.Add(tenantOverlay);
        }

        // The resolver enforces the raise-strictness-only seed-floor rule (F1) and last-writer-wins per field.
        return _resolver.Resolve(layers);
    }
}
