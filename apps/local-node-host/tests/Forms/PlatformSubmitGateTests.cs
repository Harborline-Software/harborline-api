using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Schema;
using Harborline.Foundation.RuleEngine.Environments;

using FormsExpressionEnvironment = Harborline.Foundation.Forms.Engine.FormsExpressionEnvironment;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// T-304 (DES-0029 ck-7 plan S7, rules-eng-26): the api submit gate evaluates on the platform rule runtime
/// under Forms' admitted environment, and an evaluation without admission refuses the write by its
/// <c>rule.environment.*</c> code instead of evaluating or hiding anything.
/// </summary>
public sealed class PlatformSubmitGateTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvaluationAdmission Admitted = FormsExpressionEnvironment.Admitted.For(EvaluationPhase.Submission);

    private static readonly RuleDefinition NameIsOk = new(
        Id: "restrict.name",
        Tier: RuleTier.JsonLogic,
        Scope: RuleScope.Field,
        ScopeTarget: "name",
        Expression: """{"==":[{"var":"name"},"ok"]}""",
        Action: RuleActionKind.Validate);

    [Fact]
    public void Admitted_submission_evaluates_the_rule_graph()
    {
        var form = Form(HarborlineOverlay.Empty with { Rules = [NameIsOk] });

        Assert.Empty(Gate(form, """{"name":"ok"}""", Admitted).RuleErrors);
        var refusal = Assert.Single(Gate(form, """{"name":"no"}""", Admitted).RuleErrors);
        Assert.Equal("/name", refusal.JsonPointer);
        Assert.Contains("restrict.name", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unadmitted_rule_graph_refuses_with_the_environment_code()
    {
        var form = Form(HarborlineOverlay.Empty with { Rules = [NameIsOk] });

        // The candidate would pass: only the missing admission refuses it.
        var refusal = Assert.Single(Gate(form, """{"name":"ok"}""", admission: null).RuleErrors);
        Assert.Equal(BorrowerEnvironmentAdmission.NotAdmitted, refusal.Code);
    }

    [Fact]
    public void Rule_outside_the_admitted_operations_refuses_with_the_environment_code()
    {
        var narrow = BorrowerEnvironmentAdmission.Admit(FormsExpressionEnvironment.Declaration with
        {
            Borrower = "t304-narrow",
            Operations = ["var"],
        }).For(EvaluationPhase.Submission);
        var form = Form(HarborlineOverlay.Empty with { Rules = [NameIsOk] });

        var refusal = Assert.Single(Gate(form, """{"name":"ok"}""", narrow).RuleErrors);
        Assert.Equal(BorrowerEnvironmentAdmission.OperationNotAdmitted, refusal.Code);
    }

    [Fact]
    public void Unadmitted_page_guard_refuses_the_write_instead_of_hiding_the_page()
    {
        // A guard that would show the page. Hiding it on a refusal would prune "a" and skip its checks.
        var form = Form(HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["a"] = new(InternationalizedText.FromInvariant("a")) },
            Sections = [new FormSection("s1", InternationalizedText.FromInvariant("s1"), ["a"], new SectionAccess([], []))],
            Pages = [new FormPage("p1", InternationalizedText.FromInvariant("p1"), ["s1"], VisibleWhen: "true")],
        });

        Assert.Empty(Gate(form, """{"a":"x"}""", Admitted).RuleErrors);
        var gate = Gate(form, """{"a":"x"}""", admission: null);
        var refusal = Assert.Single(gate.RuleErrors);
        Assert.Equal(FormDefinitionCodes.RulesGuardUncompilable, refusal.Code);
        Assert.Equal(BorrowerEnvironmentAdmission.NotAdmitted, refusal.Params!["code"]);
        Assert.Empty(gate.PrunedKeys);
    }

    [Fact]
    public void Submission_is_a_phase_the_forms_declaration_admits_and_run_is_not()
    {
        Assert.Equal(EvaluationPhase.Submission, Admitted.Phase);
        var refused = Assert.Throws<BorrowerEnvironmentException>(() => FormsExpressionEnvironment.Admitted.For(EvaluationPhase.Run));
        Assert.Equal(BorrowerEnvironmentAdmission.PhaseNotAdmitted, refused.Code);
    }

    private static SubmitValidationGate.GateResult Gate(FormDefinition form, string candidate, EvaluationAdmission? admission)
    {
        using var document = JsonDocument.Parse(candidate);
        return SubmitValidationGate.Evaluate(form, document, new FakeClock(), admission, CancellationToken.None);
    }

    private static FormDefinition Form(HarborlineOverlay overlay) => new(
        Id: new FormDefinitionId("t304.gate"),
        Version: new SemanticVersion(1, 0, 0),
        Status: FormDefinitionStatus.Published,
        Tenant: new TenantId("tenant-t304"),
        Owner: IdentityRef.System,
        SchemaRef: new SchemaId("t304.schema"),
        Overlay: overlay,
        Lineage: null,
        CreatedAt: At,
        UpdatedAt: At);

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }
}
