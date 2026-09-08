using System.Text.Json;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Packs.Navigation;

/// <summary>Stable install-time refusal codes for the pack-declared chrome grammar.</summary>
public static class PackNavigationAdmissionCodes
{
    public const string Malformed = "pack.nav.malformed";
    public const string BoundsExceeded = "pack.nav.bounds_exceeded";
    public const string DuplicateWorkspace = "pack.nav.duplicate_workspace";
    public const string DuplicateGroup = "pack.nav.duplicate_group";
    public const string ItemLabelKeyRequired = "pack.nav.item_label_key_required";
    public const string DuplicateItem = "pack.nav.duplicate_item";
    public const string DuplicatePanel = "pack.nav.duplicate_panel";
    public const string DuplicateModeSwitch = "pack.nav.duplicate_mode_switch";
    public const string UnknownTrait = "pack.nav.unknown_trait";
    public const string UnknownBodyTemplate = "pack.nav.unknown_body_template";
    public const string TraitTemplateMismatch = "pack.nav.trait_template_mismatch";
    public const string PanelChromeDeclared = "pack.nav.panel_chrome_declared";
    public const string KnownPanelShapeDeclared = "pack.nav.known_panel_shape_declared";
    public const string ZoneOrderDeclared = "pack.nav.zone_order_declared";
    public const string SystemBadgeDeclared = "pack.nav.system_badge_declared";
    public const string DisabledEntry = "pack.nav.disabled_entry";
    public const string DishonestCount = "pack.nav.dishonest_count";
    public const string DanglingReference = "pack.nav.dangling_reference";
    public const string RoleGateNotWired = "pack.nav.role_gate_not_wired";
}

public sealed record PackNavigationRefusal(string Code, string Message);

public sealed record PackNavigationParseResult(
    PackNavigationDeclaration? Declaration,
    PackNavigationRefusal? Refusal)
{
    public bool Succeeded => Declaration is not null && Refusal is null;
}

/// <summary>
/// The pack-owned fill for kernel navigation and dock chrome. It deliberately carries no zone order,
/// system entries, badges, shell affordances, scroll regions, or tenant-editable state.
/// </summary>
public sealed record PackNavigationDeclaration(
    IReadOnlyList<PackNavigationWorkspace> SeedWorkspaces,
    PackNavigationModeSwitch? ModeSwitch,
    IReadOnlyList<PackPanelDeclaration> PanelSet);

public sealed record PackNavigationModeSwitch(IReadOnlyList<PackNavigationMode> Modes);

public sealed record PackNavigationMode(
    string Id,
    string LabelKey,
    IReadOnlyList<string> WorkspaceIds);

public sealed record PackNavigationWorkspace(
    string Id,
    string LabelKey,
    string? Icon,
    string? DestinationQueryRef,
    string? CountQueryRef,
    IReadOnlyList<PackNavigationGroup> Groups,
    IReadOnlyList<PackNavigationAction> CreateActions,
    IReadOnlyList<PackDocumentSpineNode> DocumentSpine,
    IReadOnlyList<string>? DefaultForPersonas);

public sealed record PackNavigationGroup(
    string Id,
    string LabelKey,
    string? DestinationQueryRef,
    string? CountQueryRef,
    IReadOnlyList<string> ItemIds,
    PackNavigationAction? AddAction,
    IReadOnlyList<PackNavigationItem> Items);

public sealed record PackNavigationItem(string Id, string LabelKey);

public sealed record PackNavigationAction(
    string Id,
    string VerbKey,
    string Icon,
    string Binding,
    string Shortcut,
    IReadOnlyList<string> PermittedRoles);

public sealed record PackDocumentSpineNode(
    string Id,
    string LabelKey,
    string Binding,
    IReadOnlyList<PackDocumentSpineNode> Children);

public sealed record PackPanelDeclaration(
    string Id,
    string LabelKey,
    string Binding,
    string Shortcut,
    int DefaultWidth,
    int MinimumHeight,
    bool DefaultOpen,
    string HeaderForm,
    string BodyTemplate,
    PackPanelFooter Footer,
    IReadOnlyList<string> Traits);

public sealed record PackPanelFooter(string Kind, string LabelKey, string? Binding);

