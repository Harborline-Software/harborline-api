using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Shared registration-time invariant checks for <see cref="IFormDefinitionStore"/>
/// implementations. Centralised so the in-memory reference store and the durable
/// entity-store-backed store enforce identical overlay + schema-ref invariants and
/// cannot drift apart.
/// </summary>
internal static class FormDefinitionValidation
{
    /// <summary>
    /// Validates the Harborline overlay: section ids unique, every field referenced by
    /// a section is declared in <see cref="HarborlineOverlay.Fields"/>, rule ids unique,
    /// non-schema-scoped rules carry a scope target, rule expressions are non-empty, and
    /// (ADR 0055 Rev 7) any nested item tree is within the fail-closed
    /// <see cref="FormTreeLimits"/> bounds.
    /// </summary>
    public static void ValidateOverlayOrThrow(FormDefinition definition)
        => ValidateOverlayOrThrow(definition, FormTreeLimits.Default);

    /// <summary>
    /// Overload taking explicit tree limits (the numbers are CIC-tunable; the default
    /// overload uses <see cref="FormTreeLimits.Default"/>). Split out so tests can drive
    /// the fail-closed rejection with tight caps without publishing a huge definition.
    /// </summary>
    public static void ValidateOverlayOrThrow(FormDefinition definition, FormTreeLimits limits)
    {
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        // F-23: the COMPLETE declared-section-id set, computed up front so a
        // scroll-to-section action may target any section (including a later one).
        var allSectionIds = new HashSet<string>(
            definition.Overlay.Sections.Select(s => s.Id), StringComparer.Ordinal);
        // Total node budget is shared across ALL sections' trees (INV-S2 extension) —
        // a definition cannot dodge the cap by spreading fan-out across many sections.
        int totalNodes = 0;
        foreach (var section in definition.Overlay.Sections)
        {
            if (!sectionIds.Add(section.Id))
            {
                throw new FormDefinitionValidationException(definition.Id, $"duplicate section id '{section.Id}'.");
            }

            foreach (var field in section.Fields)
            {
                if (!definition.Overlay.Fields.ContainsKey(field))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"section '{section.Id}' references field '{field}' which is not declared in Overlay.Fields.");
                }
            }

            // F-23: the section's own layout intents (breakpoint/density/align/width tokens)
            // are validated against the closed vocabularies, fail-closed. Only the NEW
            // members are checked — the Rev-6 members keep their tolerant posture.
            LayoutIntentValidator.Validate(
                section.Layout,
                section.FieldPlacement,
                $"section '{section.Id}' layout",
                (message, code) => new FormDefinitionValidationException(definition.Id, message, code));

