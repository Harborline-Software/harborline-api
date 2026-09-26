using System.Text.Json;

using Harborline.Blocks.BuilderDefinitions;
using Harborline.Contracts.Fields;
using ContractRuleActionKind = Harborline.Contracts.Forms.RuleActionKind;
using ContractRuleDefinition = Harborline.Contracts.Forms.RuleDefinition;
using ContractRuleScope = Harborline.Contracts.Forms.RuleScope;
using ContractRuleTier = Harborline.Contracts.Forms.RuleTier;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>
/// The Layout content supplied by installed packs and the node's record catalogue at publication.
/// T-734 will add a dedicated pack content kind for page declarations; until then this is the host
/// seam that carries the page declarations its pack projection has discovered.
/// </summary>
public sealed record LayoutPublishState(
    IReadOnlyList<LayoutRecordFieldDescriptor>? RecordFields = null,
    IReadOnlyList<LayoutPageLayoutDefinition>? InstalledPackPageLayouts = null,
    IReadOnlyList<LayoutPageMasterDefinition>? InstalledPackPageMasters = null);

/// <summary>
/// Selects which bound registers the host supplies. Production uses the default; the explicit
/// omissions make the host's fail-closed boundary directly provable in-process.
/// </summary>
public sealed record LayoutPublishRegisterOptions(
    bool SupplyFieldControls = true,
    bool SupplyPages = true,
    bool SupplyValidationRules = true);

/// <summary>
/// T-733 publish-time Layout host seam. It binds the node's released controls and validation rules,
/// plus pack-declared pages and record fields, before platform admission runs. T-735 will compose this
/// seam into the HTTP/DI publication route; it is deliberately usable in-process now.
/// </summary>
public sealed class LayoutPublishRegisters(
    LayoutPublishState state,
    LayoutPublishRegisterOptions? options = null)
{
    /// <summary>The released text control shared with the node's Forms vocabulary.</summary>
    public const string TextControlId = "text";

    /// <summary>The released currency control shared with the node's Forms vocabulary.</summary>
    public const string CurrencyControlId = "currency";

    /// <summary>The node-owned validation rule available to Layout capture blocks.</summary>
    public const string RequiredValueRuleId = "rules.value-required";

    private readonly LayoutPublishRegisterOptions _options = options ?? new();

    /// <summary>
    /// The released field-control catalogue, built once for the process. The platform's
    /// <see cref="LayoutFieldControlRegistry"/> registers each control's parameter schema into a
    /// process-wide <c>Json.Schema</c> registry keyed by the control id (T-733 finding): rebuilding
    /// this registry on every publish call re-registers the same "text"/"currency" URIs and throws
    /// <c>JsonSchemaException: Overwriting registered schemas is not permitted</c> on the second call.
    /// The released catalogue never varies per request, so it is built once and reused.
    /// </summary>
    private static readonly LayoutFieldControlDescriptor[] ReleasedControls =
    [
        new(TextControlId, [FieldScalarValueShape.Text]),
        new(CurrencyControlId, [FieldScalarValueShape.Number], JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { precision = new { type = "integer", minimum = 0 } },
            additionalProperties = false,
        })),
    ];

    private static readonly Lazy<LayoutFieldControlRegistry> ReleasedFieldControls =
        new(() => new LayoutFieldControlRegistry(ReleasedControls));

    /// <summary>Builds the actual platform admission input from this node's publish state.</summary>
    public LayoutHostRegisters Build()
    {
        var controls = _options.SupplyFieldControls ? ReleasedFieldControls.Value : null;
        var pages = _options.SupplyPages
            ? new LayoutPageRegistry(state.InstalledPackPageLayouts ?? [], state.InstalledPackPageMasters ?? [])
            : null;
        var rules = _options.SupplyValidationRules ? new LayoutValidationRuleRegistry(ReleasedValidationRules) : null;

        return new LayoutHostRegisters(
            LayoutBlockKindRegistry.Platform,
            FieldControls: controls,
            Pages: pages,
            ValidationRules: rules,
            // Publication checks a named control against the actual record field it captures. The
            // catalogue can be empty, but it remains a real register rather than a permissive fallback.
            Fields: controls is null ? null : new LayoutRecordFieldRegistry(state.RecordFields ?? []));
    }

    /// <summary>Runs the platform's one publish-time admission path against the host-built registers.</summary>
    public void ValidateForPublish(LayoutDefinition definition)
        => LayoutDefinitionAdmission.ValidateForPublish(definition, Build());

    private static readonly ContractRuleDefinition[] ReleasedValidationRules =
    [
        new()
        {
            Id = RequiredValueRuleId,
            Tier = ContractRuleTier.JsonSchema,
            Scope = ContractRuleScope.Schema,
            ScopeTarget = string.Empty,
            Expression = "{\"minLength\":1}",
            Action = ContractRuleActionKind.Validate,
        },
    ];
}
