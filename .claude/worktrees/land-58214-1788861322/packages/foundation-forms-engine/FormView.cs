using System.Text.Json;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// The rendered, localized projection of a form definition (optionally bound to
/// a specific instance). Carries the form's section / field structure plus —
/// for a populated instance — the readable field values. PII-classified and
/// role-gated fields are returned with a null <see cref="FormViewField.Value"/>
/// and <see cref="FormViewField.IsReadable"/> = <see langword="false"/>; the
/// engine never places PII cleartext into a view (decryption-on-render is a
/// deliberate follow-up, gated on a separate decrypt capability).
/// </summary>
public sealed record FormView(
    FormDefinitionId FormId,
    SemanticVersion Version,
    InternationalizedText? Title,
    InternationalizedText? Description,
    IReadOnlyList<FormViewSection> Sections);

/// <summary>A section of a <see cref="FormView"/> — a titled group of fields.</summary>
/// <remarks>
/// <para><c>Layout</c> / <c>FieldPlacement</c> are projected from the authoring
/// <see cref="FormSection"/> (ADR 0055 Rev 6 — input-group flex/grid layout) and
/// are presentation-only; they do not affect authorization or validation.</para>
/// <para><c>Items</c> is the rendered projection of the authoring section's nested
/// item tree (ADR 0055 Rev 7 — nested sub-form items). Present ⇒ the renderer walks
/// the tree (nested groups / repeatable collections); absent ⇒ the flat <c>Fields</c>
/// list, byte-identical to a pre-Rev-7 view. The server-side render projection that
/// POPULATES <c>Items</c> from a bound instance is a deferred follow-up (shared with
/// the FORM-KEY <c>FormEngine.RenderAsync</c> projection); the shape is carried here so
/// the wire DTO + the React renderer share one canonical contract.</para>
/// </remarks>
public sealed record FormViewSection(
    string Id,
    InternationalizedText Title,
    IReadOnlyList<FormViewField> Fields,
    SectionLayout? Layout = null,
    IReadOnlyDictionary<string, FieldPlacement>? FieldPlacement = null,
    IReadOnlyList<FormViewItem>? Items = null);

/// <summary>
/// One node in a rendered <see cref="FormViewSection"/>'s item tree (ADR 0055 Rev 7 —
/// nested sub-form items). The render-side mirror of the authoring
/// <see cref="FormItem"/>: a <see cref="FormItemKind.Field"/> node carries a rendered
/// <see cref="FormViewField"/> (label/value/readability resolved); a
/// <see cref="FormItemKind.Group"/> / <see cref="FormItemKind.Collection"/> node
/// carries child <see cref="Items"/> ⇒ arbitrary depth. One record + a
/// <see cref="Kind"/> discriminator, so it round-trips with no polymorphic config and
/// mirrors the TS discriminated union.
/// </summary>
/// <param name="Kind">Node kind (Field / Group / Collection / Content / Action).</param>
/// <param name="Key">Field name (Field) or container/block key (Group / Collection / Content / Action).</param>
/// <param name="Field">The rendered field — populated iff <paramref name="Kind"/> is
/// <see cref="FormItemKind.Field"/>.</param>
/// <param name="Items">Child nodes — populated iff <paramref name="Kind"/> is a container.</param>
/// <param name="Cardinality">Collection instance bounds; ignored for Field / Group.</param>
/// <param name="Title">Optional localized container label (nested fieldset legend / collection heading).</param>
/// <param name="Content">F-23: the content nodes — populated iff <paramref name="Kind"/> is
/// <see cref="FormItemKind.Content"/> (a display-only block; never a value).</param>
/// <param name="Action">F-23: the declarative action config — populated iff
/// <paramref name="Kind"/> is <see cref="FormItemKind.Action"/>.</param>
/// <param name="Layout">F-23 zone intents projected from the authoring group (Group only).</param>
/// <param name="Placement">F-23 per-child placement keyed by child item key (Group only).</param>
/// <param name="Table">F-24 (item 5): the collection's tabular presentation config (columns + totals),
/// projected from the authoring <see cref="FormItem.Table"/> (Collection only). Present ⇒ the runner
/// renders the rows as a keyboard-navigable grid; absent ⇒ the legacy stacked "list". Presentation-only;
/// mirrors the TS <c>FormViewItem</c> collection variant's <c>table</c>. Additive — absent ⇒ byte-identical.</param>
public sealed record FormViewItem(
    FormItemKind Kind,
    string Key,
    FormViewField? Field = null,
    IReadOnlyList<FormViewItem>? Items = null,
    Cardinality? Cardinality = null,
    InternationalizedText? Title = null,
    IReadOnlyList<ContentNode>? Content = null,
    FormActionConfig? Action = null,
    SectionLayout? Layout = null,
    IReadOnlyDictionary<string, FieldPlacement>? Placement = null,
    CollectionTableConfig? Table = null);

