using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-978 (api half): the Rules stage of a Records write. The bound property form's schema-scoped Validate
/// rules are the record type's rules (owner ruling 2026-09-28); the keyed record validator runs them after
/// schema validation and before commit, at the act's admitted instant, and fails closed (DES-0018
/// rules-eng-13/14, L272, L274).
/// </summary>
public sealed class RecordWriteRulesStageTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000009781");
    private const string Type = "t978.lease";
    private const string RentPositive = """{">":[{"var":"rent"},0]}""";

    [Fact(DisplayName = "T-978 L272: a write a bound rule refuses is refused before commit with the rule named, and nothing persists")]
    public async Task Violating_write_is_refused_before_commit_with_the_rule_named()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive));

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => host.CreateAsync(rent: 0).AsTask());

        Assert.Equal(RecordWriteRulesValidator.RuleRefused, refusal.ReasonCode);
        Assert.Contains("rent-positive", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await host.RecordsAsync());
        Assert.Empty(await host.RegistryRowsAsync());
    }

    [Fact(DisplayName = "T-978: a write that satisfies every bound rule commits")]
    public async Task Satisfying_write_commits()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive));

        await host.CreateAsync(rent: 1200);

        var record = Assert.Single(await host.RecordsAsync());
        Assert.Equal(1200, record.Body.RootElement.GetProperty("rent").GetInt32());
    }

    [Fact(DisplayName = "T-978 L272: an update is judged on the state it commits, against its record's bound rules")]
    public async Task Violating_update_is_refused_and_the_record_keeps_its_version()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive));
        await host.CreateAsync(rent: 1200);
        var record = Assert.Single(await host.RecordsAsync());
        using var zero = JsonDocument.Parse("""{"rent":0,"unit":"4B"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => host.Writer.UpdateAsync(
            record.Id, zero, new UpdateOptions(new ActorId("t978")), host.Authority()).AsTask());

        Assert.Equal(RecordWriteRulesValidator.RuleRefused, refusal.ReasonCode);
        var stored = Assert.Single(await host.RecordsAsync());
        Assert.Equal(record.CurrentVersion, stored.CurrentVersion);
    }

    [Fact(DisplayName = "T-978 L274: a bound rule the engine cannot interpret during the write refuses it")]
    public async Task Rule_exceeding_its_step_budget_refuses_the_write()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive), stepBudget: 1);

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => host.CreateAsync(rent: 1200).AsTask());

        Assert.Equal(RecordWriteRulesValidator.RuleRefused, refusal.ReasonCode);
        Assert.Contains("rent-positive", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await host.RecordsAsync());
    }

    [Fact(DisplayName = "T-978 L274: a bound rule that does not compile refuses the write rather than being skipped")]
    public async Task Rule_that_does_not_compile_refuses_the_write()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", """{"no-such-operator":[{"var":"rent"}]}"""));

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => host.CreateAsync(rent: 1200).AsTask());

        Assert.Equal(RecordWriteRulesValidator.RulesUnavailable, refusal.ReasonCode);
        Assert.Empty(await host.RecordsAsync());
    }

    [Fact(DisplayName = "T-978: a record whose bound form is gone is refused, never written without its rules")]
    public async Task Missing_bound_form_refuses_the_write()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive));
        var binding = new EntityBinding(host.Schema, "t978.no-such-form", "1.0.0", "1", ["en"], host.Now);
        using var body = JsonDocument.Parse("""{"rent":1200,"unit":"4B"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => host.Writer.CreateAsync(
            host.Schema, body,
            new CreateOptions("record", "t978", "orphan", new ActorId("t978"), Tenant, Binding: binding),
            host.Authority()).AsTask());

        Assert.Equal(RecordWriteRulesValidator.RulesUnavailable, refusal.ReasonCode);
        Assert.Empty(await host.RecordsAsync());
    }

    [Fact(DisplayName = "T-978: a hierarchy split mints records through the same Rules stage")]
    public async Task Hierarchy_split_target_is_refused_by_its_bound_rule()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive));
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, TimeProvider.System);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities,
            new InMemoryHierarchyService(storage),
            new HierarchyAuthorizedAuditWriter(new Harborline.Api.Foundation.Assets.Audit.InMemoryAuditLog(storage)),
            TestAuthorization.AllowGate(),
            TimeProvider.System,
            host.Validator);
        using var oldBody = JsonDocument.Parse("""{"rent":1200,"unit":"4B"}""");
        using var newBody = JsonDocument.Parse("""{"rent":0,"unit":"4B"}""");
        var oldId = await entities.CreateAsync(host.Schema, oldBody, host.Options("split-old"));

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => coordinator.SplitAsync(
            oldId,
            [new SplitTarget(host.Schema, newBody, host.Options("split-new"))],
            new Dictionary<EntityId, EntityId>(),
            "split",
            new ActorId("t978"),
            Tenant,
            host.Now));

        Assert.Equal(RecordWriteRulesValidator.RuleRefused, refusal.ReasonCode);
        Assert.NotNull(await entities.GetAsync(oldId));
        Assert.Null(await entities.GetAsync(new EntityId("record", "t978", "split-new")));
    }

    [Fact(DisplayName = "T-978: a hierarchy merge mints its target record through the same Rules stage")]
    public async Task Hierarchy_merge_target_is_refused_by_its_bound_rule()
    {
        await using var host = await Host.OpenAsync(Rule("rent-positive", RentPositive));
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, TimeProvider.System);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities,
            new InMemoryHierarchyService(storage),
            new HierarchyAuthorizedAuditWriter(new Harborline.Api.Foundation.Assets.Audit.InMemoryAuditLog(storage)),
            TestAuthorization.AllowGate(),
            TimeProvider.System,
            host.Validator);
        using var oldBody = JsonDocument.Parse("""{"rent":1200,"unit":"4B"}""");
        using var newBody = JsonDocument.Parse("""{"rent":0,"unit":"4B"}""");
        var oldId = await entities.CreateAsync(host.Schema, oldBody, host.Options("merge-old"));

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => coordinator.MergeAsync(
            [oldId], host.Schema, newBody, host.Options("merge-new"), "merge", new ActorId("t978"), Tenant, host.Now));

        Assert.Equal(RecordWriteRulesValidator.RuleRefused, refusal.ReasonCode);
        Assert.NotNull(await entities.GetAsync(oldId));
        Assert.Null(await entities.GetAsync(new EntityId("record", "t978", "merge-new")));
    }

    private static RuleDefinition Rule(string id, string expression) =>
        new(new DefinitionEnvelope<string, string, TenantId, string?>(id, "1.0.0", Tenant, CascadeLayer.Tenant, null, [], Contract: null),
            RuleTier.JsonLogic, RuleScope.Schema, string.Empty, expression, RuleActionKind.Validate);

    private sealed class Host(WebApplication app, SchemaId schema, FormDefinition form) : IAsyncDisposable
    {
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal SchemaId Schema => schema;
        internal IEntityValidator Validator =>
            app.Services.GetRequiredKeyedService<IEntityValidator>(CompiledSchemaEntityValidator.RecordWriteKey);
        internal NodeEntityWriter Writer => app.Services.GetRequiredService<NodeEntityWriter>();

        internal AuthorizationWriteContext Authority() => new(new ActorId("t978"), Tenant, AdmittedInstant.FromRecordedAct(Now));

        internal CreateOptions Options(string localPart) => new(
            "record", "t978", localPart, new ActorId("t978"), Tenant, Now, ExplicitLocalPart: localPart,
            Binding: new EntityBinding(schema, form.Id.Value, form.Version.ToString(), "1", ["en"], Now));

        internal async ValueTask CreateAsync(int rent)
        {
            using var values = JsonDocument.Parse($$"""{"rent":{{rent}},"unit":"4B"}""");
            await app.Services.GetRequiredService<PackBoundRegistryRecordWriter>().CreateAsync(
                new EntityTypeId(Type), new FormBindingRef(form.Id, form.Version), "Lease 4B", null, values,
                new ActorId("t978"), Authority());
        }

        internal async Task<List<Entity>> RecordsAsync()
        {
            var records = new List<Entity>();
            await foreach (var entity in app.Services.GetRequiredService<IEntityStore>().QueryAsync(new EntityQuery(Tenant: Tenant)))
                if (entity.Id.Scheme == "record") records.Add(entity);
            return records;
        }

        internal async Task<IReadOnlyList<RegistryEntity>> RegistryRowsAsync() =>
            await app.Services.GetRequiredService<IRegistryEntityRepository>().ListByTypeAsync(Tenant, new EntityTypeId(Type));

        internal static async Task<Host> OpenAsync(RuleDefinition rule, int? stepBudget = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            TestDesktopOperator.AddTestDesktopOperator(builder.Services);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            var audit = new InMemoryAuditTrail();
            builder.Services.AddSingleton<IAuditTrail>(audit);
            builder.Services.AddSingleton<IAuthorizedAuditTrail>(audit);
            builder.Services.AddTestAuthorizationGate();
            if (stepBudget is { } budget)
                builder.Services.AddSingleton(Harborline.Foundation.RuleEngine.RuleEngineLimits.Default with { StepBudget = budget });
            // The record coordinator takes the KEYED record validator, the way the shipped composition does.
            builder.Services.AddTestNodeForms(configureWriters: (services, mutations, _) =>
                services.AddSingleton(sp => new NodeEntityWriter(null!, mutations(sp),
                    sp.GetRequiredKeyedService<IEntityValidator>(CompiledSchemaEntityValidator.RecordWriteKey),
                    TestAuthorization.AllowGate())));
            builder.Services.AddNodeAssetRegistry();
            builder.Services.AddSingleton(sp => new PackBoundRegistryRecordWriter(
                sp.GetRequiredService<IFormDefinitionStore>(), sp.GetRequiredService<NodeEntityWriter>(),
                sp.GetRequiredService<IEntityStore>(), sp.GetRequiredService<IRegistryEntityRepository>()));
            var app = builder.Build();

            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var schema = await app.Services.GetRequiredService<ISchemaRegistry>().RegisterAsync(
                """{"type":"object","properties":{"rent":{"type":"integer"},"unit":{"type":"string"}},"required":["rent"]}""");
            var form = new FormDefinition(new FormDefinitionId("t978.lease.capture"), new SemanticVersion(1, 0, 0),
                FormDefinitionStatus.Draft, Tenant, IdentityRef.System, schema.Id,
                new HarborlineOverlay(new Dictionary<string, FieldOverlay>(), [], [rule],
                    InternationalizedText.FromInvariant("Lease")), null, now, now);
            var forms = app.Services.GetRequiredService<IFormDefinitionStore>();
            await forms.RegisterAsync(form);
            await forms.PublishAsync(new DefinitionCoordinates(Tenant, form.Id.Value, form.Version.ToString()));
            app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(new EntityTypeId(Type),
                new EntityTypeDescriptor("Lease", EntityTrait.Movable,
                    PropertyFormBinding: new FormBindingRef(form.Id, form.Version)), CascadeLayer.Pack));
            return new Host(app, schema.Id, form);
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
