using System.Text.Json;

using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.DataExchangeDefinitions;

/// <summary>A tenant-scoped, immutable binding from a stable key and version to a host-registered exchange kind.</summary>
/// <remarks>
/// Portability export (ADR 0014) is not an exchange kind. Settings never carry credentials, sync
/// cursors, or connection state (ADR 0112 sec-eng C1).
/// </remarks>
public sealed record DataExchangeDefinition
{
    private DefinitionEnvelope<string, string, string, JsonElement> _envelope = new(
        string.Empty,
        string.Empty,
        string.Empty,
        CascadeLayer.Tenant,
        JsonSerializer.SerializeToElement(new { }),
        Array.Empty<DefinitionRequirement>());

    /// <summary>Gets the definition's single control-metadata authority.</summary>
    public DefinitionEnvelope<string, string, string, JsonElement> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the stable definition key within the tenant.</summary>
    public required string Key
    {
        get => Envelope.Identity;
        init => _envelope = Envelope with { Identity = value };
    }

    /// <summary>Gets the immutable semantic version of this definition revision.</summary>
    public required string Version
    {
        get => Envelope.Version;
        init => _envelope = Envelope with { Version = value };
    }

    /// <summary>Gets the tenant that owns the definition.</summary>
    public required string Tenant
    {
        get => Envelope.Tenant;
        init => _envelope = Envelope with { Tenant = value };
    }

    /// <summary>Gets the definition's place in the configuration cascade. Wire-settable so the
    /// projector's authority rule (a non-Tenant layer demands a vendor-vouched pack) can actually
    /// observe an authoritative-tier definition; defaults to <see cref="CascadeLayer.Tenant"/>.</summary>
    public CascadeLayer CascadeLayer
    {
        get => Envelope.CascadeLayer;
        init => _envelope = Envelope with { CascadeLayer = value };
    }

    /// <summary>Gets the definition's transport provenance and lineage.</summary>
    public JsonElement Provenance
    {
        get => Envelope.Provenance;
        init => _envelope = Envelope with { Provenance = value };
    }

    /// <summary>Gets the wire schema version; the initial contract uses value <c>1</c>.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the stable exchange-kind token resolved by the host's descriptor registry — e.g.
    /// <c>banking.statement-import/csv</c>, <c>banking.feed/mock-bank-feed-provider</c>, or
    /// <c>import.erpnext/maria-db-dump</c>.</summary>
    public required string ExchangeKind { get; init; }

    /// <summary>Gets the human-readable title shown to data-exchange consumers.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the exchange-kind-specific settings/mapping object preserved as JSON (e.g. a CSV
    /// column mapping); typed per-kind binding is the host's deepening follow-up.</summary>
    public required JsonElement Settings { get; init; }
}