/// <summary>
/// A single field within a <see cref="FormViewSection"/>.
/// </summary>
/// <param name="Name">The candidate-document property name this field maps to.</param>
/// <param name="Label">Localized field label.</param>
/// <param name="HelpText">Optional localized help text.</param>
/// <param name="ControlHint">Optional UI control hint (e.g. "textarea", "date").</param>
/// <param name="IsSensitive">True when the field is PII-classified — its value is never populated in the view.</param>
/// <param name="IsReadable">True when the active token's roles may read this field's section and the field is not PII-classified.</param>
/// <param name="Value">
/// The field's value, cloned from the instance body so it survives the source
/// document's disposal. <see langword="null"/> when no instance was supplied,
/// the field is absent from the instance body, the field is PII-classified, or
/// the token's roles cannot read the containing section.
/// </param>
/// <param name="Rules">
/// The SPINE-1 rule outcomes projected onto this field SERVER-side (F-12): the merged
/// visibility / required / read-only state, the rule-computed value, and any presentation
/// hint, evaluated by the engine over the bound instance so a runtime form matches the
/// builder's live preview. <see langword="null"/> when no rule targets this field (a
/// rule-free form is byte-identical to the pre-F-12 view).
/// </param>
public sealed record FormViewField(
    string Name,
    InternationalizedText Label,
    InternationalizedText? HelpText,
    string? ControlHint,
    bool IsSensitive,
    bool IsReadable,
    JsonElement? Value,
    FormViewFieldRules? Rules = null);

/// <summary>
/// The SPINE-1 rule outcomes for a <see cref="FormViewField"/>, projected server-side by
/// <see cref="FormEngine.RenderAsync"/> (F-12). Mirrors the rule engine's
/// <c>VisibilityState</c> + computed value + presentation hint so the runtime renderer can
/// apply the SAME visibility / required / read-only / compute / presentation the builder's
/// live preview shows — no client re-evaluation needed for parity.
/// </summary>
/// <param name="Visible">False ⇒ a visibility rule hid this field.</param>
/// <param name="Required">True ⇒ a required rule made this field mandatory.</param>
/// <param name="ReadOnly">True ⇒ a read-only rule locked this field.</param>
/// <param name="Computed">The rule-computed value for a Compute-target field (resolved only);
/// null when the field has no compute rule or the value is pending/errored.</param>
/// <param name="PresentationSeverity">Optional presentation severity (<c>info</c>/<c>warn</c>/<c>error</c>).</param>
/// <param name="PresentationBadge">Optional localized presentation badge text.</param>
/// <param name="PresentationStyleToken">Optional presentation style token.</param>
public sealed record FormViewFieldRules(
    bool Visible = true,
    bool Required = false,
    bool ReadOnly = false,
    JsonElement? Computed = null,
    string? PresentationSeverity = null,
    InternationalizedText? PresentationBadge = null,
    string? PresentationStyleToken = null);
