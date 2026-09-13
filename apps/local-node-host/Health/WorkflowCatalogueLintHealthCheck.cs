using System.Collections.Concurrent;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>One non-refusing diagnostic from the active, compiled workflow catalogue.</summary>
public sealed record WorkflowCatalogueLintFinding(
    string Code,
    string Pointer,
    string DefinitionId,
    string Version,
    string Pack);

/// <summary>
/// The one catalogue lint rule: a state unreachable from a workflow's initial state is reported after
/// activation. It deliberately returns a finding rather than participating in admission or activation.
/// </summary>
public static class WorkflowCatalogueLint
{
    public const string UnreachableStateCode = "workflow.unreachable_state";

    public static IReadOnlyList<WorkflowCatalogueLintFinding> Find(
        IReadOnlyList<WorkflowDefinitionRecord> definitions,
        IReadOnlyList<InstalledPack> installedPacks)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(installedPacks);

        var findings = new List<WorkflowCatalogueLintFinding>();
        foreach (var definition in definitions.Where(definition =>
                     definition.Status == WorkflowDefinitionStatus.Published
                     && definition.PackSource is not null))
        {
            var source = definition.PackSource!;
            var pack = installedPacks.SingleOrDefault(candidate =>
                candidate.Lifecycle == PackLifecycleState.Active
                && string.Equals(candidate.PackKey, source.PackId, StringComparison.Ordinal)
                && string.Equals(candidate.Version, source.PackVersion, StringComparison.Ordinal));
            if (pack is null)
            {
                continue;
            }

            var model = WorkflowDefinitionWireMapper.ToModel(
                definition.Authored, definition.Tenant, definition.Key, definition.Version, definition.Envelope.CascadeLayer);
            var reachable = new HashSet<string>(StringComparer.Ordinal) { model.InitialState };
            var pending = new Queue<string>();
            pending.Enqueue(model.InitialState);
            while (pending.TryDequeue(out var current))
            {
                foreach (var next in model.Transitions
                             .Where(transition => string.Equals(transition.From, current, StringComparison.Ordinal))
                             .Select(transition => transition.To))
                {
                    if (reachable.Add(next))
                    {
                        pending.Enqueue(next);
                    }
                }
            }

            var contentIndex = pack.SeedItems
                .Select((item, index) => (item, index))
                .Single(tuple => tuple.item.Kind == PackContentKind.WorkflowDefinition
                    && string.Equals(tuple.item.Key, definition.Key, StringComparison.Ordinal)
                    && string.Equals(tuple.item.Version, definition.Version, StringComparison.Ordinal))
                .index;
            foreach (var (state, stateIndex) in model.States.Select((state, index) => (state, index)))
            {
                if (!reachable.Contains(state.Id))
                {
                    findings.Add(new WorkflowCatalogueLintFinding(
                        UnreachableStateCode,
                        $"/contents/{contentIndex}/content/states/{stateIndex}",
                        definition.Key,
                        definition.Version,
                        source.PackId));
                }
            }
        }

        return findings;
    }
}

/// <summary>Stores the latest post-activation workflow-lint findings for the Health surface.</summary>
public sealed class WorkflowCatalogueLintReports
{
    private readonly ConcurrentDictionary<TenantId, IReadOnlyList<WorkflowCatalogueLintFinding>> findings = new();

    public async Task RefreshAsync(
        AuthorizedWorkflowDefinitionLifecycle workflows,
        TenantId tenant,
        IReadOnlyList<InstalledPack> installedPacks,
        CancellationToken cancellationToken)
    {
        var definitions = new List<WorkflowDefinitionRecord>();
        await foreach (var definition in workflows.ListByTenantAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(definition);
        }

        findings[tenant] = WorkflowCatalogueLint.Find(definitions, installedPacks);
    }

    public IReadOnlyList<WorkflowCatalogueLintFinding> Inspect(TenantId tenant) =>
        findings.TryGetValue(tenant, out var current) ? current : Array.Empty<WorkflowCatalogueLintFinding>();

    internal void Replace(TenantId tenant, IReadOnlyList<WorkflowCatalogueLintFinding> current) =>
        findings[tenant] = current;
}

/// <summary>Surfaces workflow catalogue lint findings through the existing aggregate Health endpoint.</summary>
public sealed class WorkflowCatalogueLintHealthCheck(
    WorkflowCatalogueLintReports reports,
    IActiveTeamAccessor activeTeam) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (activeTeam.Active is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy());
        }

        var findings = reports.Inspect(NodeTenant.Resolve(activeTeam));
        if (findings.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Compiled workflow catalogue has no unreachable states."));
        }

        var shown = string.Join(", ", findings.Select(finding =>
            $"{finding.DefinitionId} v{finding.Version} (pack {finding.Pack})"));
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["workflowCatalogueLintFindings"] = findings,
        };
        return Task.FromResult(HealthCheckResult.Degraded(
            $"Compiled workflow catalogue contains {findings.Count} unreachable state(s): {shown}.",
            data: data));
    }
}
