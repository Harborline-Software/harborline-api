using System.Security.Cryptography;
using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>Internal read identity; never a response DTO or an alternate way to return denied fields.</summary>
public sealed record CatalogueFormSourceIdentity(TenantId Tenant, string Kind, string Id, string Version,
    CatalogueFieldSourceBinding Binding);

public sealed record CatalogueFieldPayload(CatalogueFormSourceIdentity Identity, CatalogueFieldCoordinate Coordinate, JsonElement Value);

public interface ICatalogueFormSources
{
    CatalogueFormSourceHandle? Resolve(TenantId tenant, CatalogueFieldCoordinate coordinate);
}

/// <summary>A metadata-only handle. Binding creates a single-field closure, never a body reader.</summary>
public abstract class CatalogueFormSourceHandle(CatalogueFormSourceIdentity identity)
{
    public CatalogueFormSourceIdentity Identity { get; } = identity;
    public abstract Func<CatalogueFieldPayload> Bind(CatalogueFieldCoordinate coordinate, CatalogueFieldSourceBinding binding);
}

/// <summary>
/// Lifecycle-owned read index, hydrated only by admitted persistence operations. A cold revision is
/// unavailable: resolving it never loads a definition. Mutation invalidation and payload reads share a
/// lock; persistence and index publication are serialized so a stale write cannot revive a source.
/// </summary>
public sealed class CatalogueFormSources : ICatalogueFormSources, IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim mutations = new(1);
    private readonly Dictionary<DefinitionCoordinates, Snapshot> sources = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async ValueTask<FormDefinition> PersistAsync(DefinitionCoordinates coordinates, Func<ValueTask<FormDefinition>> persist)
    {
        await mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) sources.Remove(coordinates);
            var definition = await persist().ConfigureAwait(false);
            if (definition.Status == FormDefinitionStatus.Published)
            {
                // Hash only immutable source-definition bytes, not lifecycle status or transition times.
                byte[] bytes;
                try
                {
                    bytes = CanonicalJson.Serialize(new { definition.Envelope, definition.SchemaRef,
                        definition.Overlay, definition.CatalogueFieldSource });
                }
                catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
                {
                    // Legacy persistence admits a wider value domain than canonical field bindings.
                    // Keep its successful write intact, but leave this revision unavailable to typed reads.
                    return definition;
                }
                var source = definition.PackSource;
                var binding = new CatalogueFieldSourceBinding("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    source is null ? new("tenant") : new("pack", source.PackId, source.PackVersion));
                var identity = new CatalogueFormSourceIdentity(definition.Tenant, CatalogueFieldSourceContract.SourceKind,
                    definition.Id.Value, definition.Version.ToString(), binding);
                lock (sync) sources[coordinates] = new Snapshot(identity, definition);
            }
            return definition;
        }
        finally { mutations.Release(); }
    }

    public CatalogueFormSourceHandle? Resolve(TenantId tenant, CatalogueFieldCoordinate coordinate)
    {
        var coordinates = new DefinitionCoordinates(tenant, coordinate.Id, coordinate.Version);
        lock (sync) return sources.TryGetValue(coordinates, out var snapshot)
            ? new Handle(this, coordinates, snapshot) : null;
    }

    public void Dispose() => mutations.Dispose();

    private sealed record Snapshot(CatalogueFormSourceIdentity Identity, FormDefinition Definition);

    private sealed class Handle(CatalogueFormSources owner, DefinitionCoordinates key, Snapshot snapshot)
        : CatalogueFormSourceHandle(snapshot.Identity)
    {
        public override Func<CatalogueFieldPayload> Bind(CatalogueFieldCoordinate coordinate, CatalogueFieldSourceBinding binding)
        {
            _ = CatalogueFieldSourceContract.ParseCoordinate(JsonSerializer.SerializeToElement(coordinate));
            if (coordinate.Kind != Identity.Kind || coordinate.Id != Identity.Id || coordinate.Version != Identity.Version
                || binding != Identity.Binding)
                throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceBindingMismatch);
            return () =>
            {
                lock (owner.sync)
                {
                    if (!owner.sources.TryGetValue(key, out var current) || !ReferenceEquals(current, snapshot))
                        throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceChangedAfterAuthorization);
                    var definition = snapshot.Definition;
                    var value = coordinate.Field switch
                    {
                        "formId" => JsonSerializer.SerializeToElement(definition.Id.Value),
                        "title" => JsonSerializer.SerializeToElement(definition.Overlay.Title, Json),
                        "version" => JsonSerializer.SerializeToElement(definition.Version.ToString()),
                        "cascadeLayer" => JsonSerializer.SerializeToElement(definition.Envelope.CascadeLayer.ToString()),
                        _ => throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.UnsupportedCoordinate),
                    };
                    return new CatalogueFieldPayload(Identity, coordinate, value);
                }
            };
        }
    }
}
