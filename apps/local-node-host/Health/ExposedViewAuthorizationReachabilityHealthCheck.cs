using System.Collections.Concurrent;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.ViewDefinitions;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>One exposed view no seeded platform-role root can reach through an active binding.</summary>
public sealed record ExposedViewAuthorizationReachabilityFinding(
    string Code,
    string Pointer,
    string DefinitionId,
    string Version,
    string Pack,
    string Capability,
    IReadOnlyList<string> RolesChecked);

/// <summary>Whole-catalogue authorization reachability over exposed view definitions.</summary>
public static class ExposedViewAuthorizationReachability
{
    public const string UnreachableCode = "authorization.exposed_view_unreachable";
    public const string MissingCapabilityCode = "authorization.exposed_view_capability_missing";
    public const string UndeclaredCapability = "<undeclared>";

    private static readonly IReadOnlyList<RoleReference> PlatformRoots =
        [RoleReference.Administrator, RoleReference.Auditor];

    /// <summary>
    /// Finds exposed views whose declared authorization operation is absent from every effective
    /// binding rooted at the sealed Administrator or Auditor role. Findings never refuse activation.
    /// </summary>
    public static async Task<IReadOnlyList<ExposedViewAuthorizationReachabilityFinding>> FindAsync(
        TenantId tenant,
        IReadOnlyList<InstalledPack> installedPacks,
        IViewDefinitionRegistry views,
        IAuthorizationDefinitionCatalogueReader authorizationDefinitions,
        IRoleVocabularyReader roles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedPacks);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(authorizationDefinitions);
        ArgumentNullException.ThrowIfNull(roles);

        var installedRoots = (await roles.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(definition => definition.IsSealed
                && PlatformRoots.Contains(definition.Role))
            .Select(definition => definition.Role)
            .ToHashSet();
        var bindings = await authorizationDefinitions.ListAsync(tenant, cancellationToken).ConfigureAwait(false);
        var reachableCapabilities = bindings
            .Where(binding => binding.EffectiveRoles.Roles.Any(installedRoots.Contains))
            .Select(binding => binding.Definition.Operation.Value)
            .ToHashSet(StringComparer.Ordinal);
        var rolesChecked = PlatformRoots.Select(role => role.ToString()).ToArray();
        var findings = new List<ExposedViewAuthorizationReachabilityFinding>();

        foreach (var pack in installedPacks
                     .Where(pack => pack.Lifecycle == PackLifecycleState.Active)
                     .OrderBy(pack => pack.PackKey, StringComparer.Ordinal)
                     .ThenBy(pack => pack.Version, StringComparer.Ordinal))
        {
            var exposed = (pack.Exposes ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            if (exposed.Count == 0)
            {
                continue;
            }

            foreach (var (item, index) in pack.SeedItems.Select((item, index) => (item, index)))
            {
                if (item.Kind != PackContentKind.ViewDefinition || !exposed.Contains(item.Key))
                {
                    continue;
                }

                var definition = await views
                    .GetDefinitionAsync(tenant.Value, item.Key, item.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (definition is null)
                {
                    continue;
                }

                var capability = definition.AuthorizationCapability;
                if (string.IsNullOrWhiteSpace(capability))
                {
                    findings.Add(new ExposedViewAuthorizationReachabilityFinding(
                        MissingCapabilityCode,
                        $"/contents/{index}/content/authorizationCapability",
                        definition.Key,
                        definition.Version,
                        pack.PackKey,
                        UndeclaredCapability,
                        rolesChecked));
                }
                else if (!reachableCapabilities.Contains(capability))
                {
                    findings.Add(new ExposedViewAuthorizationReachabilityFinding(
                        UnreachableCode,
                        $"/contents/{index}/content/authorizationCapability",
                        definition.Key,
                        definition.Version,
                        pack.PackKey,
                        capability,
                        rolesChecked));
                }
            }
        }

        return findings;
    }
}

/// <summary>Stores the latest post-projection reachability findings for Health.</summary>
public sealed class ExposedViewAuthorizationReachabilityReports
{
    private readonly ConcurrentDictionary<TenantId,
        IReadOnlyList<ExposedViewAuthorizationReachabilityFinding>> findings = new();

    public async Task RefreshAsync(
        TenantId tenant,
        IReadOnlyList<InstalledPack> installedPacks,
        IViewDefinitionRegistry views,
        IAuthorizationDefinitionCatalogueReader authorizationDefinitions,
        IRoleVocabularyReader roles,
        CancellationToken cancellationToken = default)
        => findings[tenant] = await ExposedViewAuthorizationReachability.FindAsync(
            tenant, installedPacks, views, authorizationDefinitions, roles, cancellationToken)
            .ConfigureAwait(false);

    public IReadOnlyList<ExposedViewAuthorizationReachabilityFinding> Inspect(TenantId tenant) =>
        findings.TryGetValue(tenant, out var current)
            ? current
            : Array.Empty<ExposedViewAuthorizationReachabilityFinding>();

    public IReadOnlyList<ExposedViewAuthorizationReachabilityFinding> InspectAll() =>
        findings.Values.SelectMany(current => current).ToArray();
}

/// <summary>Reports non-refusing exposed-view reachability findings through aggregate Health.</summary>
public sealed class ExposedViewAuthorizationReachabilityHealthCheck(
    ExposedViewAuthorizationReachabilityReports reports) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var findings = reports.InspectAll();
        if (findings.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Every declared exposed view authorization capability is reachable from a platform role."));
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["exposedViewAuthorizationReachabilityFindings"] = findings,
        };
        return Task.FromResult(HealthCheckResult.Degraded(
            $"Authorization catalogue contains {findings.Count} unreachable exposed view(s).",
            data: data));
    }
}
