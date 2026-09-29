using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Foundation.RuleEngine.Environments;

using FormsExpressionEnvironment = Harborline.Foundation.Forms.Engine.FormsExpressionEnvironment;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// T-304 slice 2 (DES-0029 ck-7 plan S7, rules-eng-26): render projection and definition-save admission run on the
/// platform rule runtime, as the submit gate has since slice 1. Each fact pins a place the api copy and the
/// platform runtime disagree, so it fails on the api copy.
/// </summary>
public sealed partial class RuleFailClosedTests
{
    // Not a harborline-jsonlogic/v1 operator. The api copy compiles it and errors only at evaluation; the platform
    // compiler refuses it by rule.compile.invalid_expression, which is how submit refuses it.
    private const string UnknownOperator = """{"no_such_operator":[1]}""";

    // A workflow-bag reference: the compiler keeps the wf. context prefix, and wf is not a Forms-declared variable
    // (forms-ck-13 declares candidate, caller, clock, record_type, field and row). The api copy reads it as a
    // missing value; the platform refuses the evaluation by rule.environment.variable_not_admitted.
    private const string UndeclaredRoot = """{"==":[{"var":"wf.state"},"approved"]}""";

    [Fact]
    public void Definition_save_refuses_an_operator_the_platform_compiler_does_not_know()
    {
        var definition = Definition(HarborlineOverlay.Empty with
        {
            Rules = [RestrictingRule("restrict.unknown-op", UnknownOperator, RuleActionKind.Validate)],
        });

        var refused = Assert.Throws<FormDefinitionValidationException>(() => RuleCompileAdmission.ValidateOrThrow(definition));
        Assert.Equal(FormDefinitionCodes.RulesUncompilable, refused.Code);
        Assert.Contains("restrict.unknown-op", refused.Message, StringComparison.Ordinal);
        Assert.Contains("rule.compile.invalid_expression", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_save_refuses_a_rule_outside_the_forms_environment()
    {
        var definition = Definition(HarborlineOverlay.Empty with
        {
            Rules = [RestrictingRule("restrict.undeclared-root", UndeclaredRoot, RuleActionKind.Validate)],
        });

        var refused = Assert.Throws<FormDefinitionValidationException>(() => RuleCompileAdmission.ValidateOrThrow(definition));
        Assert.Equal(FormDefinitionCodes.RulesUncompilable, refused.Code);
        Assert.Contains("rule.environment.variable_not_admitted", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_save_refuses_a_page_guard_outside_the_forms_environment()
    {
        var definition = Definition(HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["a"] = Field("a") },
            Sections = [Section("s1", "a")],
            Pages = [new FormPage("p1", InternationalizedText.FromInvariant("p1"), ["s1"], VisibleWhen: UndeclaredRoot)],
        });

        var refused = Assert.Throws<FormDefinitionValidationException>(() => RuleCompileAdmission.ValidateOrThrow(definition));
        Assert.Equal(FormDefinitionCodes.RulesGuardUncompilable, refused.Code);
        Assert.Contains("rule.environment.variable_not_admitted", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_save_still_admits_a_rule_inside_the_forms_environment()
    {
        var definition = Definition(HarborlineOverlay.Empty with
        {
            Pages = [new FormPage("p1", InternationalizedText.FromInvariant("p1"), [], VisibleWhen: """{"==":[{"var":"a"},"x"]}""")],
            Rules =
            [
                RestrictingRule("restrict.a", """{"!=":[{"var":"field.a"},"no"]}""", RuleActionKind.Validate),
                RestrictingRule("compute.b", """{"+":[1,2]}""", RuleActionKind.Compute, RuleScope.Field, "b"),
            ],
        });

        RuleCompileAdmission.ValidateOrThrow(definition);
    }

    [Fact]
    public async Task Render_withholds_visibility_targets_when_the_rule_set_is_outside_the_forms_environment()
    {
        // The Visibility rule on 'b' reads an undeclared root, so the platform refuses the whole evaluation. A
        // refused evaluation carries no visibility verdict at all; projecting it as-is would render 'b' with its
        // bound value (a read-time fail-open). It degrades as a compile fault does: b's value is withheld.
        var logger = new CollectingLogger();
        var view = await RenderBoundAsync(
            [RestrictingRule("vis.b", UndeclaredRoot, RuleActionKind.Visibility, RuleScope.Field, "b")],
            logger);

        var fields = view.Sections.Single().Fields;
        Assert.NotNull(fields.Single(f => f.Name == "a").Value);
        var b = fields.Single(f => f.Name == "b");
        Assert.Null(b.Value);
        Assert.False(b.IsReadable);
        var warning = Assert.Single(logger.Messages, m => m.Level == LogLevel.Warning);
        Assert.Contains(FormId.Value, warning.Message, StringComparison.Ordinal);
        Assert.Contains("rule.environment.variable_not_admitted", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_withholds_visibility_targets_when_a_rule_uses_an_unknown_operator()
    {
        var view = await RenderBoundAsync(
        [
            RestrictingRule("vis.b", "true", RuleActionKind.Visibility, RuleScope.Field, "b"),
            RestrictingRule("restrict.unknown-op", UnknownOperator, RuleActionKind.Validate),
        ]);

        var b = view.Sections.Single().Fields.Single(f => f.Name == "b");
        Assert.Null(b.Value);
        Assert.False(b.IsReadable);
    }

    [Fact]
    public void Unadmitted_render_projection_withholds_visibility_targets_instead_of_projecting()
    {
        // A rule set that would evaluate: only the missing admission refuses it.
        var definition = Definition(HarborlineOverlay.Empty with
        {
            Rules = [RestrictingRule("vis.b", "true", RuleActionKind.Visibility, RuleScope.Field, "b")],
        });
        var view = new FormView(FormId, Version, Title: null, Description: null,
        [
            new FormViewSection("s1", InternationalizedText.FromInvariant("s1"),
            [
                ViewField("a"),
                ViewField("b"),
            ]),
        ]);
        var clock = new FixedTimeProvider(SubmittedAt);

        var admitted = FormEngine.ApplyRuleProjection(view, definition, entity: null, clock,
            FormsExpressionEnvironment.Admitted.For(EvaluationPhase.Render), NullLogger.Instance, CancellationToken.None);
        Assert.True(admitted.Sections.Single().Fields.Single(f => f.Name == "b").Rules!.Visible);
        Assert.NotNull(admitted.Sections.Single().Fields.Single(f => f.Name == "b").Value);

        var logger = new CollectingLogger();
        var refused = FormEngine.ApplyRuleProjection(view, definition, entity: null, clock,
            admission: null, logger, CancellationToken.None);
        var b = refused.Sections.Single().Fields.Single(f => f.Name == "b");
        Assert.Null(b.Value);
        Assert.False(b.IsReadable);
        Assert.Null(b.Rules);
        Assert.NotNull(refused.Sections.Single().Fields.Single(f => f.Name == "a").Value);
        var warning = Assert.Single(logger.Messages, m => m.Level == LogLevel.Warning);
        Assert.Contains(BorrowerEnvironmentAdmission.NotAdmitted, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forms_admits_render_and_submission_and_refuses_run()
    {
        Assert.Equal(EvaluationPhase.Render, FormsExpressionEnvironment.Admitted.For(EvaluationPhase.Render).Phase);
        var refused = Assert.Throws<BorrowerEnvironmentException>(() => FormsExpressionEnvironment.Admitted.For(EvaluationPhase.Run));
        Assert.Equal(BorrowerEnvironmentAdmission.PhaseNotAdmitted, refused.Code);
    }

    private static FormViewField ViewField(string name) => new(
        name, InternationalizedText.FromInvariant(name), HelpText: null, ControlHint: null, IsSensitive: false,
        IsReadable: true, Value: System.Text.Json.JsonDocument.Parse($"\"{name}\"").RootElement.Clone());

    private static FormDefinition Definition(HarborlineOverlay overlay) => new(
        Id: FormId,
        Version: Version,
        Status: FormDefinitionStatus.Draft,
        Tenant: Tenant,
        Owner: IdentityRef.System,
        SchemaRef: new SchemaId("t304.s2.schema"),
        Overlay: overlay,
        Lineage: null,
        CreatedAt: SubmittedAt,
        UpdatedAt: SubmittedAt);

    /// <summary>Renders a two-field form bound to {"a":"A","b":"B"} under <paramref name="rules"/>.</summary>
    private static async Task<FormView> RenderBoundAsync(RuleDefinition[] rules, CollectingLogger? logger = null)
    {
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["a"] = Field("a"), ["b"] = Field("b") },
            Sections = [Section("s1", "a", "b")],
            Rules = rules,
        };
        var context = await CreateServicesAsync(overlay, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "a": { "type": "string" }, "b": { "type": "string" } },
              "additionalProperties": false
            }
            """, logger is null ? null : services => services.AddSingleton<ILogger<FormEngine>>(logger));
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, [OperatorRole]);

        using var boundBody = System.Text.Json.JsonDocument.Parse("""{"a":"A","b":"B"}""");
        var entityId = await services.GetRequiredService<IAuthorizedFormEntityWriter>().CreateAsync(
            FormId,
            context.Schema.Id,
            boundBody,
            new Harborline.Api.Foundation.Assets.Entities.CreateOptions(
                Scheme: "forminst",
                Authority: "forms",
                Nonce: Guid.NewGuid().ToString("N"),
                Issuer: Actor,
                Tenant: Tenant,
                ValidFrom: SubmittedAt),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowedDecision(
                Tenant,
                FormId.Value,
                "forms",
                Harborline.Api.Foundation.IdentityAtlas.Permissions.Permission.FormsAuthor,
                Actor.Value,
                SubmittedAt),
            CancellationToken.None);

        return await engine.RenderAsync(FormId, entityId, token, CancellationToken.None);
    }
}
