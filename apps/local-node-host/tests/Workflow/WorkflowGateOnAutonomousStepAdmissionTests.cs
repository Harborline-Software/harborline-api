using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// T-1176/api-authoring-refusal, under the owner's Q1 ruling (2026-11-07, option A): a role or standing gate where no
/// human acts is refused when the definition is admitted, because the engine principal holds no role or standing
/// (ADR-0051), so such a gate could never pass. "No human acts" is the CP fence's own test: a transition whose trigger
/// is not HumanAction, or an action that fires without a HumanAction gating its firing. The base definition is the
/// shipped three-way-match seed, validated against the canonical capability registry.
/// </summary>
public sealed class WorkflowGateOnAutonomousStepAdmissionTests
{
    private const string Tenant = "local";
    private const string Code = "workflow.admission.gate_on_autonomous_step";

    [Fact(DisplayName = "T-1176 Q1: the shipped three-way-match seed admits with no gate refusal")]
    public void The_seed_admits()
    {
        Assert.True(Validate(Seed()).IsValid);
    }

    [Fact(DisplayName = "T-1176 Q1: a role gate on an autonomous (Event) transition is refused at admission")]
    public void A_role_gate_on_an_autonomous_transition_is_refused()
    {
        var authored = Seed();
        Element(authored, "transitions", "t-match")["requiredRoles"] = new JsonArray("sys.platform-roles/auditor");

        var result = Validate(authored);

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations, v => v.Code == Code);
        Assert.Equal("t-match", violation.Locator);
    }

    [Fact(DisplayName = "T-1176 Q1: a standing gate on an action bound to an autonomous transition is refused at admission")]
    public void A_standing_gate_on_an_action_on_an_autonomous_transition_is_refused()
    {
        var authored = Seed();
        authored["actions"]!.AsArray().Add(AutonomousNotify(new JsonObject { ["transition"] = "t-match" }));

        var result = Validate(authored);

        var violation = Assert.Single(result.Violations, v => v.Code == Code);
        Assert.Equal("a-notify", violation.Locator);
        // AP capability: no other rule fires, so the refusal is this rule's alone.
        Assert.Single(result.Violations);
    }

    [Fact(DisplayName = "T-1176 Q1: a gated action firing on entry to a state entered autonomously is refused at admission")]
    public void A_gated_on_enter_action_reached_autonomously_is_refused()
    {
        var authored = Seed();
        // 'review' is entered by t-match, an Event transition, so its on-enter action fires with no human gating it.
        authored["actions"]!.AsArray().Add(AutonomousNotify(new JsonObject { ["state"] = NodeThreeWayMatchWorkflowSeed.ReviewState }));

        var violation = Assert.Single(Validate(authored).Violations);
        Assert.Equal(Code, violation.Code);
        Assert.Equal("a-notify", violation.Locator);
    }

    [Fact(DisplayName = "T-1176 Q1: role and standing gates on a HumanAction transition and the action it fires are admitted")]
    public void Gates_where_a_person_acts_are_admitted()
    {
        var authored = Seed();
        Element(authored, "transitions", "t-approve")["requiredRoles"] = new JsonArray("sys.platform-roles/auditor");
        Element(authored, "actions", "a-post-bill")["requiredStandings"] = new JsonArray("assigned-approver");

        Assert.True(Validate(authored).IsValid);
    }

    private static JsonObject Seed()
        => JsonNode.Parse(NodeThreeWayMatchWorkflowSeed.Authored(Tenant).GetRawText())!.AsObject();

    private static JsonObject Element(JsonObject authored, string collection, string id)
        => authored[collection]!.AsArray().Single(e => (string?)e!["id"] == id)!.AsObject();

    private static JsonObject AutonomousNotify(JsonObject on) => new()
    {
        ["id"] = "a-notify",
        ["on"] = on,
        ["kind"] = "Notify",
        ["capabilityRef"] = "notify.email",
        ["classification"] = "AP",
        ["requiredStandings"] = new JsonArray("assigned-approver"),
    };

    private static WorkflowAdmissionResult Validate(JsonObject authored)
    {
        var element = JsonSerializer.SerializeToElement(authored);
        var model = WorkflowDefinitionWireMapper.ToModel(
            element, Tenant, NodeThreeWayMatchWorkflowSeed.DefinitionKey, NodeThreeWayMatchWorkflowSeed.Version);
        return new WorkflowAdmissionValidator().Validate(model);
    }
}
