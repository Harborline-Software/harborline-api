using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// T-1176/api-interim-refusal. DES-0019 workflows-eng-13 and section 6 (L422): the authorization gate resolves before any
/// guard or effect runs, and section 3: the engine may not bypass it. The api's declarative interpreter has no gate for a
/// declared role or standing yet, so it denies by default (OWASP; CWE-862), the same rule and code as platform #424:
/// a gated transition or effecting action refuses with <c>workflow.interpreter.gate_unenforced</c> before anything is
/// selected, built or recorded. The definition is the shipped three-way-match seed with one gate added, served by a fake
/// execution store so the test isolates the interpreter. Removing any refusal call turns one of these red.
/// </summary>
public sealed class WorkflowGateUnenforcedRefusalTests
{
    private static readonly TenantId Tenant = new("local");
    private static readonly Guid HumanConfirmer = Guid.Parse("40000000-0000-0000-0000-0000000000c1");

    [Fact(DisplayName = "T-1176: a human transition that declares a role is refused before any effect, confirmation or override")]
    public async Task A_role_gated_human_transition_is_refused()
    {
        var (sp, built) = Provider(Gate("transitions", "t-approve", "requiredRoles", "sys.platform-roles/auditor"));

        var refusal = await Assert.ThrowsAsync<WorkflowGateUnenforcedException>(async () =>
            await Interpreter(sp).DecideAsync(Instance(NodeThreeWayMatchWorkflowSeed.ReviewState), Human("approve"), Authority()));

        Assert.Equal("workflow.interpreter.gate_unenforced", refusal.Code);
        Assert.Contains("'t-approve'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(built);
        var sink = sp.GetRequiredService<CountingWorkflowApprovalDecisionSink>();
        Assert.Equal(0, sink.ConfirmedCount);
        Assert.Equal(0, sink.OverriddenCount);
    }

    [Fact(DisplayName = "T-1176: an autonomous transition that declares a standing is refused")]
    public async Task A_standing_gated_autonomous_transition_is_refused()
    {
        var (sp, built) = Provider(Gate("transitions", "t-match", "requiredStandings", "assigned-approver"));

        var refusal = await Assert.ThrowsAsync<WorkflowGateUnenforcedException>(async () =>
            await Interpreter(sp).DecideAsync(
                Instance(NodeThreeWayMatchWorkflowSeed.MatchingState),
                WorkflowTrigger.For(WorkflowTriggerKind.Event, InstanceId, NodeThreeWayMatchWorkflowSeed.MatchingState),
                Authority()));

        Assert.Equal(DeclarativeWorkflowInterpreterCodes.GateUnenforced, refusal.Code);
        Assert.Contains("'t-match'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(built);
    }

    [Fact(DisplayName = "T-1176: an effecting action that declares a role is refused before the broker builds its effect")]
    public async Task A_role_gated_effecting_action_is_refused_before_its_effect_is_built()
    {
        var (sp, built) = Provider(Gate("actions", "a-post-bill", "requiredRoles", "sys.platform-roles/auditor"));

        var refusal = await Assert.ThrowsAsync<WorkflowGateUnenforcedException>(async () =>
            await Interpreter(sp).DecideAsync(Instance(NodeThreeWayMatchWorkflowSeed.ReviewState), Human("approve"), Authority()));

        Assert.Equal(DeclarativeWorkflowInterpreterCodes.GateUnenforced, refusal.Code);
        Assert.Contains("'a-post-bill'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(built);
        Assert.Equal(0, sp.GetRequiredService<CountingWorkflowApprovalDecisionSink>().ConfirmedCount);
    }

    [Fact(DisplayName = "T-1176: an ungated path beside a gated one still runs")]
    public async Task An_ungated_path_beside_a_gate_still_runs()
    {
        var (sp, built) = Provider(Gate("transitions", "t-approve", "requiredRoles", "sys.platform-roles/auditor"));

        var outcome = await Interpreter(sp).DecideAsync(
            Instance(NodeThreeWayMatchWorkflowSeed.ReviewState), Human("reject"), Authority());

        Assert.Equal("rejected", outcome.NextStep);
        Assert.Null(outcome.Effect);
        Assert.Empty(built);
        Assert.Equal(1, sp.GetRequiredService<CountingWorkflowApprovalDecisionSink>().OverriddenCount);
    }

    // ── Harness ──

    private const string InstanceId = NodeThreeWayMatchWorkflowSeed.DefinitionKey + ":bill-gate";

    private static IDeclarativeWorkflowInterpreter Interpreter(IServiceProvider sp)
        => sp.GetRequiredService<IDeclarativeWorkflowInterpreter>();

    private static WorkflowTrigger Human(string verb) => WorkflowTrigger.For(
        WorkflowTriggerKind.HumanAction, InstanceId, NodeThreeWayMatchWorkflowSeed.ReviewState, $"{{\"decision\":\"{verb}\"}}");

    private static WorkflowDispatchAuthority Authority() => new(TestAuthorization.AllowedDecision(Tenant, InstanceId));

    private static WorkflowInstanceRecord Instance(string step) => new()
    {
        Id = InstanceId,
        TenantId = Tenant.Value,
        DefinitionKey = NodeThreeWayMatchWorkflowSeed.DefinitionKey,
        DefinitionVersion = NodeThreeWayMatchWorkflowSeed.Version,
        CurrentStep = step,
        Status = step == NodeThreeWayMatchWorkflowSeed.MatchingState ? WorkflowStatus.Running : WorkflowStatus.Parked,
        StateJson = "{\"billId\":\"bill-gate\",\"amount\":10,\"debitAccount\":\"6000\",\"creditAccount\":\"2000\",\"memo\":\"gate\"}",
    };

    /// <summary>The shipped seed with one role or standing gate added to one transition or action.</summary>
    private static JsonElement Gate(string collection, string id, string member, string value)
    {
        var root = JsonNode.Parse(NodeThreeWayMatchWorkflowSeed.Authored(Tenant.Value).GetRawText())!.AsObject();
        var element = root[collection]!.AsArray().Single(e => (string?)e!["id"] == id)!.AsObject();
        element[member] = new JsonArray(value);
        return JsonSerializer.SerializeToElement(root);
    }

    private static (ServiceProvider Provider, List<string> Built) Provider(JsonElement authored)
    {
        var built = new List<string>();
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddTestAuthorizationGate();
        services.AddDurableWorkflowEngine();
        // The ledger post is registered so the catalog sees an effecting capability; it only counts builds, because
        // the assertion is that a refused element never reaches the broker.
        services.AddWorkflowEffectFactory(
            NodeLedgerPostingEffect.CapabilityRef, WorkflowEffectReach.Internal,
            (_, request) =>
            {
                built.Add(request.CapabilityRef);
                return new WorkflowEffect((_, _) => Task.CompletedTask);
            });
        services.AddSingleton<IWorkflowConfirmationContext>(new FixedConfirmation());
        services.AddSingleton<IWorkflowDefinitionExecutionStore>(new FixedExecutionStore(authored));
        services.AddDeclarativeWorkflowInterpreter();
        return (services.BuildServiceProvider(), built);
    }

    private sealed class FixedExecutionStore(JsonElement authored) : IWorkflowDefinitionExecutionStore
    {
        private WorkflowDefinitionRecord Record => new(
            Tenant.Value, NodeThreeWayMatchWorkflowSeed.DefinitionKey, NodeThreeWayMatchWorkflowSeed.Version,
            WorkflowDefinitionStatus.Published, authored);

        public ValueTask<WorkflowDefinitionRecord?> GetAdmittedCurrentPublishedAsync(
            DefinitionAddress address, CancellationToken ct = default) => ValueTask.FromResult<WorkflowDefinitionRecord?>(Record);

        public ValueTask<WorkflowDefinitionRecord> GetAdmittedAsync(
            DefinitionCoordinates coordinates, CancellationToken ct = default) => ValueTask.FromResult(Record);
    }

    private sealed class FixedConfirmation : IWorkflowConfirmationContext
    {
        public WorkflowProposerIdentity EngineProposer { get; } =
            new(NodeWorkflowConfirmationContext.EngineServicePartyId, IsHuman: false);

        public ValueTask<WorkflowConfirmerIdentity> ResolveConfirmerAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new WorkflowConfirmerIdentity(HumanConfirmer, IsHuman: true));
    }
}
