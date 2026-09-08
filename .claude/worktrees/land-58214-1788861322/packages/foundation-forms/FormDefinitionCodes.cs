namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Stable, locale-independent diagnostic codes for form-definition authoring
/// rejections (ADR 0055 Rev 7). Codes are the cross-tier contract — the client
/// localizes off these (never off the exception's English prose), per the fleet's
/// "validation errors are codes+params, not English literals" rule. Carried on
/// <see cref="Exceptions.FormDefinitionValidationException.Code"/>.
/// </summary>
public static class FormDefinitionCodes
{
    /// <summary>The item tree nests deeper than <see cref="FormTreeLimits.MaxDepth"/>.</summary>
    public const string TreeDepthExceeded = "form.tree.depth_exceeded";

    /// <summary>The item tree has more nodes than <see cref="FormTreeLimits.MaxNodes"/>.</summary>
    public const string TreeTooManyNodes = "form.tree.too_many_nodes";

    /// <summary>A <c>Field</c> item's key is not declared in <see cref="Models.HarborlineOverlay.Fields"/>.</summary>
    public const string TreeUnknownField = "form.tree.unknown_field";

    /// <summary>A <c>Group</c> / <c>Collection</c> container carries no child items.</summary>
    public const string TreeEmptyContainer = "form.tree.empty_container";

    /// <summary>A <c>Field</c> item carries children (only containers may).</summary>
    public const string TreeFieldHasChildren = "form.tree.field_has_children";

    /// <summary>A <c>Collection</c> cardinality is invalid (min &lt; 0, max &lt; min, or max &gt; the cap).</summary>
    public const string TreeBadCardinality = "form.tree.bad_cardinality";

    /// <summary>Two sibling items share a key within the same container.</summary>
    public const string TreeDuplicateKey = "form.tree.duplicate_key";

    /// <summary>An item has an empty key.</summary>
    public const string TreeEmptyKey = "form.tree.empty_key";

    /// <summary>A <c>Reference</c> item (D4) carries no <see cref="Models.FormItem.Reference"/>.</summary>
    public const string TreeReferenceMissingRef = "form.tree.reference_missing_ref";

    /// <summary>A <c>Reference</c> item (D4) carries inline children (its subtree comes from
    /// the referenced unit, not inline).</summary>
    public const string TreeReferenceHasChildren = "form.tree.reference_has_children";

    /// <summary>A <c>Reference</c> item (D4) appears where references are not allowed —
    /// e.g. inside a reusable-unit body (unit-composing-unit is deferred in Phase 1).</summary>
    public const string TreeReferenceNotAllowed = "form.tree.reference_not_allowed";

    // ── Pages / wizard steps (F-14) — fail-closed page invariants ────────────────

    /// <summary>A page has an empty id.</summary>
    public const string PagesEmptyPageId = "form.pages.empty_page_id";

    /// <summary>Two pages share the same id.</summary>
    public const string PagesDuplicatePageId = "form.pages.duplicate_page_id";

    /// <summary>A page carries no sections.</summary>
    public const string PagesEmptyPage = "form.pages.empty_page";

    /// <summary>A page references a section id that is not declared in
    /// <see cref="Models.HarborlineOverlay.Sections"/>.</summary>
    public const string PagesUnknownSection = "form.pages.unknown_section";

    /// <summary>Pages are declared but a section is assigned to no page.</summary>
    public const string PagesUnassignedSection = "form.pages.unassigned_section";

    /// <summary>A section is assigned to more than one page.</summary>
    public const string PagesDuplicateSectionAssignment = "form.pages.duplicate_section_assignment";

    /// <summary>The definition declares more pages than <see cref="FormDefinitionValidation.MaxPages"/>.</summary>
    public const string PagesTooManyPages = "form.pages.too_many_pages";

    /// <summary>A page's <c>VisibleWhen</c> guard is present but empty/whitespace.</summary>
    public const string PagesEmptyVisibleWhen = "form.pages.empty_visible_when";

    /// <summary>A page's <c>VisibleWhen</c> guard is not parseable JSON (F-20 — a typo'd
    /// guard would otherwise admit and silently hide the page forever, fail-closed).</summary>
    public const string PagesInvalidVisibleWhen = "form.pages.invalid_visible_when";

    /// <summary>A page's <c>Checks</c> entry references a rule id that is not declared
    /// in <see cref="Models.HarborlineOverlay.Rules"/> (F-20).</summary>
    public const string PagesUnknownCheck = "form.pages.unknown_check";

    /// <summary>A page's <c>Checks</c> entry references a rule whose action is not
    /// <see cref="Models.RuleActionKind.Validate"/> (F-20 — only Validate rules gate).</summary>
    public const string PagesCheckNotValidate = "form.pages.check_not_validate";

    // ── Async validation checks (F-20) — fail-closed config invariants ───────────

    /// <summary>An async check has an empty id, or two checks share an id.</summary>
    public const string ChecksBadId = "form.checks.bad_id";

    /// <summary>An async check's connector registry key is empty.</summary>
    public const string ChecksEmptyConnector = "form.checks.empty_connector";

