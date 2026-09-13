using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class WorkflowCatalogueLintHealthCheckTests
{
    private static readonly TenantId Tenant = new("workflow-lint-tenant");

    [Fact]
    public async Task Fixture_pack_with_one_unreachable_state_reports_one_Health_finding_with_definition_version_and_pack()
    {
        var (definition, pack) = CompiledWorkflow(
            "fixture.workflow", "2.3.4", new[] { "Draft", "Orphan" }, "Draft", []);
        var findings = WorkflowCatalogueLint.Find([definition], [pack]);

        var finding = Assert.Single(findings);
        Assert.Equal(WorkflowCatalogueLint.UnreachableStateCode, finding.Code);
        Assert.Equal("/contents/0/content/states/1", finding.Pointer);
        Assert.Equal("fixture.workflow", finding.DefinitionId);
        Assert.Equal("2.3.4", finding.Version);
        Assert.Equal("fixture-pack", finding.Pack);

        var reports = new WorkflowCatalogueLintReports();
        var activeTeam = ActiveTeam();
        reports.Replace(NodeTenant.Resolve(activeTeam), findings);
        var result = await new WorkflowCatalogueLintHealthCheck(reports, activeTeam).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("fixture.workflow v2.3.4 (pack fixture-pack)", result.Description, StringComparison.Ordinal);
        var reported = Assert.IsAssignableFrom<IReadOnlyList<WorkflowCatalogueLintFinding>>(
            result.Data["workflowCatalogueLintFindings"]);
        Assert.Equal(finding, Assert.Single(reported));
    }

    [Fact]
    public void Shipped_packs_produce_zero_workflow_catalogue_lint_findings()
    {
        var compiled = ShippedWorkflowDefinitions();

        var findings = WorkflowCatalogueLint.Find(
            compiled.Select(item => item.definition).ToArray(),
            compiled.Select(item => item.pack).DistinctBy(pack => (pack.PackKey, pack.Version)).ToArray());

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Pack_without_an_unreachable_state_shows_nothing_on_Health()
    {
        var (definition, pack) = CompiledWorkflow(
            "healthy.workflow", "1.0.0", new[] { "Draft", "Done" }, "Draft", [("Draft", "Done")]);
        var reports = new WorkflowCatalogueLintReports();
        var activeTeam = ActiveTeam();
        reports.Replace(NodeTenant.Resolve(activeTeam), WorkflowCatalogueLint.Find([definition], [pack]));

        var result = await new WorkflowCatalogueLintHealthCheck(reports, activeTeam).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Empty(reports.Inspect(NodeTenant.Resolve(activeTeam)));
    }

    [Fact]
    public void Initial_state_with_no_inbound_transition_is_not_reported()
    {
        var (definition, pack) = CompiledWorkflow(
            "initial.workflow", "1.0.0", new[] { "Draft", "Done" }, "Draft", [("Draft", "Done")]);

        Assert.Empty(WorkflowCatalogueLint.Find([definition], [pack]));
    }

    [Fact]
    public void State_reachable_only_through_a_multi_hop_chain_is_not_reported()
    {
        var (definition, pack) = CompiledWorkflow(
            "multi-hop.workflow", "1.0.0", new[] { "Draft", "Review", "Done" }, "Draft",
            [("Draft", "Review"), ("Review", "Done")]);

        Assert.Empty(WorkflowCatalogueLint.Find([definition], [pack]));
    }

    private static (WorkflowDefinitionRecord definition, InstalledPack pack) CompiledWorkflow(
        string key,
        string version,
        IReadOnlyList<string> states,
        string initialState,
        IReadOnlyList<(string from, string to)> transitions)
    {
        var content = JsonSerializer.Serialize(new
        {
            key,
            version,
            initialState,
            states = states.Select(state => new { id = state, kind = "Normal" }).ToArray(),
            transitions = transitions.Select((transition, index) => new
            {
                id = $"t-{index}", from = transition.from, to = transition.to, on = "advance",
            }).ToArray(),
            triggers = new[] { new { id = "advance", kind = "Event", eventType = "Advance" } },
            actions = Array.Empty<object>(),
            guards = Array.Empty<object>(),
        });
        using var document = JsonDocument.Parse(content);
        var definition = new WorkflowDefinitionRecord(Tenant.Value, key, version, WorkflowDefinitionStatus.Published, document.RootElement.Clone())
        {
            PackSource = new PackProjectionSource("fixture-pack", "7.0.0"),
        };
        var seed = new PackSeedItem(
            key,
            PackContentKind.WorkflowDefinition,
            version,
            content,
            Cid.FromBytes(Encoding.UTF8.GetBytes(content)));
        var pack = new InstalledPack(
            "fixture-pack", "7.0.0", PackScopeTier.Horizontal, PackLifecycleState.Active,
            [seed], new Dictionary<string, int>(), DateTimeOffset.UnixEpoch,
            PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());
        return (definition, pack);
    }

    private static IReadOnlyList<(WorkflowDefinitionRecord definition, InstalledPack pack)> ShippedWorkflowDefinitions()
    {
        var definitions = new List<(WorkflowDefinitionRecord definition, InstalledPack pack)>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Packs"), "*.export.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var packKey = root.GetProperty("key").GetString()!;
            var packVersion = root.GetProperty("version").GetString()!;
            var items = root.GetProperty("contents").EnumerateArray().ToArray();
            var seeds = items.Select(item =>
            {
                var content = item.GetProperty("content").GetRawText();
                return new PackSeedItem(
                    item.GetProperty("key").GetString()!,
                    Enum.Parse<PackContentKind>(item.GetProperty("kind").GetString()!, ignoreCase: true),
                    item.GetProperty("version").GetString()!, content,
                    Cid.FromBytes(Encoding.UTF8.GetBytes(content)));
            }).ToArray();
            var pack = new InstalledPack(
                packKey, packVersion, PackScopeTier.Horizontal, PackLifecycleState.Active,
                seeds, new Dictionary<string, int>(), DateTimeOffset.UnixEpoch,
                PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster,
                Array.Empty<PackDependencyRef>());
            foreach (var item in items.Where(item => string.Equals(
                         item.GetProperty("kind").GetString(), "WorkflowDefinition", StringComparison.Ordinal)))
            {
                var content = item.GetProperty("content").Clone();
                definitions.Add((new WorkflowDefinitionRecord(
                    Tenant.Value,
                    item.GetProperty("key").GetString()!,
                    item.GetProperty("version").GetString()!,
                    WorkflowDefinitionStatus.Published,
                    content)
                {
                    PackSource = new PackProjectionSource(packKey, packVersion),
                }, pack));
            }
        }
        return definitions;
    }

    private static IActiveTeamAccessor ActiveTeam() => new FixedActiveTeamAccessor(
        new TeamContext(new TeamId(Guid.Parse("22222222-2222-2222-2222-222222222222")), "lint", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
