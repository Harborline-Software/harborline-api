using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// The single bounded recursive form-item-tree validator (ADR 0055 Rev 7 — nested sub-form
/// items; the INV-S2 resource-bounds invariant extended to the tree). Shared by
/// <see cref="FormDefinitionValidation"/> (a definition's section trees) and
/// <see cref="ReusableUnitValidation"/> (a D4 reusable-unit body) so the two enforce
/// identical structural bounds and cannot drift apart.
/// </summary>
/// <remarks>
/// Fail-closed on every violation via the caller-supplied <c>fail</c> factory (which builds
/// the caller's exception type carrying the offending id + a stable code). Enforces:
/// sibling-key uniqueness, non-empty keys, Field items reference a declared field and carry
/// no children, containers carry ≥1 child, Collection cardinality is well-formed and within
/// the instance cap, the tree does not exceed the depth cap, the TOTAL node count (shared
/// across sections via the <c>totalNodes</c> accumulator) does not exceed the node cap,
/// (D4) a Reference node carries a ref, no inline children, and appears only where references
/// are permitted, and (F-23) Content/Action blocks are well-formed leaves (payload present,
/// closed node/action kinds, http(s)-only URLs, declared scroll targets) and zone layout
/// intents appear only on Group items with tokens from the closed
/// <see cref="LayoutIntents"/> vocabularies.
/// </remarks>
internal static class FormItemTreeValidator
{
    /// <summary>
    /// Validates one level of an item tree, recursing into containers.
    /// </summary>
    /// <param name="items">The items at this level.</param>
    /// <param name="isDeclaredField">Predicate: is this Field-item key declared in the owning
    /// scope's field map?</param>
    /// <param name="isDeclaredSection">Predicate: is this section id declared in the owning
    /// definition (F-23 — validates a <c>scroll-to-section</c> action target)? A reusable-unit
    /// body declares NO sections, so its caller passes an always-false predicate — a
    /// scroll-to-section action inside a unit body is rejected fail-closed (its target cannot
    /// be known until the unit is referenced).</param>
    /// <param name="limits">Fail-closed authoring bounds.</param>
    /// <param name="allowReferences">Whether <see cref="FormItemKind.Reference"/> nodes are
    /// permitted here (true for a definition's section trees; false inside a unit body — a
    /// nested reference is rejected in Phase 1).</param>
    /// <param name="fail">Factory turning (message, code) into the caller's exception type.</param>
    /// <param name="totalNodes">Running node count shared across the whole tree.</param>
    /// <param name="depth">Current nesting depth (top-level items are depth 1).</param>
    internal static void ValidateBounded(
        IReadOnlyList<FormItem> items,
        Func<string, bool> isDeclaredField,
        Func<string, bool> isDeclaredSection,
        FormTreeLimits limits,
        bool allowReferences,
        Func<string, string?, Exception> fail,
        ref int totalNodes,
        int depth = 1)
    {
        if (depth > limits.MaxDepth)
        {
            throw fail($"item tree exceeds max depth {limits.MaxDepth}.", FormDefinitionCodes.TreeDepthExceeded);
        }

        var siblingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            totalNodes++;
            if (totalNodes > limits.MaxNodes)
            {
                throw fail($"item tree exceeds max node count {limits.MaxNodes}.", FormDefinitionCodes.TreeTooManyNodes);
            }

            if (string.IsNullOrEmpty(item.Key))
            {
                throw fail($"an item of kind '{item.Kind}' has an empty key.", FormDefinitionCodes.TreeEmptyKey);
            }

            if (!siblingKeys.Add(item.Key))
            {
                throw fail($"duplicate sibling item key '{item.Key}'.", FormDefinitionCodes.TreeDuplicateKey);
            }

            // F-23: zone layout/placement intents are a Group-only surface. A leaf carries
            // placement on its PARENT, keyed by its item key — never on itself.
            if (item.Kind != FormItemKind.Group && (item.Layout is not null || item.Placement is not null))
            {
                throw fail(
                    $"item '{item.Key}' ({item.Kind}) declares a zone layout/placement; zone intents are only valid on a Group.",
                    FormDefinitionCodes.LayoutZoneNotGroup);
            }

            // F-24 (item 5): tabular presentation is a Collection-only surface (fail-closed).
            if (item.Kind != FormItemKind.Collection && item.Table is not null)
            {
                throw fail(
                    $"item '{item.Key}' ({item.Kind}) declares a table presentation; tabular presentation is only valid on a Collection.",
                    FormDefinitionCodes.CollectionTableNotCollection);
            }

            switch (item.Kind)
            {
                case FormItemKind.Field:
                    if (item.Items is { Count: > 0 })
                    {
                        throw fail($"field item '{item.Key}' must not carry child items.", FormDefinitionCodes.TreeFieldHasChildren);
                    }

                    if (!isDeclaredField(item.Key))
                    {
                        throw fail($"field item '{item.Key}' is not declared in the field map.", FormDefinitionCodes.TreeUnknownField);
                    }

                    break;

                case FormItemKind.Group:
                case FormItemKind.Collection:
                    if (item.Items is not { Count: > 0 })
                    {
                        throw fail($"container item '{item.Key}' ({item.Kind}) must carry at least one child item.", FormDefinitionCodes.TreeEmptyContainer);
                    }

                    if (item.Kind == FormItemKind.Collection)
                    {
                        ValidateCardinality(item, limits, fail);
                        // F-24: a Collection MAY carry a tabular presentation — validate its column
                        // + totals config fail-closed (unknown tokens; totals/columns must name a
                        // `field` item in the row template).
                        ValidateCollectionTable(item, fail);
                    }

                    if (item.Kind == FormItemKind.Group)
                    {
                        // F-23: a Group MAY be a zone — validate its intent tokens fail-closed.
                        LayoutIntentValidator.Validate(item.Layout, item.Placement, $"group '{item.Key}'", fail);
                    }

                    ValidateBounded(item.Items, isDeclaredField, isDeclaredSection, limits, allowReferences, fail, ref totalNodes, depth + 1);
                    break;

                case FormItemKind.Reference:
                    if (!allowReferences)
                    {
                        throw fail(
                            $"reference item '{item.Key}' is not allowed here (unit-composing-unit is deferred in Phase 1).",
                            FormDefinitionCodes.TreeReferenceNotAllowed);
                    }

                    if (item.Reference is null)
                    {
                        throw fail($"reference item '{item.Key}' carries no reusable-unit reference.", FormDefinitionCodes.TreeReferenceMissingRef);
                    }

                    if (item.Items is { Count: > 0 })
                    {
                        throw fail(
                            $"reference item '{item.Key}' must not carry inline children (its subtree comes from the referenced unit).",
                            FormDefinitionCodes.TreeReferenceHasChildren);
                    }

                    // A reference is a leaf at authoring time; the referenced unit is validated
                    // independently at ITS own registration. Do not descend.
                    break;

                case FormItemKind.Content:
                    ValidateContentBlock(item, limits, fail);
                    break;

                case FormItemKind.Action:
                    ValidateActionBlock(item, isDeclaredSection, fail);
                    break;

                default:
                    // Fail-closed: an out-of-range kind (e.g. an integer outside the enum,
                    // smuggled past wire mapping) never admits silently.
                    throw fail($"item '{item.Key}' has an unknown kind '{item.Kind}'.", FormDefinitionCodes.BlocksMissingPayload);
            }
        }
    }

    private static void ValidateCardinality(FormItem item, FormTreeLimits limits, Func<string, string?, Exception> fail)
    {
        var card = item.Cardinality;
        if (card is null)
        {
            return; // unbounded within the engine-level cap; acceptable.
        }

        bool bad = card.Min < 0
            || (card.Max is { } max && (max < card.Min || max > limits.MaxCollectionInstances));
        if (bad)
        {
            throw fail(
                $"collection item '{item.Key}' has invalid cardinality "
                + $"(min={card.Min}, max={(card.Max?.ToString() ?? "∞")}; cap={limits.MaxCollectionInstances}).",
                FormDefinitionCodes.TreeBadCardinality);
        }
    }

    /// <summary>F-23: a Content block is a leaf carrying ≥1 well-formed node — closed node
    /// kinds, non-empty plain text, a sane heading level, within the per-block node cap.</summary>
    private static void ValidateContentBlock(FormItem item, FormTreeLimits limits, Func<string, string?, Exception> fail)
    {
        if (item.Items is { Count: > 0 })
        {
            throw fail($"content block '{item.Key}' must not carry child items (blocks are leaves).", FormDefinitionCodes.BlocksBlockHasChildren);
        }

        if (item.Content is not { Count: > 0 })
        {
            throw fail($"content block '{item.Key}' carries no content nodes.", FormDefinitionCodes.BlocksContentMissingNodes);
        }

        if (item.Content.Count > FormDefinitionValidation.MaxContentNodes)
        {
            throw fail(
                $"content block '{item.Key}' declares {item.Content.Count} nodes; the maximum is {FormDefinitionValidation.MaxContentNodes}.",
                FormDefinitionCodes.BlocksContentTooManyNodes);
        }

        foreach (var node in item.Content)
        {
            if (!ContentNodeKinds.IsKnown(node.Kind))
            {
                throw fail(
                    $"content block '{item.Key}' has a node of unknown kind '{node.Kind}' (allowed: heading, paragraph).",
                    FormDefinitionCodes.BlocksContentUnknownNodeKind);
            }

            if (node.Text is null || node.Text.Values.Count == 0
                || node.Text.Values.Values.All(string.IsNullOrWhiteSpace))
            {
                throw fail($"content block '{item.Key}' has a node with empty text.", FormDefinitionCodes.BlocksContentEmptyText);
            }

            // #1679 review F4: the doc/renderer contract is levels 2–6 (h1 is the form title's
            // grain). The renderer CLAMPS an out-of-range level, so admission must reject <2 (or
            // >6) with the stable code — otherwise an authored level-1 heading admits yet renders
            // as something else (the authored definition would lie about what displays).
            if (node.Kind == ContentNodeKinds.Heading && node.Level is { } level && (level < 2 || level > 6))
            {
                throw fail(
                    $"content block '{item.Key}' has a heading with level {level}; levels are 2–6.",
                    FormDefinitionCodes.BlocksContentBadHeadingLevel);
            }
        }
    }

    /// <summary>F-23: an Action block is a leaf carrying a well-formed declarative config —
    /// a closed action kind, a non-empty label, and a valid target for its kind (absolute
    /// http(s) URL for <c>open-url</c>; a declared section id for <c>scroll-to-section</c>).</summary>
    private static void ValidateActionBlock(FormItem item, Func<string, bool> isDeclaredSection, Func<string, string?, Exception> fail)
    {
        if (item.Items is { Count: > 0 })
        {
            throw fail($"action block '{item.Key}' must not carry child items (blocks are leaves).", FormDefinitionCodes.BlocksBlockHasChildren);
        }

        if (item.Action is null)
        {
            throw fail($"action block '{item.Key}' carries no action config.", FormDefinitionCodes.BlocksMissingPayload);
        }

        var action = item.Action;
        if (!FormActionKinds.IsKnown(action.Kind))
        {
            throw fail(
                $"action block '{item.Key}' has unknown action kind '{action.Kind}' (allowed: open-url, scroll-to-section).",
                FormDefinitionCodes.BlocksActionUnknownKind);
        }

        if (action.Label is null || action.Label.Values.Count == 0
            || action.Label.Values.Values.All(string.IsNullOrWhiteSpace))
        {
            throw fail($"action block '{item.Key}' has an empty label.", FormDefinitionCodes.BlocksActionEmptyLabel);
        }

        if (action.Kind == FormActionKinds.OpenUrl)
        {
            // Fail-closed: absolute http(s) ONLY — a javascript:/data:/relative URL is a
            // script-injection / open-redirect channel and never admits.
            if (string.IsNullOrWhiteSpace(action.Url)
                || !Uri.TryCreate(action.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw fail(
                    $"action block '{item.Key}' (open-url) requires an absolute http(s) URL.",
                    FormDefinitionCodes.BlocksActionBadUrl);
            }

            // #1679 review F3: an open-url MUST NOT carry a scroll target — inert at render, but
            // a tolerated mutually-exclusive field is a footgun; fail-closed on the mismatch.
            if (!string.IsNullOrEmpty(action.SectionId))
            {
                throw fail(
                    $"action block '{item.Key}' (open-url) must not carry a sectionId.",
                    FormDefinitionCodes.BlocksActionExtraneousTarget);
            }
        }
        else // scroll-to-section
        {
            if (string.IsNullOrWhiteSpace(action.SectionId) || !isDeclaredSection(action.SectionId))
            {
                throw fail(
                    $"action block '{item.Key}' (scroll-to-section) targets section '{action.SectionId}' which is not declared.",
                    FormDefinitionCodes.BlocksActionUnknownSection);
            }

            // #1679 review F3: a scroll-to-section MUST NOT carry a URL (mutually exclusive target).
            if (!string.IsNullOrEmpty(action.Url))
            {
                throw fail(
                    $"action block '{item.Key}' (scroll-to-section) must not carry a url.",
                    FormDefinitionCodes.BlocksActionExtraneousTarget);
            }
        }
    }

    /// <summary>F-24: validates a Collection's tabular presentation config fail-closed — each
    /// column key + each totals key must name a <c>field</c> item in the row template, and each
    /// column's width/align token must be in the closed <see cref="LayoutIntents"/> vocab.</summary>
    private static void ValidateCollectionTable(FormItem item, Func<string, string?, Exception> fail)
    {
        if (item.Table is not { } table)
        {
            return;
        }

        // The row-template `field` keys — a total/column may only reference a leaf field
        // (a sum over a group/collection column, or an unknown key, is meaningless).
        var rowFieldKeys = new HashSet<string>(
            (item.Items ?? Array.Empty<FormItem>())
                .Where(i => i.Kind == FormItemKind.Field)
                .Select(i => i.Key),
            StringComparer.Ordinal);

        foreach (var (colKey, col) in table.Columns ?? new Dictionary<string, CollectionColumn>())
        {
            if (!rowFieldKeys.Contains(colKey))
            {
                throw fail(
                    $"collection '{item.Key}' table column '{colKey}' is not a field in the row template.",
                    FormDefinitionCodes.CollectionColumnUnknownField);
            }

            if (col.Width is { } width && !LayoutIntents.Widths.Contains(width))
            {
                throw fail(
                    $"collection '{item.Key}' table column '{colKey}' has unknown width '{width}' (allowed: auto, 1/4, 1/3, 1/2, 2/3, 3/4, full).",
                    FormDefinitionCodes.LayoutUnknownWidth);
            }

            if (col.Align is { } align && !LayoutIntents.Aligns.Contains(align))
            {
                throw fail(
                    $"collection '{item.Key}' table column '{colKey}' has unknown align '{align}' (allowed: start, center, end, stretch).",
                    FormDefinitionCodes.LayoutUnknownAlign);
            }
        }

        foreach (var totalKey in table.Totals ?? Array.Empty<string>())
        {
            if (!rowFieldKeys.Contains(totalKey))
            {
                throw fail(
                    $"collection '{item.Key}' table total '{totalKey}' is not a field in the row template.",
                    FormDefinitionCodes.CollectionTotalUnknownField);
            }
        }
    }
}