    /// <summary>An async check's fail code is empty.</summary>
    public const string ChecksEmptyFailCode = "form.checks.empty_fail_code";

    /// <summary>An async check names a field (target or input) that is not declared in
    /// <see cref="Models.HarborlineOverlay.Fields"/>.</summary>
    public const string ChecksUnknownField = "form.checks.unknown_field";

    /// <summary>The definition declares more async checks than
    /// <see cref="FormDefinitionValidation.MaxAsyncChecks"/>.</summary>
    public const string ChecksTooMany = "form.checks.too_many";

    /// <summary>An async check's debounce is negative or absurdly large (&gt; 60000 ms).</summary>
    public const string ChecksBadDebounce = "form.checks.bad_debounce";

    // ── Tier-2 rule-compile admission (F3 follow-up of the #1671 deep review) ────

    /// <summary>A Tier-2 rule expression fails to COMPILE on the node's rule compiler
    /// (bad grammar, cycle, exceeded static bound, unsupported tier). Rejected at
    /// definition-save: an admitted-but-uncompilable rule set would silently degrade
    /// the submit gate to schema-only while the client still prunes rule-hidden
    /// values — a form the user can render but never submit.</summary>
    public const string RulesUncompilable = "form.rules.uncompilable";

    /// <summary>A page's <c>VisibleWhen</c> guard is parseable JSON but fails to
    /// COMPILE on the node's rule compiler. Rejected at definition-save for the same
    /// reason as <see cref="RulesUncompilable"/> (an erroring guard fail-closes to
    /// hidden at render AND at submit — an uncompilable guard would hide its page
    /// forever).</summary>
    public const string RulesGuardUncompilable = "form.rules.guard_uncompilable";

    // ── Field validation-constraint config (F-20) — fail-closed admission ────────

    /// <summary>A field carries a validation constraint with an unknown code.</summary>
    public const string ConstraintUnknownCode = "form.constraint.unknown_code";

    /// <summary>A constraint's parameter is missing or not parseable for its code
    /// (e.g. a non-numeric <c>minLength</c>, a negative length, a non-numeric bound).</summary>
    public const string ConstraintBadParam = "form.constraint.bad_param";

    /// <summary>A constraint pair conflicts (<c>minLength</c> &gt; <c>maxLength</c>,
    /// or <c>minimum</c> &gt; <c>maximum</c>).</summary>
    public const string ConstraintBoundsConflict = "form.constraint.bounds_conflict";

    /// <summary>A <c>pattern</c> constraint's regular expression does not compile.</summary>
    public const string ConstraintBadPattern = "form.constraint.bad_pattern";

    /// <summary>A constraint is not applicable to the field's type (e.g. <c>minLength</c>
    /// on a number field, <c>minimum</c> on a text field).</summary>
    public const string ConstraintTypeMismatch = "form.constraint.type_mismatch";

    // ── Content / action blocks (F-23) — fail-closed block invariants ────────────

    /// <summary>A <c>Content</c> item carries no content nodes.</summary>
    public const string BlocksContentMissingNodes = "form.blocks.content_missing_nodes";

    /// <summary>A <c>Content</c> item declares more nodes than
    /// <see cref="FormDefinitionValidation.MaxContentNodes"/>.</summary>
    public const string BlocksContentTooManyNodes = "form.blocks.content_too_many_nodes";

    /// <summary>A content node's kind is outside the closed
    /// <see cref="Models.ContentNodeKinds"/> set (fail-closed — e.g. an <c>html</c> node
    /// would be a markup-injection channel).</summary>
    public const string BlocksContentUnknownNodeKind = "form.blocks.content_unknown_node_kind";

    /// <summary>A content node carries no text (no default-locale value).</summary>
    public const string BlocksContentEmptyText = "form.blocks.content_empty_text";

    /// <summary>A heading node's level is outside 1–6.</summary>
    public const string BlocksContentBadHeadingLevel = "form.blocks.content_bad_heading_level";

    /// <summary>A <c>Content</c> / <c>Action</c> block carries child items (blocks are leaves).</summary>
    public const string BlocksBlockHasChildren = "form.blocks.block_has_children";

    /// <summary>A <c>Content</c> item carries no <see cref="Models.FormItem.Content"/> payload,
    /// or an <c>Action</c> item carries no <see cref="Models.FormItem.Action"/> payload.</summary>
    public const string BlocksMissingPayload = "form.blocks.missing_payload";

    /// <summary>An action block's kind is outside the closed
    /// <see cref="Models.FormActionKinds"/> set (the bounded, analyzable v1 vocabulary).</summary>
    public const string BlocksActionUnknownKind = "form.blocks.action_unknown_kind";

    /// <summary>An action block's label is empty (no default-locale value).</summary>
    public const string BlocksActionEmptyLabel = "form.blocks.action_empty_label";

    /// <summary>An <c>open-url</c> action's URL is missing, relative, or not http(s) —
    /// fail-closed against <c>javascript:</c> / <c>data:</c> payloads.</summary>
    public const string BlocksActionBadUrl = "form.blocks.action_bad_url";