/// <summary>
/// Single strict parser for both install admission and the active-seed read projection. Exact-property
/// checks make absence meaningful: packs omit unavailable capabilities and cannot smuggle a disabled row.
/// </summary>
public static class PackNavigationDeclarationParser
{
    public const int MaxWorkspaces = 32;
    private const int MaxCanonicalJsonLength = 262_144;
    private const int MaxGroupsPerWorkspace = 16;
    private const int MaxItemsPerGroup = 64;
    private const int MaxActionsPerWorkspace = 16;
    private const int MaxPersonasPerWorkspace = 16;
    private const int MaxPanels = 32;
    private const int MaxDocumentNodes = 512;
    private const int MaxDocumentDepth = 16;
    private const int MaxIdLength = 128;
    private const int MaxTextLength = 256;

    private static readonly HashSet<string> Traits = new(StringComparer.Ordinal)
    {
        "Scoped", "Collection", "OpensOne", "Live", "Consequential",
    };

    private static readonly IReadOnlyDictionary<string, KnownPanelShape> KnownPanels =
        new Dictionary<string, KnownPanelShape>(StringComparer.Ordinal)
        {
            ["notifications"] = new("panels.notifications", "Title", "Feed",
                "panels.notifications.footerClaim", ["Collection", "Live"]),
            ["runs"] = new("panels.runs", "Title", "Feed",
                "panels.runs.footerClaim", ["Collection", "Live"]),
            ["documents"] = new("panels.documents", "SwitcherItem", "Library",
                "panels.documents.footerClaim", ["Scoped", "Collection", "OpensOne"]),
            ["reports"] = new("panels.reports", "SwitcherItem", "Library",
                "panels.reports.footerClaim", ["Scoped", "Collection", "OpensOne"]),
            ["pilot"] = new("panels.pilot", "Title", "Decision",
                "panels.pilot.ask", ["Scoped", "Live", "Consequential"]),
            ["inspector"] = new("panels.inspector", "Title", "Fields",
                "panels.inspector.footerClaim", ["Scoped"]),
        };

