namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// The kind of a <see cref="FormItem"/> node in a form's recursive item tree
/// (ADR 0055 Rev 7 — nested sub-form items; composes the FORM-2 child-table
/// design 2026-06-30). A form definition is, structurally, a tree of these:
/// leaves are <see cref="Field"/>s, and <see cref="Group"/> / <see cref="Collection"/>
/// are containers whose children are themselves <see cref="FormItem"/>s ⇒ arbitrary depth.
/// </summary>
public enum FormItemKind
{
    /// <summary>A leaf field. <see cref="FormItem.Key"/> is the field name (an entry
    /// in <see cref="HarborlineOverlay.Fields"/>); it carries NO children.</summary>
    Field = 0,

    /// <summary>A <b>cardinality-1</b> nested object — a fieldset that groups child
    /// items under a single object-valued key. Renders as a nested fieldset.</summary>
    Group = 1,

    /// <summary>A <b>cardinality-N</b> repeatable group — a LIST of instances, each an
    /// instance of the child item template. The child-table / grid is the tabular
    /// presentation of a Collection; the rule engine addresses its rows via the
    /// <c>Row</c> / <c>Table</c> scope (SPINE-1).</summary>
    Collection = 2,

    /// <summary>A <b>reference</b> to a reusable form component (D4, ADR 0135 amendment
    /// 2026-07-01). Carries a <see cref="FormItem.Reference"/> (unit id + version selector)
    /// and NO inline children — the child subtree comes from the referenced
    /// <see cref="ReusableUnit"/> at resolution time (reuse-by-reference, not copy). Its
    /// <see cref="FormItem.Key"/> is the reference-site container key the unit's values nest
    /// under. <see cref="IReuseResolver"/> expands it into a <see cref="Group"/> and merges
    /// the unit's fields CP-locked; the unit's fields are NOT declared in the consuming
    /// definition's own overlay (they resolve from the unit).</summary>
    Reference = 3,

    /// <summary>A <b>static content block</b> (F-23 — layout breadth): authored display-only
    /// heading/paragraph text (<see cref="FormItem.Content"/>). A NON-INPUT leaf — it binds
    /// no instance value, never appears in the candidate document / submitted body / rule
    /// surface, and carries NO children.</summary>
    Content = 4,

    /// <summary>An <b>action block</b> (F-23): a declarative non-submit action affordance
    /// (<see cref="FormItem.Action"/> — label + a bounded action kind + one target; config
    /// only, never imperative code). A NON-INPUT leaf like <see cref="Content"/>.</summary>
    Action = 5,
}

/// <summary>
/// The instance-count bounds of a <see cref="FormItemKind.Collection"/> (ADR 0055 Rev 7).
/// A Group is implicitly cardinality-1; a Collection declares how many row instances
/// are permitted.
/// </summary>
/// <param name="Min">Minimum required instances (default 0).</param>
/// <param name="Max">Maximum permitted instances, or <see langword="null"/> for unbounded
/// (still subject to the fail-closed <see cref="FormTreeLimits.MaxCollectionInstances"/>
/// authoring cap — an unbounded Collection remains evaluation-bounded).</param>
public sealed record Cardinality(int Min = 0, int? Max = null);

/// <summary>
/// Per-column presentation config for a table-presented <see cref="FormItemKind.Collection"/>
/// (F-24 — grid/table input, item 5), keyed by the row-template field key on
/// <see cref="CollectionTableConfig.Columns"/>. Bounded intent tokens only (reusing the F-23
/// <see cref="LayoutIntents"/> vocabularies) — never pixels.
/// </summary>
/// <param name="Width">Column width intent — one of <see cref="LayoutIntents.Widths"/>. Null ⇒ auto.</param>
/// <param name="Align">Column cell alignment — one of <see cref="LayoutIntents.Aligns"/>. Null ⇒ start.</param>
public sealed record CollectionColumn(string? Width = null, string? Align = null);

