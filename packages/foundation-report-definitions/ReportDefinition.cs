using System.Text.Json;

using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.ReportDefinitions;

/// <summary>A tenant-scoped, immutable binding from a stable key and version to a registered report kind.</summary>
public sealed record ReportDefinition
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

    /// <summary>Gets the stable report-kind token resolved by the host's descriptor registry.</summary>
    public required string ReportKind { get; init; }

    /// <summary>Gets the human-readable title shown to report consumers.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the cartridge-specific parameter object preserved as JSON.</summary>
    public required JsonElement Parameters { get; init; }
}