    /// <summary>
    /// True when content actually carries the navigation declaration member. Older packs may retain a
    /// NavWorkspaceConfig placeholder without that member; that is absence, not a disabled declaration.
    /// </summary>
    public static bool DeclaresNavigation(string canonicalJson)
    {
        if (canonicalJson is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(canonicalJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("seedWorkspaces", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static PackNavigationParseResult Parse(string canonicalJson)
    {
        if (canonicalJson is null || canonicalJson.Length > MaxCanonicalJsonLength)
            return Refused(PackNavigationAdmissionCodes.BoundsExceeded, "Navigation content exceeds its bound.");

        try
        {
            using var document = JsonDocument.Parse(canonicalJson);
            var root = document.RootElement;
            var forbidden = FindForbiddenDeclaration(root);
            if (forbidden is not null)
                return forbidden;

            if (!HasExactProperties(root, ["seedWorkspaces"], ["modeSwitch", "panelSet"]))
                return Malformed("The navigation root must declare only seedWorkspaces, modeSwitch, and panelSet.");

            if (!TryArray(root.GetProperty("seedWorkspaces"), 1, MaxWorkspaces, out var workspaceElements))
                return Refused(PackNavigationAdmissionCodes.BoundsExceeded, "seedWorkspaces must contain 1..32 entries.");

            var workspaces = new List<PackNavigationWorkspace>(workspaceElements!.Length);
            var workspaceIds = new HashSet<string>(StringComparer.Ordinal);
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in workspaceElements)
            {
                var parsed = ParseWorkspace(element);
                if (!parsed.Succeeded)
                    return parsed.Result;
                var workspace = parsed.Value!;
                if (!workspaceIds.Add(workspace.Id))
                    return Refused(PackNavigationAdmissionCodes.DuplicateWorkspace, $"Workspace '{workspace.Id}' is declared twice.");
                foreach (var itemId in workspace.Groups.SelectMany(group => group.ItemIds))
                    if (!itemIds.Add(itemId))
                        return Refused(PackNavigationAdmissionCodes.DuplicateItem, $"Navigation item '{itemId}' is declared twice.");
                workspaces.Add(workspace);
            }

            PackNavigationModeSwitch? modeSwitch = null;
            if (root.TryGetProperty("modeSwitch", out var modeElement))
            {
                var parsed = ParseModeSwitch(modeElement, workspaceIds);
                if (!parsed.Succeeded)
                    return parsed.Result;
                modeSwitch = parsed.Value;
            }

            var panels = new List<PackPanelDeclaration>();
            if (root.TryGetProperty("panelSet", out var panelSetElement))
            {
                if (!TryArray(panelSetElement, 1, MaxPanels, out var panelElements))
                    return Refused(PackNavigationAdmissionCodes.BoundsExceeded, "panelSet must contain 1..32 entries when declared.");
                var panelIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in panelElements!)
                {
                    var parsed = ParsePanel(element);
                    if (!parsed.Succeeded)
                        return parsed.Result;
                    if (!panelIds.Add(parsed.Value!.Id))
                        return Refused(PackNavigationAdmissionCodes.DuplicatePanel, $"Panel '{parsed.Value.Id}' is declared twice.");
                    panels.Add(parsed.Value);
                }
            }

            if (panels.Any(panel => panel.Id == "documents")
                && workspaces.Any(workspace => workspace.DocumentSpine.Count == 0))
                return Refused(PackNavigationAdmissionCodes.DanglingReference,
                    "Every workspace must declare documentSpine when the Documents panel is present.");

            return new PackNavigationParseResult(
                new PackNavigationDeclaration(workspaces, modeSwitch, panels), null);
        }
        catch (JsonException)
        {
            return Malformed("Navigation content is not valid JSON.");
        }
    }

    /// <summary>Returns role gates declared by workspace create verbs and group add actions.</summary>
    public static IReadOnlyList<DeclarativeGateReference> RoleGates(PackNavigationDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var gates = new List<DeclarativeGateReference>();
        foreach (var workspace in declaration.SeedWorkspaces)
        {
            foreach (var action in workspace.CreateActions)
                AddRoles(gates, $"workspace:{workspace.Id}.create:{action.Id}", action.PermittedRoles);
            foreach (var group in workspace.Groups)
                if (group.AddAction is { } add)
                    AddRoles(gates, $"workspace:{workspace.Id}.group:{group.Id}.add:{add.Id}", add.PermittedRoles);
        }
        return gates;
    }

    /// <summary>Validates identities that must remain unique after several pack declarations compose.</summary>
    public static PackNavigationRefusal? FindCompositionRefusal(
        IEnumerable<PackNavigationDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        var items = new HashSet<string>(StringComparer.Ordinal);
        var panels = new HashSet<string>(StringComparer.Ordinal);
        var workspaceCount = 0;
        var hasModeSwitch = false;
        var hasDocumentsPanel = false;
        var hasUnspinedWorkspace = false;
        foreach (var declaration in declarations)
        {
            workspaceCount += declaration.SeedWorkspaces.Count;
            if (workspaceCount > MaxWorkspaces)
                return new(PackNavigationAdmissionCodes.BoundsExceeded,
                    "The composed navigation exceeds the 32-workspace bound.");
            if (declaration.ModeSwitch is not null)
            {
                if (hasModeSwitch)
                    return new(PackNavigationAdmissionCodes.DuplicateModeSwitch,
                        "More than one pack declares the tenant mode switch.");
                hasModeSwitch = true;
            }
            foreach (var workspace in declaration.SeedWorkspaces)
            {
                hasUnspinedWorkspace |= workspace.DocumentSpine.Count == 0;
                if (!workspaces.Add(workspace.Id))
                    return new(PackNavigationAdmissionCodes.DuplicateWorkspace,
                        $"Workspace '{workspace.Id}' is claimed by more than one pack.");
                foreach (var item in workspace.Groups.SelectMany(group => group.ItemIds))
                    if (!items.Add(item))
                        return new(PackNavigationAdmissionCodes.DuplicateItem,
                            $"Navigation item '{item}' is claimed by more than one pack.");
            }
            foreach (var panel in declaration.PanelSet)
            {
                hasDocumentsPanel |= panel.Id == "documents";
                if (!panels.Add(panel.Id))
                    return new(PackNavigationAdmissionCodes.DuplicatePanel,
                        $"Panel '{panel.Id}' is claimed by more than one pack.");
            }
        }
        if (hasDocumentsPanel && hasUnspinedWorkspace)
            return new(PackNavigationAdmissionCodes.DanglingReference,
                "Every composed workspace must declare documentSpine when the Documents panel is present.");
        return null;
    }

    private static Parsed<PackNavigationWorkspace> ParseWorkspace(JsonElement element)
    {
        if (!HasExactProperties(element, ["id", "labelKey"],
                ["icon", "destinationQueryRef", "countQueryRef", "groups", "createActions", "documentSpine", "defaultForPersonas"])
            || !TryStableId(element, "id", out var id)
            || !TryLabelKey(element, "labelKey", out var labelKey))
            return Parsed<PackNavigationWorkspace>.Fail(Malformed("A workspace has an invalid shape."));

        if (!TryOptionalToken(element, "icon", out var icon)
            || !TryOptionalReference(element, "destinationQueryRef", out var destination)
            || !TryOptionalReference(element, "countQueryRef", out var count))
            return Parsed<PackNavigationWorkspace>.Fail(Malformed("A workspace icon or query reference is invalid."));
        if (count is not null && !string.Equals(count, destination, StringComparison.Ordinal))
            return Parsed<PackNavigationWorkspace>.Fail(Refused(PackNavigationAdmissionCodes.DishonestCount,
                $"Workspace '{id}' countQueryRef must be its destinationQueryRef or be absent."));

        var groups = new List<PackNavigationGroup>();
        if (element.TryGetProperty("groups", out var groupsElement))
        {
            if (!TryArray(groupsElement, 1, MaxGroupsPerWorkspace, out var groupElements))
                return Parsed<PackNavigationWorkspace>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                    "groups must contain 1..16 entries when declared."));
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var groupElement in groupElements!)
            {
                var parsed = ParseGroup(groupElement);
                if (!parsed.Succeeded)
                    return Parsed<PackNavigationWorkspace>.Fail(parsed.Result);
                if (!groupIds.Add(parsed.Value!.Id))
                    return Parsed<PackNavigationWorkspace>.Fail(Refused(PackNavigationAdmissionCodes.DuplicateGroup,
                        $"Group '{parsed.Value.Id}' is declared twice in workspace '{id}'."));
                groups.Add(parsed.Value);
            }
        }

