using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Engine.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Context;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// Ticket 150 — the rule machinery must fail CLOSED at every restricting degrade point
/// (ADR 0038 as corrected: permits may fail open, restricts must refuse). These tests pin:
/// an uninterpretable restricting rule REFUSES the submit naming the rule (was: silent
/// schema-only degrade); a broken Required rule REFUSES rather than relaxing to
/// not-required; a budget abort names the responsible definition; and a render-time
/// compile-fault degrade stays observable (a warning naming the definition).
/// </summary>
public sealed class RuleFailClosedTests
{
    private static readonly DateTimeOffset SubmittedAt = new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-ticket-150");
    private static readonly ActorId Actor = new("ticket-150-actor");
    private static readonly FormDefinitionId FormId = new("ticket150.rules");
    private static readonly SemanticVersion Version = new(1, 0, 0);

    // ── L1143: an uninterpretable rule at validate fails closed and refuses the write ──

    [Fact]
    public async Task Uncompilable_restricting_rule_refuses_submit_naming_the_rule()
    {
        var context = await CreateServicesAsync(RestrictingRule(
            id: "restrict.uninterpretable",
            expression: "this is not JsonLogic",
            action: RuleActionKind.Validate));
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services);

        using var candidate = JsonDocument.Parse("{}");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        // One condition, one code, both doors: the submit gate refuses with the SAME contract
        // constant definition-save admission uses (the compiler's detail code rides Params["code"]).
        var error = Assert.Single(ex.Result.Errors, e => e.Code == FormDefinitionCodes.RulesUncompilable);
        Assert.Contains("restrict.uninterpretable", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Params);
        Assert.Equal("restrict.uninterpretable", error.Params!["rule"]);
        Assert.Equal(RuleEngineCodes.CompileInvalidExpression, error.Params["code"]);
    }

    // ── A broken Required rule refuses rather than relaxing to not-required ──

    [Fact]
    public async Task Broken_required_rule_refuses_submit_instead_of_relaxing()
    {
        // Division by zero errors at EVALUATION (the rule compiles fine): the old
        // FailClosedVisibility mapped that to Required:false — the restriction silently
        // relaxed and the submit succeeded. It must refuse, naming the rule.
        var context = await CreateServicesAsync(RestrictingRule(
            id: "req.broken",
            expression: """{"/":[1,0]}""",
            action: RuleActionKind.Required,
            scope: RuleScope.Field,
            scopeTarget: "name"));
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services);

        using var candidate = JsonDocument.Parse("{}");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        var error = Assert.Single(ex.Result.Errors);
        Assert.Contains("req.broken", error.Message, StringComparison.Ordinal);
        Assert.Equal(RuleEngineCodes.DivByZero, error.Code);
    }

    [Fact]
    public void Broken_required_rule_produces_refusing_validity_not_a_required_relax()
    {
        var rule = new RuleDefinition(
            Id: "req.broken",
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Field,
            ScopeTarget: "name",
            Expression: """{"/":[1,0]}""",
            Action: RuleActionKind.Required);

        var compiled = RuleCompiler.Compile(new[] { rule });
        var result = new FormRuleGraph(compiled, clock: TimeProvider.System).EvaluateInstance(new RuleInstance());

        // The restrict refuses: a failing validity naming the rule, and the save is blocked.
        var refusal = Assert.Single(result.Validations);
        Assert.Equal("req.broken", refusal.RuleId);
        Assert.True(result.IsSaveBlocked);
        // And it does NOT relax: no merged visibility state claiming Required:false for the cell.
        Assert.False(result.Visibility.ContainsKey(CellAddress.Field("name").Key));
    }

    // ── Budget refusals name the responsible definition ──

    [Fact]
    public void Guard_budget_abort_names_the_responsible_rule()
    {
        var rule = new RuleDefinition(
            Id: "guard.hungry",
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Schema,
            ScopeTarget: string.Empty,
            Expression: """{"+":[{"+":[1,2]},{"+":[3,4]}]}""",
            Action: RuleActionKind.Validate);

        var evaluator = new GuardEvaluator(new RuleEngineLimits { StepBudget = 1 }, clock: TimeProvider.System);
        var verdict = evaluator.EvaluateGuard(rule, new Dictionary<string, JsonNode?>());

        Assert.False(verdict.Ok);
        Assert.NotNull(verdict.Error);
        Assert.Equal(RuleEngineCodes.BudgetExceeded, verdict.Error!.Code);
        Assert.Equal("guard.hungry", verdict.Error.Params["rule"]);
    }

    [Fact]
    public void Value_budget_abort_names_the_responsible_rule()
    {
        var rule = new RuleDefinition(
            Id: "compute.hungry",
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Field,
            ScopeTarget: "total",
            Expression: """{"+":[{"+":[1,2]},{"+":[3,4]}]}""",
            Action: RuleActionKind.Compute);

        var evaluator = new GuardEvaluator(new RuleEngineLimits { StepBudget = 1 }, clock: TimeProvider.System);
        var value = evaluator.EvaluateValue(rule, new Dictionary<string, JsonNode?>());

        Assert.Equal(ValueState.Error, value.State);
        Assert.Equal(RuleEngineCodes.BudgetExceeded, value.Error!.Code);
        Assert.Equal("compute.hungry", value.Error.Params["rule"]);
    }

    [Fact]
    public void Whole_graph_budget_abort_names_the_in_flight_rule()
    {
        var rule = new RuleDefinition(
            Id: "validate.hungry",
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Field,
            ScopeTarget: "name",
            Expression: """{"+":[{"+":[1,2]},{"+":[3,4]}]}""",
            Action: RuleActionKind.Validate);

        var compiled = RuleCompiler.Compile(new[] { rule });
        var result = new FormRuleGraph(compiled, new RuleEngineLimits { StepBudget = 1 }, clock: TimeProvider.System)
            .EvaluateInstance(new RuleInstance());

        // The synthetic refusal keeps the RESERVED engine id (an authored rule id there can
        // collide with the submit gate's hidden-page check filter and be skipped — the review
        // regression); attribution rides Params["rule"].
        var refusal = Assert.Single(result.Validations);
        Assert.Equal("rule.engine", refusal.RuleId);
        Assert.Equal(RuleEngineCodes.BudgetExceeded, refusal.Validity!.Error!.Code);
        Assert.Equal("validate.hungry", refusal.Validity.Error.Params["rule"]);
        Assert.True(result.IsSaveBlocked);
    }

    // ── Render is projection (advisory): the degrade may stand, but never silently ──

    [Fact]
    public async Task Render_refuses_a_published_form_whose_schema_is_gone_because_its_fields_cannot_be_rendered_or_validated()
    {
        var schemas = NSubstitute.Substitute.For<ISchemaRegistry>();
        var schema = await new InMemorySchemaRegistry(TimeProvider.System).RegisterAsync(DefaultSchemaJson);
        NSubstitute.SubstituteExtensions.Returns(schemas.RegisterAsync(DefaultSchemaJson),
            ValueTask.FromResult(schema));
        var context = await CreateServicesAsync(HarborlineOverlay.Empty, DefaultSchemaJson,
            configure: services => services.AddSingleton(schemas));
        await using var services = context.Services;
        var token = await IssueReadWriteTokenAsync(services);

        var exception = await Assert.ThrowsAsync<SchemaNotFoundException>(() =>
            services.GetRequiredService<IFormEngine>().RenderAsync(FormId, null, token, CancellationToken.None));

        Assert.Contains(schema.Id.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_compile_fault_degrades_but_logs_the_responsible_definition()
    {
        var logger = new CollectingLogger();
        var context = await CreateServicesAsync(
            RestrictingRule(
                id: "restrict.uninterpretable",
                expression: "this is not JsonLogic",
                action: RuleActionKind.Validate),
            configure: services => services.AddSingleton<ILogger<FormEngine>>(logger));
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services);

        var view = await engine.RenderAsync(FormId, instance: null, token, CancellationToken.None);

        Assert.NotNull(view); // the read degrades to the rule-free view rather than failing…
        var warning = Assert.Single(logger.Messages, m => m.Level == LogLevel.Warning);
        Assert.Contains(FormId.Value, warning.Message, StringComparison.Ordinal); // …but names the definition
        Assert.Contains("restrict.uninterpretable", warning.Message, StringComparison.Ordinal);
    }

    // ── Review fix 1: the whole-graph budget refusal is UN-SKIPPABLE ──────────
    // Regression on 7951dc5: the synthetic refusal's RuleId was set to the in-flight rule id,
    // so when that rule was a check listed ONLY on a hidden page, the gate's step-5 filter
    // dropped the sole blocker — submit accepted schema-only on budget exhaustion.

    [Fact]
    public async Task Whole_graph_budget_refusal_is_not_skippable_as_a_hidden_page_check()
    {
        // One hungry Validate rule, bound as a CHECK on a page whose guard is false (hidden).
        // 60 × cat({"var":"a"}) with a 5000-char value charges ~300k steps > the 250k default
        // budget, so the abort trips while chk.hungry is in flight.
        var hungryExpression = "{\"cat\":[" + string.Join(",", Enumerable.Repeat("""{"var":"a"}""", 60)) + "]}";
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["a"] = Field("a") },
            Sections = new[] { Section("s1", "a") },
            Pages = new[]
            {
                new FormPage(
                    Id: "p1",
                    Title: InternationalizedText.FromInvariant("p1"),
                    Sections: new[] { "s1" },
                    VisibleWhen: "false",
                    Checks: new[] { "chk.hungry" }),
            },
            Rules = new[] { RestrictingRule("chk.hungry", hungryExpression, RuleActionKind.Validate) },
        };
        var context = await CreateServicesAsync(overlay, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "a": { "type": "string" } },
              "additionalProperties": false
            }
            """);
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, new[] { OperatorRole });

        using var candidate = JsonDocument.Parse($$"""{"a":"{{new string('x', 5000)}}"}""");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        // Refused even though the in-flight rule is a hidden-page-only check: the synthetic
        // outcome carries the reserved "rule.engine" id, so the filter cannot drop it; the
        // responsible rule is still named in the params.
        var error = Assert.Single(ex.Result.Errors, e => e.Code == RuleEngineCodes.BudgetExceeded);
        Assert.Equal("chk.hungry", error.Params!["rule"]);
    }

    // ── Review fix 2: a page-guard COMPILE fault refuses, never hides ─────────

    [Fact]
    public async Task Uncompilable_page_guard_refuses_submit_naming_the_page()
    {
        // The guard is parseable JSON (admission's shape check passes) but does not COMPILE:
        // a row. reference is invalid in the Schema-scoped guard wrap. Pre-fix the gate turned
        // this into "page hidden" — fields pruned, checks skipped, write accepted on a compile
        // fault.
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["name"] = Field("name") },
            Sections = new[] { Section("s1", "name") },
            Pages = new[]
            {
                new FormPage(
                    Id: "p1",
                    Title: InternationalizedText.FromInvariant("p1"),
                    Sections: new[] { "s1" },
                    VisibleWhen: """{"var":"row.x"}"""),
            },
        };
        var context = await CreateServicesAsync(overlay, DefaultSchemaJson);
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, new[] { OperatorRole });

        using var candidate = JsonDocument.Parse("""{"name":"ok"}""");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        var error = Assert.Single(ex.Result.Errors, e => e.Code == FormDefinitionCodes.RulesGuardUncompilable);
        Assert.Equal("p1", error.Params!["page"]);
        Assert.Equal("page-guard:p1", error.Params["rule"]);
        Assert.Equal(RuleEngineCodes.CompileBadGrammar, error.Params["code"]);
    }

    // ── Review fix 4: the gate honors the engine's block contract ─────────────

    [Fact]
    public async Task Errored_computed_value_refuses_submit()
    {
        // An errored compute cell (divide-by-zero) is a fail-closed save blocker per
        // RuleEvaluationResult.IsSaveBlocked; the gate previously read only Validations
        // and let it pass.
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["x"] = Field("x"), ["total"] = Field("total") },
            Sections = new[] { Section("s1", "x", "total") },
            Rules = new[]
            {
                RestrictingRule("compute.broken", """{"/":[1,0]}""", RuleActionKind.Compute, RuleScope.Field, "total"),
            },
        };
        var context = await CreateServicesAsync(overlay, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "x": { "type": "number" }, "total": { "type": "number" } },
              "additionalProperties": false
            }
            """);
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, new[] { OperatorRole });

        using var candidate = JsonDocument.Parse("""{"x":1}""");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        var error = Assert.Single(ex.Result.Errors, e => e.Code == RuleEngineCodes.DivByZero);
        Assert.Equal("/total", error.JsonPointer);
    }

    [Fact]
    public async Task Pending_value_at_submit_refuses()
    {
        // A pending value reaching the synchronous integrity tier at save is fail-closed
        // (Decision DE); the gate previously ignored HasPending entirely.
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["x"] = Field("x"), ["total"] = Field("total") },
            Sections = new[] { Section("s1", "x", "total") },
            Rules = new[]
            {
                RestrictingRule("compute.echo", """{"var":"x"}""", RuleActionKind.Compute, RuleScope.Field, "total"),
            },
        };
        var context = await CreateServicesAsync(overlay, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "x": {}, "total": {} },
              "additionalProperties": false
            }
            """);
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, new[] { OperatorRole });

        using var candidate = JsonDocument.Parse("""{"x":{"@pending":true}}""");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        var error = Assert.Single(ex.Result.Errors, e => e.Code == RuleEngineCodes.PendingAtSave);
        Assert.Equal("/total", error.JsonPointer);
        Assert.Equal("field:total", error.Params!["cell"]);
    }

    // ── Review fix 5: an unknown action kind refuses compilation, never permits ──

    [Fact]
    public async Task Unknown_action_kind_refuses_submit()
    {
        // A future/foreign RESTRICTING kind arriving via pack/sync used to lower to
        // OutputType.Visibility — a restrict silently became show/hide. It must refuse.
        var context = await CreateServicesAsync(RestrictingRule(
            id: "restrict.foreign",
            expression: "true",
            action: (RuleActionKind)999,
            scope: RuleScope.Field,
            scopeTarget: "name"));
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services);

        using var candidate = JsonDocument.Parse("{}");
        var ex = await Assert.ThrowsAsync<FormValidationException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        var error = Assert.Single(ex.Result.Errors, e => e.Code == FormDefinitionCodes.RulesUncompilable);
        Assert.Equal("restrict.foreign", error.Params!["rule"]);
        Assert.Equal(RuleEngineCodes.CompileUnknownAction, error.Params["code"]);
    }

    // ── Review fix 6: rule-read-only amnesty must not persist a changed value ──

    [Theory]
    [InlineData("true")] // healthy ReadOnly rule → ReadOnly:true
    [InlineData("""{"/":[1,0]}""")] // erroring ReadOnly rule → fail-closes to ReadOnly:true
    public async Task ReadOnly_rule_discards_a_changed_constraint_violating_value(string readOnlyExpression)
    {
        // Tier-1 constraint errors for a rule-read-only field are stripped (the user could not
        // edit it) — but pre-fix the constraint-VIOLATING submitted value then PERSISTED
        // un-validated. The value is now pruned like a hidden field's: never stored.
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["name"] = Field("name"), ["other"] = Field("other") },
            Sections = new[] { Section("s1", "name", "other") },
            Rules = new[]
            {
                RestrictingRule("ro.name", readOnlyExpression, RuleActionKind.ReadOnly, RuleScope.Field, "name"),
            },
        };
        var context = await CreateServicesAsync(overlay, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "name": { "type": "string", "minLength": 5 }, "other": { "type": "string" } },
              "additionalProperties": false
            }
            """);
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, new[] { OperatorRole });

        // "x" violates minLength 5 — and the field is rule-read-only, so the user cannot have
        // legitimately changed it.
        using var candidate = JsonDocument.Parse("""{"name":"x","other":"kept"}""");
        var receipt = await engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None);

        var entity = await services.GetRequiredService<IEntityStore>().GetAsync(receipt.InstanceId);
        Assert.NotNull(entity);
        Assert.False(entity!.Body.RootElement.TryGetProperty("name", out _)); // discarded, not persisted
        Assert.Equal("kept", entity.Body.RootElement.GetProperty("other").GetString());
    }

    // ── Review fix 14: the render degrade must not DISCLOSE ───────────────────

    [Fact]
    public async Task Render_compile_fault_withholds_visibility_rule_target_values()
    {
        // With the rule set uncompilable, the Visibility rule targeting 'b' cannot run — the
        // degraded view must withhold b's bound value (structure kept) instead of rendering
        // everything (a read-time fail-open).
        var overlay = HarborlineOverlay.Empty with
        {
            Fields = new Dictionary<string, FieldOverlay> { ["a"] = Field("a"), ["b"] = Field("b") },
            Sections = new[] { Section("s1", "a", "b") },
            Rules = new[]
            {
                RestrictingRule("vis.b", "true", RuleActionKind.Visibility, RuleScope.Field, "b"),
                RestrictingRule("restrict.uninterpretable", "this is not JsonLogic", RuleActionKind.Validate),
            },
        };
        var context = await CreateServicesAsync(overlay, """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "a": { "type": "string" }, "b": { "type": "string" } },
              "additionalProperties": false
            }
            """);
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services, new[] { OperatorRole });

        using var boundBody = JsonDocument.Parse("""{"a":"A","b":"B"}""");
        var entityId = await services.GetRequiredService<IAuthorizedFormEntityWriter>().CreateAsync(
            FormId,
            context.Schema.Id,
            boundBody,
            new CreateOptions(
                Scheme: "forminst",
                Authority: "forms",
                Nonce: Guid.NewGuid().ToString("N"),
                Issuer: Actor,
                Tenant: Tenant,
                ValidFrom: SubmittedAt),
            TestAuthorization.AllowedDecision(
                Tenant,
                FormId.Value,
                "forms",
                Permission.FormsAuthor,
                Actor.Value,
                SubmittedAt),
            CancellationToken.None);

        var view = await engine.RenderAsync(FormId, entityId, token, CancellationToken.None);

        var fields = view.Sections.Single().Fields;
        var a = fields.Single(f => f.Name == "a");
        var b = fields.Single(f => f.Name == "b");
        Assert.NotNull(a.Value); // an untargeted field keeps its bound value…
        Assert.Null(b.Value); // …the Visibility-rule target's value is withheld
        Assert.False(b.IsReadable);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static RuleDefinition RestrictingRule(
        string id,
        string expression,
        RuleActionKind action,
        RuleScope scope = RuleScope.Schema,
        string scopeTarget = "") => new(
        // Explicit tenant-scoped envelope: the legacy Id-only ctor stamps TenantId.System,
        // which the definition store's JSON round-trip rejects (reserved sentinel).
        Envelope: new Harborline.Api.Foundation.Definitions.DefinitionEnvelope<string, string, TenantId, string?>(
            id,
            "1.0.0",
            Tenant,
            Harborline.Api.Foundation.Definitions.CascadeLayer.Tenant,
            Provenance: null,
            Array.Empty<Harborline.Api.Foundation.Definitions.DefinitionRequirement>()),
        Tier: RuleTier.JsonLogic,
        Scope: scope,
        ScopeTarget: scopeTarget,
        Expression: expression,
        Action: action);

    private const string DefaultSchemaJson = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": { "name": { "type": "string" } },
          "additionalProperties": false
        }
        """;

    private static Task<TestContext> CreateServicesAsync(
        RuleDefinition rule,
        Action<IServiceCollection>? configure = null)
        => CreateServicesAsync(HarborlineOverlay.Empty with { Rules = new[] { rule } }, DefaultSchemaJson, configure);

    private static async Task<TestContext> CreateServicesAsync(
        HarborlineOverlay overlay,
        string schemaJson,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFieldEncryptor, UnusedFieldEncryptor>();
        services.AddTestAuthorizationGate();
        services.AddTestNodeForms();
        services.AddSingleton<IAuditLog>(new SucceedingAuditLog());
        services.AddFrozenKernelClock(new FixedTimeProvider(SubmittedAt));
        services.AddSingleton(new FormEngineOptions());
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var schema = await provider.GetRequiredService<ISchemaRegistry>().RegisterAsync(schemaJson);
        var definitions = provider.GetRequiredService<IFormDefinitionStore>();
        var definition = new FormDefinition(
            Id: FormId,
            Version: Version,
            Status: FormDefinitionStatus.Draft,
            Tenant: Tenant,
            Owner: IdentityRef.System,
            SchemaRef: schema.Id,
            Overlay: overlay,
            Lineage: null,
            CreatedAt: SubmittedAt,
            UpdatedAt: SubmittedAt);
        // Registered through the STORE, not the publish route: RuleCompileAdmission fences the
        // route, but a definition can arrive by other paths (pack install, sync) — exactly the
        // fail-open corner ticket 150 closes at the submit gate.
        await definitions.RegisterAsync(definition);
        await definitions.PublishAsync(new DefinitionCoordinates(Tenant, FormId.Value, Version.ToString()));
        return new TestContext(provider, schema);
    }

    /// <summary>The role the sectioned test overlays gate read + write on.</summary>
    private const string OperatorRole = "operator";

    private static FieldOverlay Field(string name) => new(InternationalizedText.FromInvariant(name));

    private static FormSection Section(string id, params string[] fields) => new(
        Id: id,
        Title: InternationalizedText.FromInvariant(id),
        Fields: fields,
        Access: new SectionAccess(
            ReadRoles: new[] { OperatorRole },
            WriteRoles: new[] { OperatorRole }));

    private static async Task<CapabilityToken> IssueReadWriteTokenAsync(
        IServiceProvider services, IReadOnlyList<string>? roles = null)
    {
        var bearer = await services.GetRequiredService<IFormCapabilityIssuer>().IssueAsync(
            Tenant,
            Actor,
            roles ?? Array.Empty<string>(),
            new[] { FormCapabilityAction.Read, FormCapabilityAction.Write },
            SubmittedAt.AddHours(1));
        return await services.GetRequiredService<IFormCapabilityVerifier>().VerifyAsync(bearer, SubmittedAt);
    }

    private sealed record TestContext(ServiceProvider Services, Schema Schema);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnusedFieldEncryptor : IFieldEncryptor
    {
        public Task<EncryptedField> EncryptAsync(ReadOnlyMemory<byte> plaintext, TenantId tenant, CancellationToken ct) =>
            throw new InvalidOperationException("A refused write must never reach field encryption.");
    }

    private sealed class SucceedingAuditLog : IAuditLog
    {
        public Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default) =>
            Task.FromResult(new AuditId(1));

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class CollectingLogger : ILogger<FormEngine>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
