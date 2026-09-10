using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 151 (ledger row L1418) — the authority-side record validator. The node's registered
/// <see cref="IEntityValidator"/> is a real schema validator compiled from the ONE schema registry,
/// not the always-accepting <see cref="NullEntityValidator"/>: a body that violates the activated
/// Records type schema is refused by name, with RFC 6901 pointers, BEFORE persistence, and the
/// authorization gate still decides first.
/// </summary>
public sealed class CompiledEntityValidatorTests(ITestOutputHelper output)
{
    private const string WorkOrderV1 = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["title", "quantity"],
          "properties": {
            "title": { "type": "string", "minLength": 1 },
            "quantity": { "type": "integer" },
            "priority": { "enum": ["low", "high"] }
          }
        }
        """;

    // v2 adds a required property: a body valid under v1 is invalid under v2.
    private const string WorkOrderV2 = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["title", "quantity", "site"],
          "properties": {
            "title": { "type": "string", "minLength": 1 },
            "quantity": { "type": "integer" },
            "site": { "type": "string" },
            "priority": { "enum": ["low", "high"] }
          }
        }
        """;

    private const string ValidBody = """{"title":"Replace filter","quantity":2,"priority":"high"}""";

    [Fact(DisplayName = "151 (c): an unresolvable schema id is a named refusal, nothing persists")]
    public async Task UnknownSchema_IsRefusedByName()
    {
        var fixture = Fixture();

        using var body = JsonDocument.Parse(ValidBody);
        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(new SchemaId("never-activated"), body, Options(fixture), Authority));

        Assert.Equal(CompiledEntityValidator.SchemaUnknownReason, error.ReasonCode);
        Assert.Contains("never-activated", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.ReceivedCalls());
    }

    [Fact(DisplayName = "151 (a): a missing required property is refused with keyword and pointer")]
    public async Task MissingRequiredProperty_IsRefusedWithPointer()
    {
        var fixture = await ActivatedAsync(WorkOrderV1);

        using var body = JsonDocument.Parse("""{"title":"No quantity"}""");
        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(fixture.RecordType, body, Options(fixture), Authority));

        Assert.Equal(CompiledEntityValidator.BodyInvalidReason, error.ReasonCode);
        Assert.Contains("required", error.Message, StringComparison.Ordinal);
        Assert.Contains(string.Empty, error.Pointers);
        Assert.DoesNotContain("No quantity", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.ReceivedCalls());
    }

    [Fact(DisplayName = "151 (a): a wrong-typed property is refused with its RFC 6901 pointer")]
    public async Task WrongType_IsRefusedWithPointer()
    {
        var fixture = await ActivatedAsync(WorkOrderV1);

        using var body = JsonDocument.Parse("""{"title":"Filter","quantity":"two"}""");
        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(fixture.RecordType, body, Options(fixture), Authority));

        Assert.Equal(CompiledEntityValidator.BodyInvalidReason, error.ReasonCode);
        Assert.Contains("/quantity", error.Pointers);
        Assert.Contains("type", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.ReceivedCalls());
    }

    [Fact(DisplayName = "151 (b): a conforming body persists and reads back")]
    public async Task ValidBody_PersistsAndReadsBack()
    {
        var fixture = await ActivatedAsync(WorkOrderV1, realStore: true);

        using var body = JsonDocument.Parse(ValidBody);
        var id = await fixture.Writer.CreateAsync(fixture.RecordType, body, Options(fixture), Authority);

        var stored = await fixture.RealStore!.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(fixture.RecordType, stored!.Schema);
        Assert.Equal("Replace filter", stored.Body.RootElement.GetProperty("title").GetString());
    }

    [Fact(DisplayName = "151 (d): an unauthorized caller with an invalid body is refused by the GATE")]
    public async Task Unauthorized_InvalidBody_IsRefusedByTheGate()
    {
        var fixture = await ActivatedAsync(WorkOrderV1, allow: false);

        using var body = JsonDocument.Parse("""{"nonsense":true}""");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await fixture.Writer.CreateAsync(fixture.RecordType, body, Options(fixture), Authority));

        Assert.Empty(fixture.Store.ReceivedCalls());
    }

    [Fact(DisplayName = "151 (e): the headless coordinator path runs the same validator")]
    public async Task HeadlessCoordinatorPath_IsValidated()
    {
        var fixture = await ActivatedAsync(WorkOrderV1);
        IEntityWriteCoordinator headless = fixture.Writer;

        using var body = JsonDocument.Parse("""{"title":"Filter","quantity":"two"}""");
        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await headless.CreateAsync(
                fixture.RecordType, body, Options(fixture), Authority.Principal, Authority.Tenant, Authority.At));

        Assert.Equal(CompiledEntityValidator.BodyInvalidReason, error.ReasonCode);
        Assert.Empty(fixture.Store.ReceivedCalls());
    }

    [Fact(DisplayName = "151: re-activating a record type with a changed schema replaces the compiled validator")]
    public async Task ReActivation_WithChangedSchema_RecompilesAndRefusesTheOldBody()
    {
        var fixture = await ActivatedAsync(WorkOrderV1);

        using var body = JsonDocument.Parse(ValidBody);
        // v1 accepts this body, compiling and caching a validator for v1's content hash.
        var v1 = fixture.RecordType;
        await fixture.Writer.CreateAsync(v1, body, Options(fixture), Authority);

        // Re-activation with a changed schema: content-addressed, so the activated record type
        // carries a new id whose content hash misses the cache and compiles afresh.
        await ActivateAsync(fixture, WorkOrderV2);
        Assert.NotEqual(v1, fixture.RecordType);

        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(fixture.RecordType, body, Options(fixture), Authority));
        Assert.Equal(CompiledEntityValidator.BodyInvalidReason, error.ReasonCode);
        Assert.Contains("required", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "151: a baseline record type whose schema text changes under the SAME id recompiles")]
    public async Task SameSchemaId_ChangedText_RecompilesRatherThanServingAStaleValidator()
    {
        var baseline = new Dictionary<SchemaId, string> { [new SchemaId("work-order")] = WorkOrderV1 };
        var registry = new InMemorySchemaRegistry(TimeProvider.System);
        var validator = new CompiledEntityValidator(registry, baseline);

        using var body = JsonDocument.Parse(ValidBody);
        await validator.ValidateAsync(new SchemaId("work-order"), body);

        baseline[new SchemaId("work-order")] = WorkOrderV2;
        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await validator.ValidateAsync(new SchemaId("work-order"), body));
        Assert.Equal(CompiledEntityValidator.BodyInvalidReason, error.ReasonCode);
    }

    [Fact(DisplayName = "151: the baseline legal-entity record type is validated by the same engine")]
    public async Task BaselineLegalEntitySchema_RefusesAnOverLongName()
    {
        var fixture = Fixture();
        var writer = fixture.Writer;

        var error = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await writer.CreateLegalEntityAsync(
                new CreateLegalEntityCommand(
                    LegalEntityId.NewId(), new string('x', 201), "Llc", "DisregardedEntity", null),
                Authority));

        Assert.Equal(CompiledEntityValidator.BodyInvalidReason, error.ReasonCode);
        Assert.Contains("/legalName", error.Pointers);
    }

    /// <summary>
    /// Perf probe, OFF by default. Set <c>HARBORLINE_VALIDATOR_PERF=1</c> to measure the compiled
    /// Corvus path against the incumbent <see cref="ISchemaRegistry.ValidateAsync"/>
    /// (JsonSchema.Net) path over the same schema and body.
    /// </summary>
    [Fact(DisplayName = "151 perf probe (HARBORLINE_VALIDATOR_PERF=1): compiled vs incumbent engine")]
    public async Task PerfProbe_CompiledVersusIncumbent()
    {
        if (Environment.GetEnvironmentVariable("HARBORLINE_VALIDATOR_PERF") is null)
        {
            return;
        }

        var fixture = await ActivatedAsync(WorkOrderV1);
        using var body = JsonDocument.Parse(ValidBody);
        var bytes = Encoding.UTF8.GetBytes(ValidBody);

        await MeasureAsync("corvus-compiled", () => fixture.Validator.ValidateAsync(fixture.RecordType, body));
        await MeasureAsync("jsonschema-net", async () =>
        {
            var result = await fixture.Registry.ValidateAsync(fixture.RecordType, bytes);
            Assert.True(result.IsValid);
        });

        async Task MeasureAsync(string label, Func<Task> once)
        {
            for (var i = 0; i < 100; i++)
            {
                await once();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var samples = new double[1000];
            for (var i = 0; i < samples.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await once();
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Sort(samples);
            output.WriteLine(
                $"{label}: p50={samples[499]:F4}ms p99={samples[989]:F4}ms max={samples[^1]:F4}ms " +
                $"allocated={allocated / samples.Length}B/validation");
        }
    }

    [Fact(DisplayName = "151: the host composition registers the real validator, not the null object")]
    public void HostComposition_RegistersTheRealValidatorAheadOfTheAssetDefaults()
    {
        var program = Read("apps/local-node-host/Program.cs");
        var registration = program.IndexOf("new Harborline.Api.Kernel.Schema.CompiledEntityValidator(", StringComparison.Ordinal);
        var assetDefaults = program.IndexOf("builder.Services.AddNodeForms(", StringComparison.Ordinal);

        Assert.True(registration >= 0, "the composition must register the authority-side record validator");
        Assert.True(
            registration < assetDefaults,
            "the real validator must be registered BEFORE AddNodeForms, whose TryAdd would otherwise "
                + "leave NullEntityValidator as the shipped default");
    }

    [Fact(DisplayName = "151: the records writer has no structural bypass of the validator")]
    public void RecordsWriter_CallsTheValidatorOnEveryWritePath()
    {
        var writer = Read("apps/local-node-host/Data/Entities/NodeEntityWriter.cs");

        Assert.DoesNotContain("ReferenceEquals(validator", writer, StringComparison.Ordinal);
        // CreateLegalEntityAsync, CreateAsync and UpdateAsync each run the validator.
        Assert.Equal(3, CountOf(writer, "validator.ValidateAsync("));

        static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }

    // ── fixture ──────────────────────────────────────────────────────────────────

    private static readonly AuthorizationWriteContext Authority = new(
        new ActorId("operator"),
        new TenantId("7e570000-0000-0000-0000-0000000000aa"),
        DateTimeOffset.Parse("2026-09-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private sealed record Harness(
        NodeEntityWriter Writer,
        IEntityMutationStore Store,
        InMemoryEntityStore? RealStore,
        ISchemaRegistry Registry,
        IEntityValidator Validator)
    {
        /// <summary>The activated record type's schema id (what a write carries).</summary>
        public SchemaId RecordType { get; set; }
    }

    private static CreateOptions Options(Harness harness) => new(
        "entity", "test", Guid.NewGuid().ToString("N"), Authority.Principal, Authority.Tenant);

    private static Harness Fixture(bool allow = true, bool realStore = false)
    {
        var registry = new InMemorySchemaRegistry(TimeProvider.System);
        // The production baseline map, so the legal-entity case pins the schema the host ships.
        var validator = new CompiledEntityValidator(registry, BaselineRecordTypeSchemas.All);
        var real = realStore
            ? new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System)
            : null;
        // A substitute store proves "nothing persisted" by ReceivedCalls(); the real store proves
        // the write reads back.
        var store = (IEntityMutationStore?)real ?? Substitute.For<IEntityMutationStore>();
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            store,
            validator,
            TestAuthorization.Gate(allow));
        return new Harness(writer, store, real, registry, validator);
    }

    private static async Task<Harness> ActivatedAsync(string schemaText, bool allow = true, bool realStore = false)
    {
        var fixture = Fixture(allow, realStore);
        await ActivateAsync(fixture, schemaText);
        return fixture;
    }

    /// <summary>Pack activation, as PackSeedProjector does it: register the record type's schema
    /// with the ONE registry and carry the id it mints.</summary>
    private static string Read(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.Combine(RepositoryRoot(file), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepositoryRoot(string file)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private static async Task ActivateAsync(Harness fixture, string schemaText)
    {
        var registered = await fixture.Registry.RegisterAsync(schemaText);
        fixture.RecordType = registered.Id;
    }
}
