using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Documents.Model;

/// <summary>
/// A <b>template</b> (#111 design §0/§1): a named, versioned, cascade-homed declarative artifact that
/// describes how to turn a record into a document. It is mutable and versioned (draft→publish, like a
/// rule); the <i>document</i> it renders is frozen at issue-time and never changes (like a ledger entry).
/// A template edit NEVER retroactively alters an already-issued document — it changes only what future
/// issues render (the accounting-integrity invariant, §0).
/// </summary>
/// <remarks>
/// The template carries no formatting logic and no second templating language: a merge field stores a
/// scope-grammar <c>var</c> reference resolved by the <c>DocumentMergeContextAdapter</c> (§1.2), and the
/// renderer applies format from the config cascade in the <i>document's</i> locale (§1.4/§1.5). Enters the
/// cascade as an additive <c>PackContentKind.TemplateDefinition</c> (§1.1); its version compares via the
/// S-8 monotonic <c>PackVersion</c> comparator the rule registry reuses.
/// </remarks>
public sealed record TemplateDefinition
{
    private DefinitionEnvelope<string, string, TenantId, string?> _envelope;

    /// <summary>Constructs a document template from its control envelope and render body.</summary>
    [JsonConstructor]
    public TemplateDefinition(
        DefinitionEnvelope<string, string, TenantId, string?>? Envelope,
        string DocumentType,
        RecordTypeBinding RecordType,
        DocumentLocalePolicy Locale,
        DocumentStyleRef? Style,
        IReadOnlyList<DocumentBlock> Structure)
    {
        _envelope = Envelope ?? new DefinitionEnvelope<string, string, TenantId, string?>(
            string.Empty,
            string.Empty,
            TenantId.System,
            CascadeLayer.Pack,
            Provenance: null,
            Array.Empty<DefinitionRequirement>());
        this.DocumentType = DocumentType;
        this.RecordType = RecordType;
        this.Locale = Locale;
        this.Style = Style;
        this.Structure = Structure ?? throw new ArgumentNullException(nameof(Structure));
    }

    /// <summary>Constructs a document template through its legacy metadata shape.</summary>
    public TemplateDefinition(
        string Key,
        string Version,
        string DocumentType,
        RecordTypeBinding RecordType,
        DocumentLocalePolicy Locale,
        DocumentStyleRef? Style,
        IReadOnlyList<DocumentBlock> Structure)
        : this(
            new DefinitionEnvelope<string, string, TenantId, string?>(
                Key,
                Version,
                TenantId.System,
                CascadeLayer.Pack,
                Provenance: null,
                Array.Empty<DefinitionRequirement>()),
            DocumentType,
            RecordType,
            Locale,
            Style,
            Structure)
    {
    }

    /// <summary>The definition's single control-metadata authority.</summary>
    public DefinitionEnvelope<string, string, TenantId, string?> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Legacy stable-key projection.</summary>
    public string Key
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

    /// <summary>Open document-type discriminator.</summary>
    public string DocumentType { get; init; }

    /// <summary>The record type and pinned version this template merges from.</summary>
    public RecordTypeBinding RecordType { get; init; }

    /// <summary>The document's locale-resolution policy.</summary>
    public DocumentLocalePolicy Locale { get; init; }

    /// <summary>Optional document visual identity.</summary>
    public DocumentStyleRef? Style { get; init; }

    /// <summary>The ordered render block tree.</summary>
    public IReadOnlyList<DocumentBlock> Structure { get; init; }

    /// <summary>Deconstructs the legacy positional record shape.</summary>
    public void Deconstruct(
        out string Key,
        out string Version,
        out string DocumentType,
        out RecordTypeBinding RecordType,
        out DocumentLocalePolicy Locale,
        out DocumentStyleRef? Style,
        out IReadOnlyList<DocumentBlock> Structure)
    {
        Key = this.Key;
        Version = this.Version;
        DocumentType = this.DocumentType;
        RecordType = this.RecordType;
        Locale = this.Locale;
        Style = this.Style;
        Structure = this.Structure;
    }
}

/// <summary>The template's binding to a versioned record type (the D7-pin philosophy applied to content, §5.4).</summary>
/// <param name="RecordType">The record type the template merges (e.g. <c>invoice</c>).</param>
/// <param name="Version">The record-type version the template was authored against; drives needs-review staleness.</param>
public sealed record RecordTypeBinding(string RecordType, string Version);

/// <summary>How the <i>document's</i> render locale is resolved (§1.5). Distinct from the authoring/UI locale.</summary>
/// <param name="Kind">Resolution strategy.</param>
/// <param name="Tag">The BCP-47 tag when <see cref="LocalePolicyKind.Fixed"/> (ignored otherwise).</param>
public sealed record DocumentLocalePolicy(LocalePolicyKind Kind, string? Tag = null)
{
    /// <summary>A template always rendered in one fixed locale (e.g. <c>en-US</c>).</summary>
    public static DocumentLocalePolicy Fixed(string tag) => new(LocalePolicyKind.Fixed, tag);

    /// <summary>Resolve to the recipient's preferred locale carried on the record (the common case).</summary>
    public static DocumentLocalePolicy FromRecord { get; } = new(LocalePolicyKind.FromRecord);

    /// <summary>Resolve to the org/instance default locale.</summary>
    public static DocumentLocalePolicy FromInstance { get; } = new(LocalePolicyKind.FromInstance);
}

/// <summary>The <see cref="DocumentLocalePolicy"/> strategy (§1.5).</summary>
public enum LocalePolicyKind
{
    /// <summary>A fixed BCP-47 tag.</summary>
    Fixed = 0,

    /// <summary>The recipient locale carried on the record.</summary>
    FromRecord = 1,

    /// <summary>The org/instance default.</summary>
    FromInstance = 2,
}

/// <summary>
/// The rendered document's visual identity (§1.6) — <b>template data</b> in a separate register from the
/// Harborline App's quiet-product chrome. The platform ships the mechanism + a neutral default; the identity is
/// tenant content. For the en-US keystone this carries the brand name rendered as the header masthead;
/// image/logo assets (and their editor-preview sanitization, F7) arrive with the D3 editor surface.
/// </summary>
/// <param name="BrandName">The brand name printed in the document masthead.</param>
/// <param name="LogoAssetRef">Optional reference to a logo asset in the blob store (rendered later; sanitized at the editor-preview boundary — F7/D3).</param>
public sealed record DocumentStyleRef(string? BrandName = null, string? LogoAssetRef = null);