/// <summary>
/// The tabular presentation of a <see cref="FormItemKind.Collection"/> (F-24 — grid/table input,
/// item 5). PRESENTATION ONLY — it rides the Collection <see cref="FormItem"/> the way F-23 zone
/// <see cref="FormItem.Layout"/> rides a Group; it does NOT change the row data shape (the
/// collection is still cardinality-N rows of the row-template subtree) and is NOT a new kind.
/// Presence ⇒ the renderer lays the rows out as a keyboard-navigable grid with a header row (+
/// optional totals footer); absence ⇒ the legacy stacked "list" presentation, byte-identical to a
/// pre-item-5 definition. Rejected on any non-Collection kind
/// (<c>form.collection.table_not_collection</c>); unknown tokens / unknown total keys are 422.
/// </summary>
/// <param name="Columns">Optional per-column config keyed by row-template field key.</param>
/// <param name="Totals">Row-template numeric/currency field keys to show a column SUM total for
/// in a footer totals row. Each MUST be a <see cref="FormItemKind.Field"/> in the row template.</param>
public sealed record CollectionTableConfig(
    IReadOnlyDictionary<string, CollectionColumn>? Columns = null,
    IReadOnlyList<string>? Totals = null);

/// <summary>
/// One node in a <see cref="FormDefinition"/>'s recursive form-item tree
/// (ADR 0055 Rev 7 — nested sub-form items). The structural core that lets a form
/// nest sub-forms to arbitrary depth: a <see cref="FormItemKind.Group"/> or
/// <see cref="FormItemKind.Collection"/> carries child <see cref="Items"/> that are
/// themselves <see cref="FormItem"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single canonical field registry.</b> A <see cref="FormItemKind.Field"/> node
/// references a field name in <see cref="HarborlineOverlay.Fields"/> — nested fields
/// draw their label / control-hint / PII classification from the SAME per-field
/// overlay map as top-level fields. There is no parallel per-node overlay; the tree
/// is a STRUCTURAL overlay on top of the flat field registry.
/// </para>
/// <para>
/// <b>Back-compat.</b> The tree is additive. A flat form carries no
/// <see cref="FormSection.Items"/> (⇒ <see langword="null"/>) and is a depth-1 tree
/// by definition — its sections' flat <see cref="FormSection.Fields"/> list is the
/// legacy presentation, unchanged.
/// </para>
/// <para>
/// <b>Discriminated-union shape (not a polymorphic hierarchy).</b> One record with a
/// <see cref="Kind"/> discriminator + nullable payloads round-trips through
/// System.Text.Json with NO polymorphic configuration and mirrors 1:1 onto the TS
/// discriminated union in <c>@harborline-software/api-contracts/forms.ts</c> — which keeps the
/// TS↔.NET structural-parity test tractable.
/// </para>
/// <para>
/// <b>Value tree ⇒ acyclic by construction.</b> A <see cref="FormItem"/> is an
/// immutable value tree, so a cycle is structurally impossible; the fail-closed
/// bound that matters is over-DEPTH and over-FAN-OUT, enforced at authoring by
/// <see cref="FormDefinitionValidation"/> against <see cref="FormTreeLimits"/>
/// (INV-S2 extension).
/// </para>
/// </remarks>
/// <param name="Kind">The node kind (Field / Group / Collection).</param>
/// <param name="Key">For <see cref="FormItemKind.Field"/> the field name (an entry in
/// <see cref="HarborlineOverlay.Fields"/>); for a container the object/collection key
/// (the instance property the child values nest under, and the rule-engine section id
/// for a Collection). MUST be non-empty and unique among its siblings.</param>
/// <param name="Items">Child items — non-empty for <see cref="FormItemKind.Group"/> /
/// <see cref="FormItemKind.Collection"/>, <see langword="null"/> for a
/// <see cref="FormItemKind.Field"/>.</param>
/// <param name="Cardinality">Instance-count bounds for a
/// <see cref="FormItemKind.Collection"/>; ignored for Field / Group.</param>
/// <param name="Title">Optional localized container label (rendered as the nested
/// fieldset legend / collection heading). Absent for a Field (its label comes from
/// the field overlay).</param>
/// <param name="Reference">For a <see cref="FormItemKind.Reference"/> node, the reference to
/// the reusable unit (id + version selector). MUST be non-null for a Reference node and null
/// for every other kind. Additive — a pre-D4 definition carries no reference nodes and is
/// byte-identical to before.</param>
/// <param name="Content">For a <see cref="FormItemKind.Content"/> node (F-23), the ordered
/// content nodes (≥1; closed node kinds, admission-validated). Null for every other kind.
/// Additive — a pre-F-23 definition carries no content nodes and is byte-identical.</param>
/// <param name="Action">For a <see cref="FormItemKind.Action"/> node (F-23), the declarative
/// action config (bounded kinds, admission-validated). Null for every other kind. Additive.</param>
/// <param name="Layout">For a <see cref="FormItemKind.Group"/> node (F-23 zone intents), an
/// optional 2D layout for the group's children — the "multi-column zone" (e.g. a wrapping
/// photo strip, a two-column pair collapsing on a narrow pane). Presentation-only; rejected
/// on any other kind (<c>form.layout.zone_not_group</c>). Additive — absent ⇒ the legacy
/// single-column group, byte-identical to a pre-F-23 definition.</param>
/// <param name="Placement">For a <see cref="FormItemKind.Group"/> zone (F-23), per-child
/// placement intents (span/grow/width/align) keyed by child item key. Presentation-only.</param>
/// <param name="Table">For a <see cref="FormItemKind.Collection"/> node (F-24 — grid/table input,
/// item 5), the optional tabular presentation config (columns + totals). Presentation-only;
/// rejected on any other kind (<c>form.collection.table_not_collection</c>). Additive — absent ⇒
/// the legacy stacked "list" presentation, byte-identical to a pre-item-5 definition.</param>
/// <param name="Aspects">For a <see cref="FormItemKind.Group"/> / <see cref="FormItemKind.Collection"/>
/// container node (SPINE-2, ADR 0140 D2 — item 6), the optional container-grain aspect overlay
/// the resolver walks as a grain BETWEEN the section and each nested field (monotonic-union
/// classification: a container tag flows to its children). Ignored for non-container kinds.
/// Additive — absent ⇒ byte-identical to a pre-SPINE-2 definition.</param>
public sealed record FormItem(
    FormItemKind Kind,
    string Key,
    IReadOnlyList<FormItem>? Items = null,
    Cardinality? Cardinality = null,
    InternationalizedText? Title = null,
    ReusableUnitRef? Reference = null,
    IReadOnlyList<ContentNode>? Content = null,
    FormActionConfig? Action = null,
    SectionLayout? Layout = null,
    IReadOnlyDictionary<string, FieldPlacement>? Placement = null,
    CollectionTableConfig? Table = null,
    AspectOverlay? Aspects = null)
{
    /// <summary>A leaf field node referencing <paramref name="key"/> in the overlay's field map.</summary>
    public static FormItem OfField(string key) => new(FormItemKind.Field, key);

    /// <summary>A cardinality-1 nested group of <paramref name="items"/>.</summary>
    public static FormItem OfGroup(string key, IReadOnlyList<FormItem> items, InternationalizedText? title = null)
        => new(FormItemKind.Group, key, items, Title: title);

    /// <summary>A cardinality-N repeatable collection whose row template is <paramref name="items"/>.</summary>
    public static FormItem OfCollection(
        string key,
        IReadOnlyList<FormItem> items,
        Cardinality? cardinality = null,
        InternationalizedText? title = null)
        => new(FormItemKind.Collection, key, items, cardinality ?? new Cardinality(), title);

    /// <summary>A reference (D4) to a reusable unit, nesting the unit's resolved subtree under
    /// <paramref name="key"/> at resolution time (reuse-by-reference, not copy).</summary>
    public static FormItem OfReference(string key, ReusableUnitRef reference)
        => new(FormItemKind.Reference, key, Reference: reference);

    /// <summary>A static content block (F-23) of ordered heading/paragraph nodes.</summary>
    public static FormItem OfContent(string key, IReadOnlyList<ContentNode> content)
        => new(FormItemKind.Content, key, Content: content);

    /// <summary>An action block (F-23) carrying a declarative action config.</summary>
    public static FormItem OfAction(string key, FormActionConfig action)
        => new(FormItemKind.Action, key, Action: action);
}