        var actions = new List<PackNavigationAction>();
        if (element.TryGetProperty("createActions", out var actionsElement))
        {
            if (!TryArray(actionsElement, 1, MaxActionsPerWorkspace, out var actionElements))
                return Parsed<PackNavigationWorkspace>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                    "createActions must contain 1..16 entries when declared."));
            var actionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var actionElement in actionElements!)
            {
                var parsed = ParseAction(actionElement);
                if (!parsed.Succeeded)
                    return Parsed<PackNavigationWorkspace>.Fail(parsed.Result);
                if (!actionIds.Add(parsed.Value!.Id))
                    return Parsed<PackNavigationWorkspace>.Fail(Malformed($"Create action '{parsed.Value.Id}' is declared twice."));
                actions.Add(parsed.Value);
            }
        }

        var spine = new List<PackDocumentSpineNode>();
        if (element.TryGetProperty("documentSpine", out var spineElement))
        {
            if (!TryArray(spineElement, 1, MaxDocumentNodes, out var spineElements))
                return Parsed<PackNavigationWorkspace>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                    "documentSpine must contain at least one bounded root when declared."));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var countNodes = 0;
            foreach (var node in spineElements!)
            {
                var parsed = ParseDocumentNode(node, seen, 1, ref countNodes);
                if (!parsed.Succeeded)
                    return Parsed<PackNavigationWorkspace>.Fail(parsed.Result);
                spine.Add(parsed.Value!);
            }
        }

        IReadOnlyList<string>? personas = null;
        if (element.TryGetProperty("defaultForPersonas", out var personasElement)
            && !TryStableIdArray(personasElement, 0, MaxPersonasPerWorkspace, out personas))
            return Parsed<PackNavigationWorkspace>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                "defaultForPersonas is invalid or exceeds its bound."));

        return Parsed<PackNavigationWorkspace>.Pass(new(
            id!, labelKey!, icon, destination, count, groups, actions, spine, personas));
    }

    private static Parsed<PackNavigationGroup> ParseGroup(JsonElement element)
    {
        if (!HasExactProperties(element, ["id", "labelKey", "itemIds"],
                ["destinationQueryRef", "countQueryRef", "addAction", "items"])
            || !TryStableId(element, "id", out var id)
            || !TryLabelKey(element, "labelKey", out var labelKey)
            || !TryStableIdArray(element.GetProperty("itemIds"), 1, MaxItemsPerGroup, out var itemIds)
            || !TryOptionalReference(element, "destinationQueryRef", out var destination)
            || !TryOptionalReference(element, "countQueryRef", out var count))
            return Parsed<PackNavigationGroup>.Fail(Malformed("A navigation group has an invalid shape."));
        if (count is not null && !string.Equals(count, destination, StringComparison.Ordinal))
            return Parsed<PackNavigationGroup>.Fail(Refused(PackNavigationAdmissionCodes.DishonestCount,
                $"Group '{id}' countQueryRef must be its destinationQueryRef or be absent."));

        if (!element.TryGetProperty("items", out var itemsElement))
            return Parsed<PackNavigationGroup>.Fail(Refused(PackNavigationAdmissionCodes.ItemLabelKeyRequired,
                "Every navigation item must declare a labelKey."));
        if (!TryArray(itemsElement, 1, MaxItemsPerGroup, out var itemElements))
            return Parsed<PackNavigationGroup>.Fail(Malformed("items must contain 1..64 labeled entries."));
        var items = new List<PackNavigationItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in itemElements!)
        {
            if (item.ValueKind != JsonValueKind.Object || !TryLabelKey(item, "labelKey", out var itemLabel))
                return Parsed<PackNavigationGroup>.Fail(Refused(PackNavigationAdmissionCodes.ItemLabelKeyRequired,
                    "Every navigation item must declare a valid dotted labelKey."));
            if (!HasExactProperties(item, ["id", "labelKey"], []) || !TryStableId(item, "id", out var itemId))
                return Parsed<PackNavigationGroup>.Fail(Malformed("A navigation item has an invalid shape."));
            if (!seen.Add(itemId!))
                return Parsed<PackNavigationGroup>.Fail(Refused(PackNavigationAdmissionCodes.DuplicateItem,
                    $"Navigation item '{itemId}' is declared twice."));
            items.Add(new(itemId!, itemLabel!));
        }
        if (!itemIds!.SequenceEqual(items.Select(item => item.Id), StringComparer.Ordinal))
            return Parsed<PackNavigationGroup>.Fail(Malformed("items must label every itemId in the same order."));

        PackNavigationAction? add = null;
        if (element.TryGetProperty("addAction", out var addElement))
        {
            var parsed = ParseAction(addElement);
            if (!parsed.Succeeded)
                return Parsed<PackNavigationGroup>.Fail(parsed.Result);
            add = parsed.Value;
        }
        return Parsed<PackNavigationGroup>.Pass(new(id!, labelKey!, destination, count, itemIds!, add, items));
    }

    private static Parsed<PackNavigationAction> ParseAction(JsonElement element)
    {
        if (!HasExactProperties(element, ["id", "verbKey", "icon", "binding", "shortcut", "permittedRoles"], [])
            || !TryStableId(element, "id", out var id)
            || !TryLabelKey(element, "verbKey", out var verbKey)
            || !TryToken(element, "icon", out var icon)
            || !TryReference(element, "binding", out var binding)
            || !TryReference(element, "shortcut", out var shortcut)
            || !TryStringArray(element.GetProperty("permittedRoles"), 1, 16, out var roles))
            return Parsed<PackNavigationAction>.Fail(Malformed("A navigation action must declare verbKey, icon, binding, shortcut, and roles."));
        try
        {
            foreach (var role in roles!)
                _ = DeclarativeGateReference.ForRole("navigation-action", role);
        }
        catch (GateReferenceShapeException exception)
        {
            return Parsed<PackNavigationAction>.Fail(Refused(exception.Code, exception.Message));
        }
        return Parsed<PackNavigationAction>.Pass(new(id!, verbKey!, icon!, binding!, shortcut!, roles!));
    }

    private static Parsed<PackNavigationModeSwitch> ParseModeSwitch(
        JsonElement element,
        IReadOnlySet<string> workspaceIds)
    {
        if (!HasExactProperties(element, ["modes"], [])
            || !TryArray(element.GetProperty("modes"), 2, 3, out var modeElements))
            return Parsed<PackNavigationModeSwitch>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                "modeSwitch must contain exactly two or three modes."));

        var modes = new List<PackNavigationMode>();
        var modesSeen = new HashSet<string>(StringComparer.Ordinal);
        var workspacesSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in modeElements!)
        {
            if (!HasExactProperties(mode, ["id", "labelKey", "workspaceIds"], [])
                || !TryStableId(mode, "id", out var id)
                || !TryLabelKey(mode, "labelKey", out var labelKey)
                || !TryStableIdArray(mode.GetProperty("workspaceIds"), 1, MaxWorkspaces, out var references))
                return Parsed<PackNavigationModeSwitch>.Fail(Malformed("A mode has an invalid shape."));
            if (!modesSeen.Add(id!))
                return Parsed<PackNavigationModeSwitch>.Fail(Malformed($"Mode '{id}' is declared twice."));
            foreach (var workspace in references!)
            {
                if (!workspaceIds.Contains(workspace) || !workspacesSeen.Add(workspace))
                    return Parsed<PackNavigationModeSwitch>.Fail(Refused(PackNavigationAdmissionCodes.DanglingReference,
                        $"Mode '{id}' has a missing or non-disjoint workspace reference '{workspace}'."));
            }
            modes.Add(new(id!, labelKey!, references!));
        }
        return Parsed<PackNavigationModeSwitch>.Pass(new(modes));
    }

    private static Parsed<PackDocumentSpineNode> ParseDocumentNode(
        JsonElement element,
        HashSet<string> seen,
        int depth,
        ref int count)
    {
        if (depth > MaxDocumentDepth || ++count > MaxDocumentNodes)
            return Parsed<PackDocumentSpineNode>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                "documentSpine exceeds its depth or node bound."));
        if (!HasExactProperties(element, ["id", "labelKey", "binding"], ["children"])
            || !TryStableId(element, "id", out var id)
            || !TryLabelKey(element, "labelKey", out var labelKey)
            || !TryReference(element, "binding", out var binding)
            || !seen.Add(id!))
            return Parsed<PackDocumentSpineNode>.Fail(Malformed("A documentSpine node is invalid or duplicated."));
        var children = new List<PackDocumentSpineNode>();
        if (element.TryGetProperty("children", out var childrenElement))
        {
            if (!TryArray(childrenElement, 1, MaxDocumentNodes, out var childElements))
                return Parsed<PackDocumentSpineNode>.Fail(Refused(PackNavigationAdmissionCodes.BoundsExceeded,
                    "A declared children collection must be non-empty and bounded."));
            foreach (var child in childElements!)
            {
                var parsed = ParseDocumentNode(child, seen, depth + 1, ref count);
                if (!parsed.Succeeded)
                    return parsed;
                children.Add(parsed.Value!);
            }
        }
        return Parsed<PackDocumentSpineNode>.Pass(new(id!, labelKey!, binding!, children));
    }

    private static Parsed<PackPanelDeclaration> ParsePanel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return Parsed<PackPanelDeclaration>.Fail(Malformed("A panel must be an object."));
        var properties = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Overlaps(["chrome", "affordances", "overflow", "tabs", "scrollRegions", "headerActions",
                "toolbarOrder", "fillHeight", "naturalHeight", "heightKind", "headerCustomization"]))
            return Parsed<PackPanelDeclaration>.Fail(Refused(PackNavigationAdmissionCodes.PanelChromeDeclared,
                "A panel declares shell-owned chrome, order, height kind, or an extra scroll region."));

        var common = new[] { "id", "binding", "shortcut", "defaultWidth", "minimumHeight", "defaultOpen" };
        if (!TryStableId(element, "id", out var id)
            || !TryReference(element, "binding", out var binding)
            || !TryReference(element, "shortcut", out var shortcut)
            || !TryInt32(element, "defaultWidth", 240, 1200, out var width)
            || !TryInt32(element, "minimumHeight", 100, 1000, out var minimumHeight)
            || !element.TryGetProperty("defaultOpen", out var defaultOpenElement)
            || defaultOpenElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Parsed<PackPanelDeclaration>.Fail(Malformed("A panel's identity, binding, shortcut, sizes, or default-open state is invalid."));

        if (KnownPanels.TryGetValue(id!, out var known))
        {
            if (!HasExactProperties(element, common, []))
                return Parsed<PackPanelDeclaration>.Fail(Refused(PackNavigationAdmissionCodes.KnownPanelShapeDeclared,
                    $"Known panel '{id}' has kernel-fixed header, body, footer, traits, and label."));
            var knownFooter = id == "pilot"
                ? new PackPanelFooter("Control", known.FooterClaimKey, "pilot.ask")
                : new PackPanelFooter("Claim", known.FooterClaimKey, null);
            return Parsed<PackPanelDeclaration>.Pass(new(
                id!, known.LabelKey, binding!, shortcut!, width, minimumHeight, defaultOpenElement.GetBoolean(),
                known.HeaderForm, known.BodyTemplate, knownFooter, known.Traits));
        }

        if (!HasExactProperties(element,
                [.. common, "labelKey", "headerForm", "bodyTemplate", "footer", "traits"], [] )
            || !TryLabelKey(element, "labelKey", out var labelKey)
            || !TryString(element, "headerForm", out var headerForm)
            || headerForm is not ("Title" or "SwitcherItem")
            || !TryString(element, "bodyTemplate", out var bodyTemplate)
            || !TryStringArray(element.GetProperty("traits"), 0, Traits.Count, out var traits)
            || !TryFooter(element.GetProperty("footer"), out var footer))
            return Parsed<PackPanelDeclaration>.Fail(Malformed("A custom panel must declare its label, header form, body template, footer, and traits."));

        if (bodyTemplate is not ("Feed" or "Library" or "Decision" or "Fields"))
            return Parsed<PackPanelDeclaration>.Fail(Refused(PackNavigationAdmissionCodes.UnknownBodyTemplate,
                $"Panel '{id}' declares unknown body template '{bodyTemplate}'."));
        var unknownTrait = traits!.FirstOrDefault(trait => !Traits.Contains(trait));
        if (unknownTrait is not null)
            return Parsed<PackPanelDeclaration>.Fail(Refused(PackNavigationAdmissionCodes.UnknownTrait,
                $"Panel '{id}' declares unknown trait '{unknownTrait}'."));
        if (!IsLawfulShape(headerForm!, bodyTemplate, traits!))
            return Parsed<PackPanelDeclaration>.Fail(Refused(PackNavigationAdmissionCodes.TraitTemplateMismatch,
                $"Panel '{id}' violates the header/body/trait shell laws."));

        return Parsed<PackPanelDeclaration>.Pass(new(
            id!, labelKey!, binding!, shortcut!, width, minimumHeight, defaultOpenElement.GetBoolean(),
            headerForm!, bodyTemplate, footer!, traits!));
    }

    private static bool IsLawfulShape(string header, string body, IReadOnlyCollection<string> traits)
    {
        var scoped = traits.Contains("Scoped");
        var collection = traits.Contains("Collection");
        var opensOne = traits.Contains("OpensOne");
        var live = traits.Contains("Live");
        var consequential = traits.Contains("Consequential");
        if ((header == "SwitcherItem") != opensOne)
            return false;
        return body switch
        {
            "Feed" => collection && !scoped && !opensOne && !consequential,
            "Library" => scoped && collection && opensOne && !live && !consequential,
            "Decision" => scoped && !collection && !opensOne,
            "Fields" => scoped && !collection && !opensOne && !live && !consequential,
            _ => false,
        };
    }

    private static bool TryFooter(JsonElement element, out PackPanelFooter? footer)
    {
        footer = null;
        if (!HasExactProperties(element, ["kind", "labelKey"], ["binding"])
            || !TryString(element, "kind", out var kind)
            || kind is not ("Claim" or "Control")
            || !TryLabelKey(element, "labelKey", out var labelKey)
            || !TryOptionalReference(element, "binding", out var binding)
            || (kind == "Claim" && binding is not null)
            || (kind == "Control" && binding is null))
            return false;
        footer = new(kind!, labelKey!, binding);
        return true;
    }

    private static PackNavigationParseResult? FindForbiddenDeclaration(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "disabled" or "enabled")
                    return Refused(PackNavigationAdmissionCodes.DisabledEntry,
                        "Unavailable navigation capabilities must be absent, never disabled.");
                if (property.Name is "zoneOrder" or "zones")
                    return Refused(PackNavigationAdmissionCodes.ZoneOrderDeclared,
                        "Rail zone order belongs to the kernel.");
                if (property.Name is "badge" or "badgeCount")
                    return Refused(PackNavigationAdmissionCodes.SystemBadgeDeclared,
                        "Packs cannot declare notification badges on system entries.");
                var nested = FindForbiddenDeclaration(property.Value);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindForbiddenDeclaration(item);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static void AddRoles(List<DeclarativeGateReference> gates, string gate, IEnumerable<string> roles)
        => gates.AddRange(roles.Select(role => DeclarativeGateReference.ForRole(gate, role)));

    private static bool HasExactProperties(JsonElement element, IReadOnlyCollection<string> required, IReadOnlyCollection<string> optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        return required.All(seen.Contains);
    }

    private static bool TryArray(JsonElement element, int minimum, int maximum, out JsonElement[]? values)
    {
        values = null;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var parsed = element.EnumerateArray().ToArray();
        if (parsed.Length < minimum || parsed.Length > maximum)
            return false;
        values = parsed;
        return true;
    }

    private static bool TryStableIdArray(JsonElement element, int minimum, int maximum, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!TryArray(element, minimum, maximum, out var elements))
            return false;
        var parsed = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elements!)
        {
            if (item.ValueKind != JsonValueKind.String || !IsStableId(item.GetString(), out var value) || !unique.Add(value!))
                return false;
            parsed.Add(value!);
        }
        values = parsed;
        return true;
    }

    private static bool TryStringArray(JsonElement element, int minimum, int maximum, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!TryArray(element, minimum, maximum, out var elements))
            return false;
        var parsed = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elements!)
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                || item.GetString()!.Length > MaxTextLength || !unique.Add(item.GetString()!))
                return false;
            parsed.Add(item.GetString()!);
        }
        values = parsed;
        return true;
    }

    private static bool TryStableId(JsonElement element, string property, out string? value)
    {
        value = null;
        return element.TryGetProperty(property, out var candidate)
               && candidate.ValueKind == JsonValueKind.String
               && IsStableId(candidate.GetString(), out value);
    }

    private static bool IsStableId(string? candidate, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxIdLength
            || !(char.IsAsciiLetterLower(candidate[0]) || char.IsAsciiDigit(candidate[0]))
            || candidate.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '_')))
            return false;
        value = candidate;
        return true;
    }

    private static bool TryLabelKey(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!TryString(element, property, out var text) || !text!.Contains('.', StringComparison.Ordinal)
            || !char.IsAsciiLetterLower(text[0])
            || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            return false;
        value = text;
        return true;
    }

    private static bool TryToken(JsonElement element, string property, out string? value)
    {
        value = null;
        return element.TryGetProperty(property, out var candidate)
               && candidate.ValueKind == JsonValueKind.String
               && IsStableId(candidate.GetString(), out value);
    }

    private static bool TryOptionalToken(JsonElement element, string property, out string? value)
    {
        value = null;
        return !element.TryGetProperty(property, out _) || TryToken(element, property, out value);
    }

    private static bool TryReference(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!TryString(element, property, out var text)
            || text!.Any(char.IsWhiteSpace))
            return false;
        value = text;
        return true;
    }

    private static bool TryOptionalReference(JsonElement element, string property, out string? value)
    {
        value = null;
        return !element.TryGetProperty(property, out _) || TryReference(element, property, out value);
    }

    private static bool TryString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind != JsonValueKind.String)
            return false;
        var text = candidate.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return false;
        value = text;
        return true;
    }

    private static bool TryInt32(JsonElement element, string property, int minimum, int maximum, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var candidate)
               && candidate.ValueKind == JsonValueKind.Number
               && candidate.TryGetInt32(out value)
               && value >= minimum && value <= maximum;
    }

    private static PackNavigationParseResult Malformed(string message)
        => Refused(PackNavigationAdmissionCodes.Malformed, message);

    private static PackNavigationParseResult Refused(string code, string message)
        => new(null, new PackNavigationRefusal(code, message));

    private sealed record KnownPanelShape(
        string LabelKey,
        string HeaderForm,
        string BodyTemplate,
        string FooterClaimKey,
        IReadOnlyList<string> Traits);

    private sealed record Parsed<T>(T? Value, PackNavigationParseResult Result)
        where T : class
    {
        internal bool Succeeded => Value is not null;
        internal static Parsed<T> Pass(T value) => new(value, new PackNavigationParseResult(null, null));
        internal static Parsed<T> Fail(PackNavigationParseResult result) => new(null, result);
    }
}