/// <summary>
/// F-23: validates a layout's intent tokens against the closed <see cref="LayoutIntents"/>
/// vocabularies, fail-closed with stable <c>form.layout.*</c> codes. Shared by the section
/// grain (<see cref="FormDefinitionValidation"/>) and the group-zone grain
/// (<see cref="FormItemTreeValidator"/>) so the two cannot drift. Only the NEW (F-23)
/// members are validated — the pre-existing Rev-6 members (kind/direction/wrap/columns/gap)
/// keep their original tolerant posture so no previously-valid definition is rejected.
/// </summary>
internal static class LayoutIntentValidator
{
    internal static void Validate(
        SectionLayout? layout,
        IReadOnlyDictionary<string, FieldPlacement>? placement,
        string where,
        Func<string, string?, Exception> fail)
    {
        if (layout is not null)
        {
            if (layout.CollapseBelow is { } bp && !LayoutIntents.Breakpoints.Contains(bp))
            {
                throw fail(
                    $"{where} has unknown collapse breakpoint '{bp}' (allowed: sm, md, lg).",
                    FormDefinitionCodes.LayoutUnknownBreakpoint);
            }

            if (layout.Density is { } density && !LayoutIntents.Densities.Contains(density))
            {
                throw fail(
                    $"{where} has unknown density '{density}' (allowed: comfortable, compact).",
                    FormDefinitionCodes.LayoutUnknownDensity);
            }

            if (layout.Align is { } align && !LayoutIntents.Aligns.Contains(align))
            {
                throw fail(
                    $"{where} has unknown align '{align}' (allowed: start, center, end, stretch).",
                    FormDefinitionCodes.LayoutUnknownAlign);
            }
        }

        if (placement is not null)
        {
            foreach (var (key, p) in placement)
            {
                if (p.Width is { } width && !LayoutIntents.Widths.Contains(width))
                {
                    throw fail(
                        $"{where} placement '{key}' has unknown width '{width}' (allowed: auto, 1/4, 1/3, 1/2, 2/3, 3/4, full).",
                        FormDefinitionCodes.LayoutUnknownWidth);
                }

                if (p.Align is { } align && !LayoutIntents.Aligns.Contains(align))
                {
                    throw fail(
                        $"{where} placement '{key}' has unknown align '{align}' (allowed: start, center, end, stretch).",
                        FormDefinitionCodes.LayoutUnknownAlign);
                }
            }
        }
    }
}