            // ADR 0055 Rev 7: validate + bound the recursive item tree, if present. D4:
            // Reference nodes are permitted in a definition's section trees (a definition
            // may reference a reusable unit); the referenced unit is validated at its own
            // registration. The shared FormItemTreeValidator enforces identical bounds here
            // and inside a reusable-unit body.
            if (section.Items is { Count: > 0 })
            {
                FormItemTreeValidator.ValidateBounded(
                    section.Items,
                    definition.Overlay.Fields.ContainsKey,
                    allSectionIds.Contains,
                    limits,
                    allowReferences: true,
                    fail: (message, code) => new FormDefinitionValidationException(
                        definition.Id, $"section '{section.Id}': {message}", code),
                    ref totalNodes);
            }
        }

        // Item 4: GLOBAL top-level key uniqueness — a SEPARATE pass AFTER the per-section tree
        // validation (so a same-section sibling duplicate still surfaces as the sibling code
        // `form.tree.duplicate_key`, not this one). Every section contributes its TOP-LEVEL
        // value/block/container keys into the ONE candidate object; sibling-uniqueness alone
        // misses a CROSS-section collision (two sections each with a top-level `notes` clash in
        // that one object, silently overwriting). Nested keys live in their container's own
        // object/row namespace and are NOT globalised here (two collections may legitimately each
        // carry a `notes` column). Builder-authored keys are unique ids, so this only bites a
        // hand/packet-authored collision — it must not brick a shipped definition.
        var globalTopLevelKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in definition.Overlay.Sections)
        {
            var topLevelKeys = section.Items is { Count: > 0 }
                ? section.Items.Select(i => i.Key)
                : (IEnumerable<string>)section.Fields;
            foreach (var key in topLevelKeys)
            {
                if (!globalTopLevelKeys.Add(key))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"item key '{key}' (section '{section.Id}') collides with another top-level key elsewhere in the definition.",
                        FormDefinitionCodes.TreeGlobalDuplicateKey);
                }
            }
        }

        var rulesById = new Dictionary<string, RuleDefinition>(StringComparer.Ordinal);
        foreach (var rule in definition.Overlay.Rules)
        {
            if (!rulesById.TryAdd(rule.Id, rule))
            {
                throw new FormDefinitionValidationException(definition.Id, $"duplicate rule id '{rule.Id}'.");
            }

            if (rule.Scope != RuleScope.Schema && string.IsNullOrEmpty(rule.ScopeTarget))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"rule '{rule.Id}' scope '{rule.Scope}' requires a non-empty ScopeTarget.");
            }

            if (string.IsNullOrEmpty(rule.Expression))
            {
                throw new FormDefinitionValidationException(definition.Id, $"rule '{rule.Id}' has empty expression.");
            }
        }

        ValidatePagesOrThrow(definition, sectionIds, rulesById);
        ValidateAsyncChecksOrThrow(definition);
    }

    /// <summary>
    /// Fail-closed cap on the number of wizard pages a definition may declare (F-14).
    /// Generous for real forms; small enough to bound the wizard chrome + admission cost.
    /// </summary>
    internal const int MaxPages = 50;

    /// <summary>
    /// Fail-closed cap on the number of async validation checks a definition may
    /// declare (F-20). Generous for real forms; bounds the client's connector fan-out.
    /// </summary>
    internal const int MaxAsyncChecks = 25;

    /// <summary>
    /// Fail-closed cap on the nodes ONE content block may declare (F-23). Content blocks
    /// are inspection instructions / short guidance, not documents; the cap bounds the
    /// authored-text surface per block (each block still counts against the tree node cap).
    /// </summary>
    internal const int MaxContentNodes = 20;

    /// <summary>
    /// Validates the optional wizard-page grain (F-14). No pages ⇒ nothing to check
    /// (a pageless definition is the byte-identical pre-F-14 shape). With pages, ALL
    /// of these hold or registration throws with a stable localizable code:
    /// unique non-empty page ids; every page non-empty; every referenced section
    /// exists; every section assigned to exactly one page; the page count within
    /// <see cref="MaxPages"/>; any <c>VisibleWhen</c> guard non-blank AND parseable
    /// JSON (F-20 — a typo'd guard would otherwise admit and, being fail-closed at
    /// render, silently hide its page forever); any page check referencing a declared
    /// <see cref="RuleActionKind.Validate"/>-action rule (F-20).
    /// </summary>
    private static void ValidatePagesOrThrow(
        FormDefinition definition,
        HashSet<string> sectionIds,
        IReadOnlyDictionary<string, RuleDefinition> rulesById)
    {
        var pages = definition.Overlay.Pages;
        if (pages is not { Count: > 0 })
        {
            return;
        }

        if (pages.Count > MaxPages)
        {
            throw new FormDefinitionValidationException(
                definition.Id,
                $"declares {pages.Count} pages; the maximum is {MaxPages}.",
                FormDefinitionCodes.PagesTooManyPages);
        }

        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var assignedSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id))
            {
                throw new FormDefinitionValidationException(
                    definition.Id, "a page has an empty id.", FormDefinitionCodes.PagesEmptyPageId);
            }

            if (!pageIds.Add(page.Id))
            {
                throw new FormDefinitionValidationException(
                    definition.Id, $"duplicate page id '{page.Id}'.", FormDefinitionCodes.PagesDuplicatePageId);
            }

            if (page.Sections is not { Count: > 0 })
            {
                throw new FormDefinitionValidationException(
                    definition.Id, $"page '{page.Id}' carries no sections.", FormDefinitionCodes.PagesEmptyPage);
            }

            foreach (var sectionId in page.Sections)
            {
                if (!sectionIds.Contains(sectionId))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"page '{page.Id}' references section '{sectionId}' which is not declared in Overlay.Sections.",
                        FormDefinitionCodes.PagesUnknownSection);
                }

                if (!assignedSections.Add(sectionId))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"section '{sectionId}' is assigned to more than one page.",
                        FormDefinitionCodes.PagesDuplicateSectionAssignment);
                }
            }

            if (page.VisibleWhen is not null && string.IsNullOrWhiteSpace(page.VisibleWhen))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"page '{page.Id}' has an empty VisibleWhen guard (omit it instead).",
                    FormDefinitionCodes.PagesEmptyVisibleWhen);
            }

            // F-20: a guard must at least be parseable JSON. The renderer is fail-closed
            // (an erroring guard HIDES the page), so admitting a typo'd guard would
            // silently hide the page forever — reject it at the door instead.
            if (page.VisibleWhen is { } guard && !string.IsNullOrWhiteSpace(guard))
            {
                try
                {
                    using var _ = System.Text.Json.JsonDocument.Parse(guard);
                }
                catch (System.Text.Json.JsonException)
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"page '{page.Id}' has a VisibleWhen guard that is not valid JSON.",
                        FormDefinitionCodes.PagesInvalidVisibleWhen);
                }
            }

            // F-20: page checks bind existing Validate-action rules — reject a dangling
            // or wrong-action reference (a silent no-op check would be a fake gate).
            foreach (var checkId in page.Checks ?? Array.Empty<string>())
            {
                if (!rulesById.TryGetValue(checkId, out var checkRule))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"page '{page.Id}' check references rule '{checkId}' which is not declared in Overlay.Rules.",
                        FormDefinitionCodes.PagesUnknownCheck);
                }

                if (checkRule.Action != RuleActionKind.Validate)
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"page '{page.Id}' check '{checkId}' has action '{checkRule.Action}'; page checks must be Validate rules.",
                        FormDefinitionCodes.PagesCheckNotValidate);
                }
            }
        }

        // When pages are declared, the page grain must COVER the sections: an
        // unassigned section would silently never render in the wizard.
        foreach (var sectionId in sectionIds)
        {
            if (!assignedSections.Contains(sectionId))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"section '{sectionId}' is not assigned to any page (pages are declared).",
                    FormDefinitionCodes.PagesUnassignedSection);
            }
        }
    }

    /// <summary>
    /// Validates the optional async validation checks (F-20). No checks ⇒ nothing to
    /// do (byte-identical pre-F-20 shape). With checks, fail-closed: unique non-empty
    /// ids; a non-empty connector key and fail code; every named field (target +
    /// inputs) declared in <see cref="HarborlineOverlay.Fields"/>; a sane debounce; the
    /// count within <see cref="MaxAsyncChecks"/>.
    /// </summary>
    private static void ValidateAsyncChecksOrThrow(FormDefinition definition)
    {
        var checks = definition.Overlay.AsyncChecks;
        if (checks is not { Count: > 0 })
        {
            return;
        }

        if (checks.Count > MaxAsyncChecks)
        {
            throw new FormDefinitionValidationException(
                definition.Id,
                $"declares {checks.Count} async checks; the maximum is {MaxAsyncChecks}.",
                FormDefinitionCodes.ChecksTooMany);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            if (string.IsNullOrWhiteSpace(check.Id) || !ids.Add(check.Id))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"async check id '{check.Id}' is empty or duplicated.",
                    FormDefinitionCodes.ChecksBadId);
            }

            if (string.IsNullOrWhiteSpace(check.Connector))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"async check '{check.Id}' has an empty connector.",
                    FormDefinitionCodes.ChecksEmptyConnector);
            }

            if (string.IsNullOrWhiteSpace(check.FailCode))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"async check '{check.Id}' has an empty fail code.",
                    FormDefinitionCodes.ChecksEmptyFailCode);
            }

            if (!definition.Overlay.Fields.ContainsKey(check.Field))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"async check '{check.Id}' targets field '{check.Field}' which is not declared in Overlay.Fields.",
                    FormDefinitionCodes.ChecksUnknownField);
            }

            foreach (var input in check.Inputs ?? Array.Empty<string>())
            {
                if (!definition.Overlay.Fields.ContainsKey(input))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"async check '{check.Id}' input '{input}' is not declared in Overlay.Fields.",
                        FormDefinitionCodes.ChecksUnknownField);
                }
            }

            if (check.DebounceMs is { } debounce && (debounce < 0 || debounce > 60_000))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"async check '{check.Id}' debounce {debounce}ms is out of range (0–60000).",
                    FormDefinitionCodes.ChecksBadDebounce);
            }
        }
    }

    /// <summary>
    /// Validates the content-addressed schema reference. The keystone holds a reference
    /// into the kernel schema registry (ADR 0055 OQ-3); the canonical schema body lives
    /// there and is validated for JSON Schema 2020-12 conformance at registration. The
    /// keystone's only structural invariant on the reference is non-emptiness.
    /// </summary>
    public static void ValidateSchemaRefOrThrow(FormDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.SchemaRef.Value))
        {
            throw new FormDefinitionValidationException(definition.Id, "SchemaRef is empty.");
        }
    }
}
