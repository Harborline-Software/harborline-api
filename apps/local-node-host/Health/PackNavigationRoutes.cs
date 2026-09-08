using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Navigation;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T3-3 — tenant-scoped, read-only projection of every Active pack's
/// <see cref="PackContentKind.NavWorkspaceConfig"/> contribution into the one deterministic workspace
/// model Harborline App consumes. The immutable pack seed layers remain the source of truth; this route creates
/// no second navigation store and never changes pack activation state.
/// </summary>
public static class PackNavigationRoutes
{
    /// <summary>Route: the composed navigation model for the active tenant.</summary>
    public const string NavigationRoute = "/api/local-node/navigation/workspaces";

    /// <summary>The server-owned identity of the composed model; pack content cannot assert it.</summary>
    internal const string ComposedPackId = "harborline.active-pack-composition";

    /// <summary>Maps the read-only navigation projection.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IPackInstallStore store,
        IActiveTeamAccessor activeTeam,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet(NavigationRoute, () =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var result = PackNavigationComposer.Compose(store, tenant);
            if (result.Error is not null)
            {
                logger.LogWarning(
                    "Pack navigation projection refused for tenant {Tenant}: {Code} ({Targets}).",
                    tenant, result.Error.Code, string.Join(",", result.Error.Targets));

                var error = new PackNavigationErrorDto(result.Error.Code, result.Error.Targets);
                return result.Error.IsConflict
                    ? Results.Conflict(error)
                    : Results.UnprocessableEntity(error);
            }

            return Results.Ok(new PackNavigationResponseDto(
                Configured: result.Pack is not null,
                Pack: result.Pack));
        });
    }

    internal static class Codes
    {
        internal const string Malformed = PackNavigationAdmissionCodes.Malformed;
        internal const string BoundsExceeded = PackNavigationAdmissionCodes.BoundsExceeded;
        internal const string DuplicateWorkspace = PackNavigationAdmissionCodes.DuplicateWorkspace;
        internal const string DuplicateGroup = PackNavigationAdmissionCodes.DuplicateGroup;
        internal const string DuplicateItem = PackNavigationAdmissionCodes.DuplicateItem;
        internal const string DuplicatePanel = PackNavigationAdmissionCodes.DuplicatePanel;
        internal const string DuplicateModeSwitch = PackNavigationAdmissionCodes.DuplicateModeSwitch;
        internal const string ContentKeyConflict = "pack.nav.content_key_conflict";
    }

    internal sealed record ComposeResult(PackNavigationPackDto? Pack, ComposeError? Error);

    internal sealed record ComposeError(string Code, IReadOnlyList<string> Targets, bool IsConflict);

    /// <summary>
    /// Pure deterministic composer used by the route and focused tests. Ordering depends only on
    /// <c>(packKey, contentKey)</c>, never install or activation sequence. Same-content-key ownership uses
    /// the pack engine's existing collision authority; workspace/item collisions across otherwise distinct
    /// contributions refuse rather than silently choosing a winner.
    /// </summary>
    internal static class PackNavigationComposer
    {
        internal static ComposeResult Compose(IPackInstallStore store, TenantId tenant)
        {
            var installed = store.ListInstalled(tenant);
            var activeInstalled = installed
                .Where(p => p.Lifecycle == PackLifecycleState.Active)
                .ToList();
            var collisions = PackCompositionConflicts.Detect(
                    PackCompositionConflicts.ClaimsFromInstalled(activeInstalled),
                    store.GetKeyOwnership(tenant))
                .ToDictionary(c => c.ContentKey, StringComparer.Ordinal);

            var contributions = new List<(string PackKey, string ContentKey, PackSeedItem Item)>();
            foreach (var pack in activeInstalled.OrderBy(p => p.PackKey, StringComparer.Ordinal))
            {
                foreach (var item in pack.SeedItems
                             .Where(i => i.Kind == PackContentKind.NavWorkspaceConfig)
                             .OrderBy(i => i.Key, StringComparer.Ordinal))
                {
                    if (collisions.TryGetValue(item.Key, out var collision))
                    {
                        if (collision.OwnerPackKey is null)
                        {
                            return Refused(Codes.ContentKeyConflict, collision.ClaimingPackKeys, isConflict: true);
                        }

                        if (!string.Equals(collision.OwnerPackKey, pack.PackKey, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }

                    contributions.Add((pack.PackKey, item.Key, item));
                }
            }

            if (contributions.Count == 0)
            {
                return new ComposeResult(null, null);
            }

            var workspaces = new List<PackNavigationWorkspaceDto>();
            var workspaceIds = new HashSet<string>(StringComparer.Ordinal);
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var panels = new List<PackPanelDeclaration>();
            var panelIds = new HashSet<string>(StringComparer.Ordinal);
            PackNavigationModeSwitch? modeSwitch = null;

            foreach (var contribution in contributions)
            {
                if (!PackNavigationContent.TryParse(
                        contribution.Item.CanonicalJson,
                        out var parsed,
                        out var parseError))
                {
                    return Refused(
                        parseError!.Code,
                        new[] { contribution.PackKey, contribution.ContentKey },
                        isConflict: false);
                }

                if (workspaces.Count + parsed!.SeedWorkspaces.Count > PackNavigationContent.MaxWorkspaces)
                {
                    return Refused(
                        Codes.BoundsExceeded,
                        new[] { contribution.PackKey, contribution.ContentKey },
                        isConflict: false);
                }

                if (parsed.ModeSwitch is not null)
                {
                    if (modeSwitch is not null)
                        return Refused(Codes.DuplicateModeSwitch, [contribution.ContentKey], isConflict: true);
                    modeSwitch = parsed.ModeSwitch;
                }

                foreach (var panel in parsed.PanelSet)
                {
                    if (!panelIds.Add(panel.Id))
                        return Refused(Codes.DuplicatePanel, [panel.Id], isConflict: true);
                    panels.Add(panel);
                }

                foreach (var declaration in parsed.SeedWorkspaces)
                {
                    var workspace = ToDto(declaration);
                    if (!workspaceIds.Add(workspace.Id))
                    {
                        return Refused(Codes.DuplicateWorkspace, new[] { workspace.Id }, isConflict: true);
                    }

                    foreach (var itemId in workspace.Groups.SelectMany(g => g.ItemIds))
                    {
                        if (!itemIds.Add(itemId))
                        {
                            return Refused(Codes.DuplicateItem, new[] { itemId }, isConflict: true);
                        }
                    }

                    workspaces.Add(workspace);
                }
            }

            return new ComposeResult(new PackNavigationPackDto(
                ComposedPackId, workspaces, modeSwitch, panels), null);
        }

        private static PackNavigationWorkspaceDto ToDto(PackNavigationWorkspace workspace)
            => new(
                workspace.Id,
                workspace.LabelKey,
                workspace.Groups.Select(group => new PackNavigationGroupDto(
                    group.Id,
                    group.LabelKey,
                    group.ItemIds,
                    group.DestinationQueryRef,
                    group.CountQueryRef,
                    group.AddAction, group.Items.Select(item => new PackNavigationItemDto(
                        item.Id, item.LabelKey, PackNavigationLabels.Resolve(item.LabelKey))).ToArray())).ToArray(),
                workspace.DefaultForPersonas,
                workspace.Icon,
                workspace.DestinationQueryRef,
                workspace.CountQueryRef,
                workspace.CreateActions,
                workspace.DocumentSpine);

        private static ComposeResult Refused(string code, IEnumerable<string> targets, bool isConflict)
            => new(null, new ComposeError(
                code,
                targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                isConflict));
    }

    /// <summary>Compatibility facade over the shared install/read declaration parser.</summary>
    internal static class PackNavigationContent
    {
        internal const int MaxWorkspaces = PackNavigationDeclarationParser.MaxWorkspaces;

        internal static bool TryParse(
            string canonicalJson,
            out PackNavigationDeclaration? declaration,
            out ComposeError? error)
        {
            var parsed = PackNavigationDeclarationParser.Parse(canonicalJson);
            declaration = parsed.Declaration;
            error = parsed.Refusal is null
                ? null
                : new ComposeError(parsed.Refusal.Code, Array.Empty<string>(), IsConflict: false);
            return parsed.Succeeded;
        }
    }
}

/// <summary>Navigation projection response. <see cref="Pack"/> is null when no Active pack contributes.</summary>
public sealed record PackNavigationResponseDto(bool Configured, PackNavigationPackDto? Pack);

/// <summary>The one composed Harborline App navigation model.</summary>
public sealed record PackNavigationPackDto(
    string PackId,
    IReadOnlyList<PackNavigationWorkspaceDto> SeedWorkspaces,
    PackNavigationModeSwitch? ModeSwitch = null,
    IReadOnlyList<PackPanelDeclaration>? PanelSet = null);

/// <summary>One pack-composed workspace; labels are existing Harborline App i18n keys, never literals.</summary>
public sealed record PackNavigationWorkspaceDto(
    string Id,
    string LabelKey,
    IReadOnlyList<PackNavigationGroupDto> Groups,
    IReadOnlyList<string>? DefaultForPersonas = null,
    string? Icon = null,
    string? DestinationQueryRef = null,
    string? CountQueryRef = null,
    IReadOnlyList<PackNavigationAction>? CreateActions = null,
    IReadOnlyList<PackDocumentSpineNode>? DocumentSpine = null);

/// <summary>One workspace group.</summary>
public sealed record PackNavigationGroupDto(
    string Id,
    string LabelKey,
    IReadOnlyList<string> ItemIds,
    string? DestinationQueryRef = null,
    string? CountQueryRef = null,
    PackNavigationAction? AddAction = null,
    IReadOnlyList<PackNavigationItemDto>? Items = null);

/// <summary>A declared rail item with its resource key and localized display text.</summary>
public sealed record PackNavigationItemDto(string Id, string LabelKey, string Label);

/// <summary>Stable localizable refusal code plus non-sensitive conflict targets.</summary>
public sealed record PackNavigationErrorDto(string Code, IReadOnlyList<string> Targets);