    /// <summary>A <c>scroll-to-section</c> action targets a section id that is not declared
    /// in <see cref="Models.HarborlineOverlay.Sections"/> (or is missing entirely).</summary>
    public const string BlocksActionUnknownSection = "form.blocks.action_unknown_section";

    // ── Layout intents (F-23) — fail-closed zone/intent invariants ───────────────

    /// <summary>A layout's <c>CollapseBelow</c> token is outside
    /// <see cref="Models.LayoutIntents.Breakpoints"/>.</summary>
    public const string LayoutUnknownBreakpoint = "form.layout.unknown_breakpoint";

    /// <summary>A layout's <c>Density</c> token is outside
    /// <see cref="Models.LayoutIntents.Densities"/>.</summary>
    public const string LayoutUnknownDensity = "form.layout.unknown_density";

    /// <summary>A layout's / placement's <c>Align</c> token is outside
    /// <see cref="Models.LayoutIntents.Aligns"/>.</summary>
    public const string LayoutUnknownAlign = "form.layout.unknown_align";

    /// <summary>A placement's <c>Width</c> token is outside
    /// <see cref="Models.LayoutIntents.Widths"/>.</summary>
    public const string LayoutUnknownWidth = "form.layout.unknown_width";

    /// <summary>A zone layout / placement is declared on a non-<c>Group</c> item (a
    /// collection's tabular presentation is the separate grid work; fields/blocks carry
    /// placement on their PARENT, not themselves).</summary>
    public const string LayoutZoneNotGroup = "form.layout.zone_not_group";

    // ── Global key uniqueness (item 4 — cross-scope collision) ────────────────────

    /// <summary>An item key (field / block / container) collides with another item key
    /// somewhere ELSE in the definition's item forest. Sibling-uniqueness (<see
    /// cref="TreeDuplicateKey"/>) alone misses a cross-section collision: two sections
    /// each contributing a top-level key <c>notes</c> clash in the ONE candidate object.
    /// The key namespace of a form definition is GLOBAL, so keys must be globally unique.</summary>
    public const string TreeGlobalDuplicateKey = "form.tree.global_duplicate_key";

    // ── Collection tabular presentation (F-24 — grid/table input, item 5) ─────────

    /// <summary>A <see cref="Models.CollectionTableConfig"/> is declared on a non-<c>Collection</c>
    /// item — tabular presentation only applies to a repeatable collection (fail-closed).</summary>
    public const string CollectionTableNotCollection = "form.collection.table_not_collection";

    /// <summary>A collection table's <c>totals</c> names a key that is not a <c>field</c> item in
    /// the collection's row template (a total over a missing / non-field column is meaningless).</summary>
    public const string CollectionTotalUnknownField = "form.collection.total_unknown_field";

    /// <summary>A collection table's <c>columns</c> map names a key that is not a <c>field</c> item
    /// in the collection's row template.</summary>
    public const string CollectionColumnUnknownField = "form.collection.column_unknown_field";

    // ── Placeholder labels (ticket 157 / L1543 — patterns.md §20) ─────────────────

    /// <summary>A field still carries the builder's placeholder label ("New field", or a
    /// dedup variant like "New field 2" / "New field (copy)") at PUBLISH. A Draft may hold
    /// placeholders; a published definition may not — nothing is ever created called
    /// "New field" that you must then find and rename.</summary>
    public const string LabelPlaceholder = "form.label.placeholder";

    /// <summary>A field carries NO usable label at all (no values, or only blank values) at
    /// PUBLISH — strictly worse than a placeholder. Same drafts-exempt posture as
    /// <see cref="LabelPlaceholder"/>.</summary>
    public const string LabelMissing = "form.label.missing";

    // ── Mutually-exclusive block target (F-23 tighten — #1679 review F3) ──────────

    /// <summary>An action block carries a target field for the WRONG kind — an <c>open-url</c>
    /// carrying a <c>sectionId</c>, or a <c>scroll-to-section</c> carrying a <c>url</c>. Inert at
    /// render, but a config the admission tolerates silently is a footgun; fail-closed instead.</summary>
    public const string BlocksActionExtraneousTarget = "form.blocks.action_extraneous_target";

    // ── Classification admission (ticket 094: these codes lived inside the English message) ──

    /// <summary>A field carries a tag in the predefined data-classification system whose kind is
    /// not a recognised one — a forged or typo'd predefined tag no policy binds.</summary>
    public const string AspectUnknownKind = "aspect.unknown_kind";

    /// <summary>An async check feeds a classified field to a connector without
    /// <c>allowsSensitiveInputs</c>.</summary>
    public const string AspectSensitiveInputUnacknowledged = "aspect.sensitive_input_unacknowledged";

    /// <summary>Fallback for a validation refusal whose throw site does not yet carry a stable code.
    /// Ticket 094 slice 2 gives every throw site its own; until then the wire answers a chosen code
    /// rather than the exception's English message.</summary>
    public const string ValidationRefused = "form_definition.validation_refused";
}
