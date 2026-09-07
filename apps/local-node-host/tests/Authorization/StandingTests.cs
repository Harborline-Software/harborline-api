using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class StandingTests
{
    private static readonly DateTimeOffset ActInstant =
        new(2026, 9, 2, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void StandingRuleDefinition_RoundTripsItsDeclaredPredicateAndInputs()
    {
        var definition = HandlerRule();

        var json = JsonSerializer.Serialize(definition);
        var roundTripped = JsonSerializer.Deserialize<StandingRuleDefinition>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(definition.RuleId, roundTripped.RuleId);
        Assert.Equal(definition.RuleVersion, roundTripped.RuleVersion);
        Assert.Equal(definition.Standing, roundTripped.Standing);
        Assert.Equal(definition.RecordType, roundTripped.RecordType);
        Assert.Equal(definition.Predicate.Expression, roundTripped.Predicate.Expression);
        Assert.Equal(["handler_id"], roundTripped!.InputFields);
        Assert.Equal(RuleTier.JsonLogic, roundTripped.Predicate.Tier);
    }

    [Fact]
    public void StandingEvaluator_ComputesFromRecordFieldsAndEmitsRuleEvidence()
    {
        var evaluator = new StandingEvaluator();
        var record = new StandingRecord(
            "matter",
            new Dictionary<string, JsonNode?> { ["handler_id"] = "person-7" });

        var result = evaluator.Evaluate(record, ActInstant, [HandlerRule()]);

        Assert.Equal([new StandingReference("handler")], result.Standings);
        var evidence = Assert.Single(result.Evidence);
        Assert.True(evidence.CarriesStanding);
        Assert.Equal("matter.handler", evidence.RuleId);
        Assert.Equal("1.0.0", evidence.RuleVersion);
        Assert.Equal(ActInstant, evidence.Instant);
        Assert.Equal("person-7", evidence.InputFieldValues["handler_id"]!.GetValue<string>());

        var repeated = evaluator.Evaluate(record, ActInstant, [HandlerRule()]);
        Assert.Equal(result.Standings, repeated.Standings);
        Assert.Equal(
            JsonSerializer.Serialize(result.Evidence),
            JsonSerializer.Serialize(repeated.Evidence));
    }

    [Fact]
    public void StandingEvaluator_RecordAndRuleChangesAlterTheNextVerdict()
    {
        var evaluator = new StandingEvaluator();
        var matching = new StandingRecord(
            "matter",
            new Dictionary<string, JsonNode?> { ["handler_id"] = "person-7" });
        var changedRecord = matching with
        {
            FieldValues = new Dictionary<string, JsonNode?> { ["handler_id"] = "person-8" },
        };
        var changedRule = HandlerRule("person-8", version: "1.1.0");

        Assert.Contains(new StandingReference("handler"),
            evaluator.Evaluate(matching, ActInstant, [HandlerRule()]).Standings);
        Assert.Empty(evaluator.Evaluate(changedRecord, ActInstant, [HandlerRule()]).Standings);
        var afterRuleChange = evaluator.Evaluate(changedRecord, ActInstant, [changedRule]);
        Assert.Contains(new StandingReference("handler"), afterRuleChange.Standings);
        Assert.Equal("1.1.0", Assert.Single(afterRuleChange.Evidence).RuleVersion);
    }

    [Fact]
    public async Task StandingCatalogue_ListsEveryAndOnlySchemaCarryingTheRuleField()
    {
        var schemas = new InMemorySchemaRegistry(TimeProvider.System);
        var matter = await schemas.RegisterAsync(SchemaWithProperties("handler_id", "title"));
        var journal = await schemas.RegisterAsync(SchemaWithProperties("posted_at"));
        var tenancy = await schemas.RegisterAsync(SchemaWithProperties("tenant_id", "handler_id"));
        var catalogue = new StandingCatalogue(schemas);

        var carrying = await catalogue.ListCarryingRecordTypesAsync(HandlerRule(), "handler_id");

        Assert.Equal(
            new[] { matter.Id, tenancy.Id }.OrderBy(id => id.Value, StringComparer.Ordinal),
            carrying);
        Assert.DoesNotContain(journal.Id, carrying);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await catalogue.ListCarryingRecordTypesAsync(HandlerRule(), "not_declared"));
    }

    [Fact]
    public void StandingReference_IsStructurallyOutsideGrantAndBindingTypes()
    {
        var standingPayload = JsonSerializer.Serialize(new StandingReference("handler"));
        Assert.ThrowsAny<ArgumentException>(() =>
            JsonSerializer.Deserialize<RoleReference>(standingPayload));
        Assert.False(typeof(RoleReference).IsAssignableFrom(typeof(StandingReference)));
        Assert.Equal(
            typeof(RoleReference),
            typeof(AccessGrant).GetProperty(nameof(AccessGrant.Role))!.PropertyType);
        Assert.Equal(
            typeof(RoleReference),
            typeof(GrantIssuanceRequest).GetProperty(nameof(GrantIssuanceRequest.Role))!.PropertyType);
        Assert.Equal(
            typeof(RoleBindingSet),
            typeof(CapabilityRoleBindingRevision)
                .GetProperty(nameof(CapabilityRoleBindingRevision.SelectedRoles))!.PropertyType);
        Assert.DoesNotContain(
            typeof(AccessGrant).Assembly.GetTypes(),
            type => type.Name.Contains("Delegation", StringComparison.Ordinal)
                && type.GetProperties().Any(property => property.PropertyType == typeof(StandingReference)));
    }

    [Fact]
    public async Task PackageInstallation_AddsDefinitionRowWithoutAddingGateDiscriminator()
    {
        var before = GateDiscriminatorSet();
        var definition = HandlerRule();
        var canonicalJson = JsonSerializer.Serialize(definition);
        var tenant = new TenantId("aaaaaaaa-0000-0000-0000-000000000219");
        var installStore = new InMemoryPackInstallStore();
        var seed = new PackSeedItem(
            definition.RuleId,
            PackContentKind.StandingRuleDefinition,
            definition.RuleVersion,
            canonicalJson,
            Cid.FromBytes(Encoding.UTF8.GetBytes(canonicalJson)));
        var pack = new InstalledPack(
            "standing-rules.test",
            "1.0.0",
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            [seed],
            new Dictionary<string, int>(),
            ActInstant,
            PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]),
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());
        installStore.Commit(new PackInstallTransaction(
            tenant,
            pack,
            new PackInstallWatermark(pack.PackKey, pack.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        installStore.Activate(tenant, pack.PackKey, pack.Version);

        var definitions = new InMemoryStandingRuleDefinitionStore();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        var projector = new PackSeedProjector(
            installStore,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            standingRules: definitions, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);
        var installed = await definitions.GetAsync(definition.RuleId, definition.RuleVersion);

        Assert.Empty(summary.Refusals);
        Assert.NotNull(installed);
        Assert.Equal(definition.Standing, installed.Standing);
        Assert.Equal(before, GateDiscriminatorSet());
        Assert.DoesNotContain(GateDiscriminatorSet(), name => name.Contains("Standing", StringComparison.Ordinal));
    }

    private static StandingRuleDefinition HandlerRule(
        string person = "person-7",
        string version = "1.0.0") => new(
            RuleId: "matter.handler",
            RuleVersion: version,
            Standing: new StandingReference("handler"),
            RecordType: "matter",
            InputFields: ["handler_id"],
            Predicate: new RuleDefinition(
                new DefinitionEnvelope<string, string, TenantId, string?>(
                    "matter.handler",
                    version,
                    new TenantId("bbbbbbbb-0000-0000-0000-000000000219"),
                    CascadeLayer.Pack,
                    Provenance: null,
                    Array.Empty<DefinitionRequirement>()),
                RuleTier.JsonLogic,
                RuleScope.Schema,
                string.Empty,
                $$"""{"==":[{"var":"handler_id"},"{{person}}"]}""",
                RuleActionKind.Validate));

    private static string SchemaWithProperties(params string[] properties)
    {
        var propertyNodes = new JsonObject();
        foreach (var property in properties)
            propertyNodes[property] = new JsonObject { ["type"] = "string" };
        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = propertyNodes,
        }.ToJsonString();
    }

    private static string[] GateDiscriminatorSet() =>
        typeof(PackSeedProjector).Assembly.GetTypes()
            .Where(type => type.IsEnum
                && (type.Name.EndsWith("GateKind", StringComparison.Ordinal)
                    || type.Name.EndsWith("GateType", StringComparison.Ordinal)))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
