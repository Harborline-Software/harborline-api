using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Catalog.Templates;

/// <summary>
/// A user-authored artifact (form, checklist, report, …) expressed as a data
/// schema plus a UI schema. Stored as metadata; rendered by an adapter.
/// </summary>
public sealed record TemplateDefinition
{
    private DefinitionEnvelope<string, string, TenantId, string?> _envelope;

    /// <summary>Constructs a catalog template from its control envelope and template body.</summary>
    [JsonConstructor]
    public TemplateDefinition(
        DefinitionEnvelope<string, string, TenantId, string?>? Envelope,
        TemplateKind Kind,
        JsonNode DataSchema,
        JsonNode UiSchema,
        string? DisplayName = null,
        string? Description = null,
        string? Locale = null)
    {
        _envelope = Envelope ?? new DefinitionEnvelope<string, string, TenantId, string?>(
            string.Empty,
            string.Empty,
            TenantId.System,
            CascadeLayer.Base,
            Provenance: null,
            Array.Empty<DefinitionRequirement>());
        this.Kind = Kind;
        this.DataSchema = DataSchema ?? throw new ArgumentNullException(nameof(DataSchema));
        this.UiSchema = UiSchema ?? throw new ArgumentNullException(nameof(UiSchema));
        this.DisplayName = DisplayName;
        this.Description = Description;
        this.Locale = Locale;
    }

    /// <summary>Constructs a catalog template through its legacy metadata shape.</summary>
    public TemplateDefinition(
        string Id,
        string Version,
        TemplateKind Kind,
        JsonNode DataSchema,
        JsonNode UiSchema,
        string? BaseRef = null,
        string? DisplayName = null,
        string? Description = null,
        string? Locale = null)
        : this(
            new DefinitionEnvelope<string, string, TenantId, string?>(
                Id,
                Version,
                TenantId.System,
                CascadeLayer.Base,
                BaseRef,
                Array.Empty<DefinitionRequirement>()),
            Kind,
            DataSchema,
            UiSchema,
            DisplayName,
            Description,
            Locale)
    {
    }

    /// <summary>The definition's single control-metadata authority.</summary>
    public DefinitionEnvelope<string, string, TenantId, string?> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Legacy stable-identifier projection.</summary>
    public string Id
    {
        get => Envelope.Identity;
        init => _envelope = Envelope with { Identity = value };
    }

    /// <summary>Legacy semantic-version projection.</summary>
    public string Version
    {
        get => Envelope.Version;
        init => _envelope = Envelope with { Version = value };
    }

    /// <summary>Template classification.</summary>
    public TemplateKind Kind { get; init; }

    /// <summary>JSON Schema 2020-12 document describing the data shape.</summary>
    public JsonNode DataSchema { get; init; }

    /// <summary>Renderer-facing schema.</summary>
    public JsonNode UiSchema { get; init; }

    /// <summary>Legacy lineage projection for the optional base id and version.</summary>
    public string? BaseRef
    {
        get => Envelope.Provenance;
        init => _envelope = Envelope with { Provenance = value };
    }

    /// <summary>Optional human-readable label.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Optional long-form description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional BCP-47 locale tag.</summary>
    public string? Locale { get; init; }

    /// <summary>Deconstructs the legacy positional record shape.</summary>
    public void Deconstruct(
        out string Id,
        out string Version,
        out TemplateKind Kind,
        out JsonNode DataSchema,
        out JsonNode UiSchema,
        out string? BaseRef,
        out string? DisplayName,
        out string? Description,
        out string? Locale)
    {
        Id = this.Id;
        Version = this.Version;
        Kind = this.Kind;
        DataSchema = this.DataSchema;
        UiSchema = this.UiSchema;
        BaseRef = this.BaseRef;
        DisplayName = this.DisplayName;
        Description = this.Description;
        Locale = this.Locale;
    }
}
