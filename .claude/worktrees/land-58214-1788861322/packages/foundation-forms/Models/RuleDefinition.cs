using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// One cross-field rule attached to a <see cref="FormDefinition"/> (ADR 0055
/// §"Three-tier rules", composing the cross-field-rules-engine UPF).
/// </summary>
/// <remarks>
/// <para>
/// The keystone substrate declares the rule shape; <b>evaluation lives in
/// the separate <c>Harborline.Api.Foundation.RuleEngine</c> package</b> (forthcoming).
/// The keystone is intentionally inert with respect to rule semantics — it
/// stores rules as data so the form authoring UX, the entity-store write
/// path, the migration tooling, and the cross-device CRDT sync all see the
/// same canonical shape regardless of which evaluator runs.
/// </para>
/// <para>
/// <b>Expression contract:</b> the keystone treats <see cref="Expression"/>
/// as an opaque string. The evaluator interprets it according to
/// <see cref="Tier"/> — JSON Schema text, JsonLogic JSON, or Power Fx text.
/// Validation of the expression's grammar is the evaluator's responsibility;
/// the keystone only validates that the field is non-empty.
/// </para>
/// </remarks>
public sealed record RuleDefinition
{
    private DefinitionEnvelope<string, string, TenantId, string?> _envelope;

    /// <summary>Constructs a rule from its control envelope and rule body.</summary>
    [JsonConstructor]
    public RuleDefinition(
        DefinitionEnvelope<string, string, TenantId, string?>? Envelope,
        RuleTier Tier,
        RuleScope Scope,
        string ScopeTarget,
        string Expression,
        RuleActionKind Action,
        InternationalizedText? ErrorMessage = null,
        PresentationHint? Presentation = null)
    {
        _envelope = Envelope ?? new DefinitionEnvelope<string, string, TenantId, string?>(
            string.Empty,
            "0.0.0",
            TenantId.System,
            CascadeLayer.Tenant,
            Provenance: null,
            Array.Empty<DefinitionRequirement>());
        this.Tier = Tier;
        this.Scope = Scope;
        this.ScopeTarget = ScopeTarget;
        this.Expression = Expression;
        this.Action = Action;
        this.ErrorMessage = ErrorMessage;
        this.Presentation = Presentation;
    }

    /// <summary>Constructs a rule through its legacy metadata shape.</summary>
    public RuleDefinition(
        string Id,
        RuleTier Tier,
        RuleScope Scope,
        string ScopeTarget,
        string Expression,
        RuleActionKind Action,
        InternationalizedText? ErrorMessage = null,
        PresentationHint? Presentation = null)
        : this(
            new DefinitionEnvelope<string, string, TenantId, string?>(
                Id,
                "0.0.0",
                TenantId.System,
                CascadeLayer.Tenant,
                Provenance: null,
                Array.Empty<DefinitionRequirement>()),
            Tier,
            Scope,
            ScopeTarget,
            Expression,
            Action,
            ErrorMessage,
            Presentation)
    {
    }

    /// <summary>The definition's single control-metadata authority.</summary>
    public DefinitionEnvelope<string, string, TenantId, string?> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Legacy stable-rule-identifier projection.</summary>
    public string Id
    {
        get => Envelope.Identity;
        init => _envelope = Envelope with { Identity = value };
    }

    /// <summary>Expression language tier.</summary>
    public RuleTier Tier { get; init; }

    /// <summary>The field, section, or whole-schema target scope.</summary>
    public RuleScope Scope { get; init; }

    /// <summary>The field or section identifier, or empty for schema scope.</summary>
    public string ScopeTarget { get; init; }

    /// <summary>Opaque expression text interpreted according to <see cref="Tier"/>.</summary>
    public string Expression { get; init; }

    /// <summary>The action performed when the expression is true.</summary>
    public RuleActionKind Action { get; init; }

    /// <summary>Optional localized validation failure message.</summary>
    public InternationalizedText? ErrorMessage { get; init; }

    /// <summary>Optional presentation action payload.</summary>
    public PresentationHint? Presentation { get; init; }

    /// <summary>Deconstructs the legacy positional record shape.</summary>
    public void Deconstruct(
        out string Id,
        out RuleTier Tier,
        out RuleScope Scope,
        out string ScopeTarget,
        out string Expression,
        out RuleActionKind Action,
        out InternationalizedText? ErrorMessage,
        out PresentationHint? Presentation)
    {
        Id = this.Id;
        Tier = this.Tier;
        Scope = this.Scope;
        ScopeTarget = this.ScopeTarget;
        Expression = this.Expression;
        Action = this.Action;
        ErrorMessage = this.ErrorMessage;
        Presentation = this.Presentation;
    }
}
