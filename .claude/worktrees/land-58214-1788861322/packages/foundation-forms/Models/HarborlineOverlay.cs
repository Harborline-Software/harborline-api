namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Harborline-specific overlay carried alongside a <see cref="FormDefinition"/>'s
/// JSON Schema document (ADR 0055 §"Schema Registry").
/// </summary>
/// <remarks>
/// <para>
/// JSON Schema 2020-12 expresses structural validation but says nothing
/// about UI rendering, internationalization, section-based authorization,
/// or cross-field rules. The overlay carries everything Harborline adds on
/// top — labels, control hints, PII classification, sections, rules — as
/// a single composable record so the schema definition stays a coherent
/// unit on the CRDT sync substrate (ADR 0028) and the audit trail
/// (ADR 0049).
/// </para>
/// <para>
/// <b>Invariants checked by <see cref="IFormDefinitionStore"/>:</b>
/// </para>
/// <list type="bullet">
/// <item><description>Every key in <see cref="Fields"/> MUST correspond to
///   a property declared in the schema's JSON Schema document.</description></item>
/// <item><description>Every field referenced by a <see cref="FormSection.Fields"/>
///   list MUST appear in <see cref="Fields"/>.</description></item>
/// <item><description>Every <see cref="FormSection.Id"/> MUST be unique
///   within the overlay.</description></item>
/// <item><description>Every <see cref="RuleDefinition.Id"/> MUST be unique
///   within the overlay.</description></item>
/// </list>
/// <para>
/// The keystone store enforces these invariants at registration time;
/// callers receive a <see cref="Exceptions.FormDefinitionValidationException"/>
/// on violation (named after the type that failed and the specific invariant
/// violated in the message).
/// </para>
/// </remarks>
/// <param name="Fields">Per-field overlay map keyed by JSON property name.</param>
/// <param name="Sections">Ordered list of sections; section order is the
/// default field-presentation order for the form engine.</param>
/// <param name="Rules">Cross-field rules attached to this schema (the
/// rule-engine package interprets them; the keystone stores them as data).</param>
/// <param name="Title">Optional localized title for the schema.</param>
/// <param name="Description">Optional localized description.</param>
/// <param name="Aspects">Optional SPINE-2 (ADR 0140 D2) form-grain aspect overlay —
/// the coarsest grain the SPINE-2 resolver walks. Additive — absent ⇒ byte-identical
/// pre-SPINE-2 behaviour.</param>
/// <param name="Pages">Optional ordered wizard pages (F-14). Present ⇒ the renderer
/// is a multi-step wizard over the pages (see <see cref="FormPage"/> invariants);
/// null ⇒ a pageless definition, byte-identical to pre-F-14 (one implicit page).</param>
/// <param name="Wizard">Optional wizard chrome settings (F-14). Only meaningful when
/// <paramref name="Pages"/> is present. Additive — absent ⇒ byte-identical.</param>
/// <param name="AsyncChecks">Optional lookup-backed async validation checks (F-20 —
/// see <see cref="AsyncValidationCheck"/>). Additive — absent ⇒ byte-identical.</param>
public sealed record HarborlineOverlay(
    IReadOnlyDictionary<string, FieldOverlay> Fields,
    IReadOnlyList<FormSection> Sections,
    IReadOnlyList<RuleDefinition> Rules,
    InternationalizedText? Title = null,
    InternationalizedText? Description = null,
    AspectOverlay? Aspects = null,
    IReadOnlyList<FormPage>? Pages = null,
    WizardSettings? Wizard = null,
    IReadOnlyList<AsyncValidationCheck>? AsyncChecks = null)
{
    /// <summary>
    /// Empty overlay (no fields, no sections, no rules) — useful for tests
    /// and for bootstrap schemas registered before the authoring UX exists.
    /// </summary>
    public static HarborlineOverlay Empty { get; } = new(
        Fields: new Dictionary<string, FieldOverlay>(),
        Sections: Array.Empty<FormSection>(),
        Rules: Array.Empty<RuleDefinition>());
}
