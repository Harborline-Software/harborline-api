using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 151 slice 2 JUDGE tests — written against origin/main BEFORE any candidate diff was read.
/// <para>
/// L1418: the shipped fallback <see cref="IEntityValidator"/> is <c>NullEntityValidator</c>, an
/// always-accepting null object, so a live record write reaches persistence unvalidated. These tests state
/// the property the slice must hold, independently of how a candidate implements it:
/// </para>
/// <list type="number">
///   <item>a 30-body corpus against the activated Records type schema gets the right verdict;</item>
///   <item>fail-closed: registry throws / unknown id / non-object body / cancellation — refuse, persist nothing;</item>
///   <item>ordering: an unauthorized caller with an invalid body is refused by the GATE, no validation ran;</item>
///   <item>legibility: reason code + RFC 6901 pointer reach the route body, the operator CLI and the trace,
///     and the submitted body reaches none of them;</item>
///   <item>invalidation: after a pack re-activation changes the Records type schema, the body the new schema
///     rejects is refused;</item>
///   <item>bypass fence: every production caller of the entity store's create/update is a named validated
///     writer, and the composed validator is not a null object.</item>
/// </list>
/// <para>
/// Composition is the production graph (<c>AddTestNodeForms</c> → <c>AddNodeForms</c>, the same call
/// Program.cs makes, with the same <c>configureWriters</c> hook that builds <see cref="NodeEntityWriter"/>).
/// The fixtures are owned by the test: the corpus schema is registered in the composed
/// <see cref="ISchemaRegistry"/> and mirrors the MVP pack shape ticket 357 slice 1 established — one Records
/// type whose bound form synthesizes one closed JSON Schema — over the keyword subset that shape emits
/// (required, type, enum, maxLength, minimum/maximum, a nested object, additionalProperties:false).
/// </para>
/// </summary>
public sealed class RecordWriteValidationJudgeTests
{
    private const string PerfEnvironmentVariable = "HARBORLINE_151_PERF";

    /// <summary>By name, not by symbol: a candidate may delete the null object outright.</summary>
    private const string NullValidatorTypeName =
        "Harborline.Api.Foundation.Assets.Entities.NullEntityValidator";

    /// <summary>A value that appears in every corpus body so "the body is never echoed" is checkable.</summary>
    private const string BodyMarker = "judge151marker";

    internal const string RecordsTypeSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "title":  { "type": "string", "maxLength": 10 },
            "status": { "type": "string", "enum": ["open", "closed"] },
            "count":  { "type": "integer", "minimum": 1, "maximum": 5 },
            "owner":  {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
          },
          "required": ["title", "status"],
          "additionalProperties": false
        }
        """;

    /// <summary>The same Records type after a pack upgrade adds a required field.</summary>
    internal const string RecordsTypeSchemaV2 = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "title":  { "type": "string", "maxLength": 10 },
            "status": { "type": "string", "enum": ["open", "closed"] },
            "site":   { "type": "string" }
          },
          "required": ["title", "status", "site"],
          "additionalProperties": false
        }
        """;

    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("judge-151-s2");
    private static readonly ActorId Principal = new("judge-operator");

    // ── Row 1: the corpus ───────────────────────────────────────────────────────────────────────

    /// <summary>(name, body, valid?, expected RFC 6901 pointer — null when the engine may pick one of several).</summary>
    public static TheoryData<string, string, bool, string?> Corpus()
    {
        var data = new TheoryData<string, string, bool, string?>();
        void Row(string name, string body, bool valid, string? pointer = null) => data.Add(name, body, valid, pointer);

        // 20 refusals.
        Row("required-title-missing", """{"status":"open"}""", false);
        Row("required-status-missing", """{"title":"ok"}""", false);
        Row("wrong-type-title-number", """{"title":5,"status":"open"}""", false, "/title");
        Row("wrong-type-count-string", """{"title":"ok","status":"open","count":"3"}""", false, "/count");
        Row("enum-outside-set", """{"title":"ok","status":"archived"}""", false, "/status");
        Row("string-over-max", """{"title":"judge151marker","status":"open"}""", false, "/title");
        Row("number-below-min", """{"title":"ok","status":"open","count":0}""", false, "/count");
        Row("number-above-max", """{"title":"ok","status":"open","count":9}""", false, "/count");
        Row("nested-wrong-type", """{"title":"ok","status":"open","owner":{"name":5}}""", false, "/owner/name");
        Row("nested-required-missing", """{"title":"ok","status":"open","owner":{}}""", false, "/owner/name");
        Row("nested-scalar-for-object", """{"title":"ok","status":"open","owner":"bob"}""", false, "/owner");
        Row("nested-unknown-property", """{"title":"ok","status":"open","owner":{"name":"a","extra":1}}""", false);
        Row("null-for-required-title", """{"title":null,"status":"open"}""", false, "/title");
        Row("null-for-required-status", """{"title":"ok","status":null}""", false, "/status");
        Row("unknown-extra-property", """{"title":"ok","status":"open","judge151marker":1}""", false);
        Row("empty-body", "{}", false);
        Row("array-where-object", "[]", false);
        Row("number-where-object", "42", false);
        Row("unicode-over-max", """{"title":"日本語テスト文字列超過","status":"open"}""", false, "/title");
        Row("fractional-for-integer", """{"title":"ok","status":"open","count":2.5}""", false, "/count");

        // Board amendment (seat a): shapes a careless caller or an attacker reaches for first that the
        // judge corpus did not contain - duplicate keys, a number the schema calls an integer written as a
        // float, exponent and big-integer forms, and an oversized string.
        Row("duplicate-key-both-invalid", """{"title":5,"status":"open","title":7}""", false, "/title");
        Row("exponent-above-max", """{"title":"ok","status":"open","count":1e2}""", false, "/count");
        Row("bignum-above-max", """{"title":"ok","status":"open","count":123456789012345678901}""", false, "/count");
        Row("oversized-title", $$"""{"title":"{{new string('x', 100_000)}}","status":"open"}""", false, "/title");

        // 10 accepts.
        Row("valid-minimal", """{"title":"ok","status":"open"}""", true);
        Row("valid-count-min", """{"title":"ok","status":"open","count":1}""", true);
        Row("valid-count-max", """{"title":"ok","status":"closed","count":5}""", true);
        Row("valid-nested-owner", """{"title":"ok","status":"open","owner":{"name":"a"}}""", true);
        Row("valid-unicode-title", """{"title":"日本語テスト","status":"open"}""", true);
        Row("valid-title-exactly-max", """{"title":"0123456789","status":"open"}""", true);
        Row("valid-status-closed", """{"title":"ok","status":"closed"}""", true);
        Row("valid-nested-unicode-owner", """{"title":"ok","status":"open","count":3,"owner":{"name":"山田"}}""", true);
        Row("valid-emoji-title", """{"title":"😀ok","status":"open"}""", true);
        Row("valid-all-fields", """{"title":"full","status":"open","count":2,"owner":{"name":"b"}}""", true);
        // Board amendment (seat a): JSON Schema 2020-12 calls an integer-valued float an integer.
        Row("valid-integer-valued-float", """{"title":"ok","status":"open","count":3.0}""", true);
        return data;
    }

    [Theory(DisplayName = "151 s2 row 1: the corpus gets the right verdict from the composed validator")]
    [Trait("Holds", "RW-2")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    [MemberData(nameof(Corpus))]
    public async Task Corpus_VerdictMatchesTheActivatedRecordsTypeSchema(
        string name, string bodyJson, bool valid, string? pointer)
    {
        await using var harness = await Harness.CreateAsync();
        using var body = JsonDocument.Parse(bodyJson);
        var options = harness.Options(name);

        if (valid)
        {
            var id = await harness.Writer.CreateAsync(harness.Schema, body, options, harness.Authority);
            var stored = await harness.Reader.GetAsync(id);
            Assert.NotNull(stored);
            // Board amendment (seat b): the accept half asserted only that a row exists, so a validator or
            // writer that rewrote or dropped the body passed. The persisted body is the submitted body.
            Assert.Equal(
                JsonSerializer.Serialize(body.RootElement),
                JsonSerializer.Serialize(stored!.Body.RootElement));
            return;
        }

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, options, harness.Authority));
        Assert.Empty(await harness.PersistedAsync());
        if (pointer is not null)
            Assert.Contains(pointer, Refusal.Text(refusal));
    }

    // ── Row 2: fail-closed ─────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "151 s2 row 2a: a registry whose reads throw refuses the write; nothing persists")]
    [Trait("Holds", "RW-5")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task FailClosed_RegistryThrows()
    {
        await using var harness = await Harness.CreateAsync(registry: ThrowingRegistry(), schemaId: FlippableRegistry.RecordsType);
        using var body = JsonDocument.Parse("""{"title":"ok","status":"open"}""");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("throws"), harness.Authority));
        Assert.Empty(await harness.PersistedAsync());
        // Board amendment (seat b): ThrowsAny plus "nothing persisted" is satisfied by a validator that
        // throws without validating anything (verified: such a plant kept this row green). The write must
        // have reached the authority registry before failing closed.
        Assert.True(harness.RegistryReads > 0,
            "The write failed without consulting the authority schema registry: this row cannot tell "
            + "fail-closed validation from a validator that throws unconditionally.");
    }

    [Fact(DisplayName = "151 s2 row 2b: an unknown schema id is a named refusal, not a pass")]
    [Trait("Holds", "RW-3")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task FailClosed_UnknownSchemaIsANamedRefusal()
    {
        await using var harness = await Harness.CreateAsync();
        using var body = JsonDocument.Parse("""{"title":"ok","status":"open"}""");

        var refusal = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await harness.Writer.CreateAsync(
                new SchemaId("judge-151-unknown-schema"), body, harness.Options("unknown"), harness.Authority));
        Assert.Empty(await harness.PersistedAsync());
        Assert.Contains("schema_unknown", Refusal.Text(refusal));
    }

    [Fact(DisplayName = "151 s2 row 2c: a body that is not a JSON object refuses; nothing persists")]
    [Trait("Holds", "RW-2")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task FailClosed_NonObjectBody()
    {
        await using var harness = await Harness.CreateAsync();
        using var body = JsonDocument.Parse("""[{"title":"ok","status":"open"}]""");

        await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("not-object"), harness.Authority));
        Assert.Empty(await harness.PersistedAsync());
    }

    [Fact(DisplayName = "151 s2 row 2d: the write path honours an already-cancelled token; nothing persists")]
    [Trait("Holds", "RW-5")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task FailClosed_CancellationPropagates()
    {
        await using var harness = await Harness.CreateAsync();
        using var body = JsonDocument.Parse("""{"title":"ok","status":"open"}""");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await harness.Writer.CreateAsync(
                harness.Schema, body, harness.Options("cancelled"), harness.Authority, cancelled.Token));
        Assert.Empty(await harness.PersistedAsync());
    }

    [Fact(DisplayName = "151 s2 row 2e: cancellation raised inside validation propagates as cancellation")]
    [Trait("Holds", "RW-5")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task FailClosed_CancellationInsideValidationIsNotAValidationRefusal()
    {
        // Board amendment (seat b): row 2d's token is cancelled before the call, so the gate or the store
        // satisfies it and the validator is never reached (verified: 2d stayed green against a validator
        // that threw unconditionally). This row cancels inside the registry read instead.
        await using var harness = await Harness.CreateAsync(
            registry: CancellingRegistry(), schemaId: FlippableRegistry.RecordsType);
        using var body = JsonDocument.Parse("""{"title":"ok","status":"open"}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("mid"), harness.Authority));
        Assert.Empty(await harness.PersistedAsync());
    }

    // ── Row 3: ordering ────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "151 s2 row 3: an unauthorized caller with an invalid body is refused by the gate, unvalidated")]
    [Trait("Holds", "RW-1")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task Ordering_GateRefusesBeforeValidation()
    {
        await using var harness = await Harness.CreateAsync(gateAllows: false);
        using var body = JsonDocument.Parse("""{"status":"archived"}""");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("denied"), harness.Authority));
        Assert.Equal(0, harness.ValidationCalls);
        // Board amendment (seat b): the ordering probe counted registry ValidateAsync only, so a validator
        // that resolves a schema through GetAsync - or one that does nothing at all - passed vacuously. No
        // registry read of any kind may happen before the gate refuses.
        Assert.Equal(0, harness.RegistryReads);
        Assert.Empty(await harness.PersistedAsync());
    }

    // ── Row 4: legibility ──────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "151 s2 row 4a: the refusal carries a reason code and an RFC 6901 pointer, never the body")]
    [Trait("Holds", "RW-4")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task Legibility_ReasonCodeAndPointerWithoutTheBody()
    {
        await using var harness = await Harness.CreateAsync();
        using var body = JsonDocument.Parse($$"""{"title":"{{BodyMarker}}","status":"open"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("legible"), harness.Authority));

        var text = Refusal.Text(refusal);
        Assert.Contains("entity.validation", text);
        Assert.Contains("/title", Refusal.Text(refusal));
        Assert.DoesNotContain(BodyMarker, text);
    }

    [SkippableFact(DisplayName = "151 s2 row 4b: the route renders the reason code and pointer as a machine-readable body")]
    [Trait("Holds", "RW-4")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task Legibility_RouteProblemShape()
    {
        Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("HARBORLINE_151_ROUTE"), "1",
            StringComparison.Ordinal),
            "The record-write route needs the EF-backed harness EntityRouteTests builds (a mapped route, a "
            + "migrated SQLite store and the active-team seam); the judge scores this row from the diff.");
        await using var harness = await Harness.CreateAsync();
        await using var route = await RouteHarness.CreateAsync(harness);

        // ADAPTATION (D2): POST /api/local-node/entities takes the legal-entity command, so the corpus
        // body's members do not exist on it and "/title" can never be its pointer. Per the judge's own
        // note on PostRecordAsync, the refusal SHAPE is judged on that route's own activated schema: a
        // marker-bearing legalName with an out-of-enum kind, whose pointer is "/kind".
        using var response = await route.PostRecordAsync(
            $$"""{"legalName":"{{BodyMarker}}","kind":"NotAnEntityKind","taxClassification":"CCorp"}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("entity.validation", payload);
        Assert.Contains("/kind", payload);
        Assert.DoesNotContain(BodyMarker, payload);
        Assert.DoesNotContain("StackTrace", payload);
    }

    [Fact(DisplayName = "151 s2 row 4c: the operator CLI drives a record write and renders the refusal")]
    [Trait("Holds", "RW-4")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public void Legibility_OperatorCliRendersTheRefusal()
    {
        var manifest = File.ReadAllText(RepositoryFile("apps/node-operator-cli/cli-coverage.json"));
        using var document = JsonDocument.Parse(manifest);
        var verbs = document.RootElement.GetProperty("verbs");
        // ADAPTATION (D2): cli-coverage.json maps "METHOD /route" (the KEY) to the verb NAME (the value),
        // so the route is looked up on verb.Name. The judge's original read scanned the values, where a
        // route never appears; with it, this row could not go green for any candidate.
        var driven = verbs.EnumerateObject()
            .Any(verb => verb.Name.Contains("POST /api/local-node/entities", StringComparison.Ordinal));
        Assert.True(driven,
            "No operator-CLI verb drives POST /api/local-node/entities, so the headless record write does not "
            + "exist: the validator cannot be shown to run on it (cli-coverage.json still exempts the route).");

        // Board amendment (seat b): the second half of this row stubbed an HTTP handler returning a refusal
        // body the test itself wrote and asserted the CLI echoed it under `health --json`. That measures the
        // stub, not the validator, and it never ran because the assert above fails in every candidate.
        // Removed; once a verb exists, assert it here against the node's own refusal.
    }

    [SkippableFact(DisplayName = "151 s2 row 4d: the refused write leaves a machine-readable trace entry without the body")]
    [Trait("Holds", "RW-4")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task Legibility_DecisionTrace()
    {
        // Board amendment (seat c): ticket 151's acceptance is an authorization decision and authority-side
        // validation before persistence; the "why can I do this?" trace is M3 clause 6, owned by tickets 212
        // and 331, and the brief's Do-4 list does not ask for it. Score it on purpose, not on the gate.
        Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("HARBORLINE_151_TRACE"), "1",
            StringComparison.Ordinal),
            "A refusal trace entry is M3 clause 6 (tickets 212, 331), not ticket 151 acceptance; "
            + "set HARBORLINE_151_TRACE=1 to score it.");
        await using var harness = await Harness.CreateAsync();
        using var body = JsonDocument.Parse($$"""{"title":"{{BodyMarker}}","status":"open"}""");

        await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("traced"), harness.Authority));

        var trace = await harness.TraceFactsAsync();
        Assert.Contains(trace, fact => fact.Contains("entity.validation", StringComparison.Ordinal));
        Assert.Contains(trace, fact => fact.Contains("/title", StringComparison.Ordinal));
        Assert.DoesNotContain(BodyMarker, string.Join('\n', trace));
    }

    // ── Row 5: invalidation ────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "151 s2 row 5: a Records type schema whose content changes under one id is not served stale")]
    [Trait("Holds", "RW-6")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task Invalidation_ReactivationIsHonoured()
    {
        var flip = new FlippableRegistry(RecordsTypeSchema, RecordsTypeSchemaV2);
        await using var harness = await Harness.CreateAsync(
            registry: flip.Registry, schemaId: FlippableRegistry.RecordsType);
        using var body = JsonDocument.Parse("""{"title":"ok","status":"open"}""");

        var id = await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("v1"), harness.Authority);
        Assert.NotNull(await harness.Reader.GetAsync(id));

        flip.ActivateV2();
        using var same = JsonDocument.Parse("""{"title":"ok","status":"open"}""");
        await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await harness.Writer.CreateAsync(harness.Schema, same, harness.Options("v2"), harness.Authority));
    }

    [Fact(DisplayName = "151 s2 row 5b: the update path validates against the stored record's current schema")]
    [Trait("Holds", "RW-6")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task Invalidation_UpdateIsValidatedToo()
    {
        await using var harness = await Harness.CreateAsync();
        using var good = JsonDocument.Parse("""{"title":"ok","status":"open"}""");
        var id = await harness.Writer.CreateAsync(harness.Schema, good, harness.Options("update"), harness.Authority);

        using var bad = JsonDocument.Parse("""{"title":"ok","status":"archived"}""");
        await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await harness.Writer.UpdateAsync(id, bad, new UpdateOptions(Principal, ValidFrom: At), harness.Authority));

        var stored = await harness.Reader.GetAsync(id);
        Assert.NotNull(stored);
        Assert.DoesNotContain("archived", stored!.Body.RootElement.GetRawText());
    }

    // ── Row 6: the bypass fence ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "151 s2 row 6: every production record-write call site is a named validated writer")]
    [Trait("Holds", "RW-7")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    [Trait("Holds", "RW-9")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public void Fence_EveryRecordWriteCallerIsAValidatedWriter()
    {
        var discovered = ArchTests.RecordWriteValidatedWriterFence.DiscoveredRecordWriteCallers();
        Assert.NotEmpty(discovered);

        var unlisted = discovered
            .Where(caller => !ArchTests.RecordWriteValidatedWriterFence.ValidatedWriters
                .Any(writer => caller.StartsWith(writer, StringComparison.Ordinal)))
            .Where(caller => !ArchTests.RecordWriteValidatedWriterFence.ExceptionRows
                .Contains(caller, StringComparer.Ordinal))
            .ToArray();
        Assert.True(unlisted.Length == 0,
            "Production record-write call sites that are not named validated writers:\n"
            + string.Join("\n", unlisted.Order(StringComparer.Ordinal)));

        // "Validated" is the other half of the claim: the validator the production composition hands those
        // writers must not be an always-accepting null object (L1418).
        var composed = Harness.ComposedValidatorTypeName();
        Assert.False(composed == NullValidatorTypeName,
            $"The production composition still hands the record writers {composed} — L1418 is open.");
    }

    // ── Row 7: non-record write shapes (board amendment, seat a) ────────────────────────

    [Fact(DisplayName = "151 s2 row 7: installing the record validator does not refuse other entity shapes")]
    [Trait("Holds", "RW-8")] // acceptance traceability (AcceptanceTraceabilityArchTests)
    public async Task NonRecordWriteShapesStillPersist()
    {
        // Seat (a): every judge row drives the record coordinator, so a candidate that put the validator in
        // the shared IEntityMutationStore hook scored full marks here while breaking every other write shape
        // - definition envelopes, form instances, sync replay, pack-activation seeding, boot backfills. The
        // Mac full runs found exactly that on candidates C (21 unlisted failures) and E; no judge row did.
        // Those shapes write through this port under schema ids the authority registry never holds.
        // allowNullValidator: this row is a NON-REGRESSION row, green on origin/main and red only when the
        // validator is moved into the shared store hook, so it must not inherit the L1418 composition assert.
        await using var harness = await Harness.CreateAsync(allowNullValidator: true);
        var mutations = harness.Services.GetRequiredService<IEntityMutationStore>();
        using var envelope = JsonDocument.Parse(
            """{"kind":"form-definition","identity":"judge-151-envelope","status":"Published"}""");

        var id = await mutations.CreateAsync(
            new SchemaId("judge-151-definition-envelope"), envelope, harness.Options("envelope"));

        var stored = await harness.Reader.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(
            JsonSerializer.Serialize(envelope.RootElement),
            JsonSerializer.Serialize(stored!.Body.RootElement));
    }

    // ── Perf probe (opt in with HARBORLINE_151_PERF=1) ─────────────────────────────────────────

    [SkippableFact(DisplayName = "151 s2 perf probe: 1,000 warm record creates through the real coordinator")]
    public async Task RecordWritePerfProbe()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(PerfEnvironmentVariable), "1", StringComparison.Ordinal),
            $"Set {PerfEnvironmentVariable}=1 to run the 151 s2 perf probe.");

        // Baseline mode: on a tree with no real validator (origin/main) the probe measures the null-object
        // write path, so the delta the validator adds is measurable on the same harness.
        await using var harness = await Harness.CreateAsync(allowNullValidator: true);
        using var body = JsonDocument.Parse("""{"title":"ok","status":"open","count":3,"owner":{"name":"a"}}""");
        Console.WriteLine($"[151-s2-perf] validator={harness.Validator.GetType().FullName}");

        var cold = Stopwatch.StartNew();
        await harness.Writer.CreateAsync(harness.Schema, body, harness.Options("cold"), harness.Authority);
        cold.Stop();

        for (var i = 0; i < 100; i++)
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options($"warm-{i}"), harness.Authority);

        var samples = new double[1000];
        var watch = new Stopwatch();
        for (var i = 0; i < samples.Length; i++)
        {
            watch.Restart();
            await harness.Writer.CreateAsync(harness.Schema, body, harness.Options($"probe-{i}"), harness.Authority);
            watch.Stop();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < 10_000; i++)
            try { await harness.Validator.ValidateAsync(harness.Schema, body); }
            catch (EntityValidationException) { }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var heapGrowth = GC.GetTotalMemory(forceFullCollection: true) - heapBefore;

        Console.WriteLine(
            $"[151-s2-perf] cold={cold.Elapsed.TotalMilliseconds:F3}ms p50={samples[499]:F4}ms "
            + $"p99={samples[989]:F4}ms max={samples[^1]:F4}ms "
            + $"allocated10k={allocated} heapGrowth10k={heapGrowth}");
    }

    // ── Fixtures owned by the test ─────────────────────────────────────────────────────────────

    private static string RepositoryFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }

    internal static class Refusal
    {
        /// <summary>
        /// Everything the refusal carries, whatever member the candidate chose: the message chain, the
        /// exception data, and every public instance member that is a string or a collection.
        /// </summary>
        internal static string Text(Exception refusal)
        {
            var sink = new StringBuilder();
            for (var error = refusal; error is not null; error = error.InnerException)
            {
                sink.Append(error.Message).Append('\n');
                foreach (var value in error.Data.Values) sink.Append(value).Append('\n');
                foreach (var property in error.GetType()
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.GetIndexParameters().Length == 0
                        && property.Name is not (nameof(Exception.InnerException) or nameof(Exception.Data)
                            or nameof(Exception.TargetSite) or nameof(Exception.StackTrace))))
                {
                    object? value = null;
                    try { value = property.GetValue(error); } catch (Exception) { }
                    if (value is string text) sink.Append(text).Append('\n');
                    else if (value is System.Collections.IEnumerable rows)
                        foreach (var row in rows) sink.Append(Flatten(row)).Append('\n');
                    else if (value is not null && value.GetType().IsValueType)
                        sink.Append(value).Append('\n');
                }
            }
            return sink.ToString();
        }

        private static string Flatten(object? row)
        {
            if (row is null) return string.Empty;
            if (row is string text) return text;
            var sink = new StringBuilder(row.ToString());
            foreach (var property in row.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0))
            {
                object? value = null;
                try { value = property.GetValue(row); } catch (Exception) { }
                if (value is not null) sink.Append(' ').Append(value);
            }
            return sink.ToString();
        }
    }

    private sealed class JudgeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private readonly CountingRegistry _counting;

        private Harness(
            ServiceProvider provider,
            IEntityStore reader,
            NodeEntityWriter writer,
            IEntityValidator validator,
            CountingRegistry counting,
            SchemaId schema)
        {
            _provider = provider;
            Reader = reader;
            Writer = writer;
            Validator = validator;
            _counting = counting;
            Schema = schema;
        }

        internal IEntityStore Reader { get; }
        internal NodeEntityWriter Writer { get; }
        internal IEntityValidator Validator { get; }
        internal SchemaId Schema { get; }
        internal IServiceProvider Services => _provider;
        /// <summary>Real validation work observed at the authority's schema registry.</summary>
        internal int ValidationCalls => _counting.Calls;

        /// <summary>Any read of the authority registry: ValidateAsync or a schema resolution by id.</summary>
        internal int RegistryReads => _counting.Reads;

        internal AuthorizationWriteContext Authority => new(Principal, Tenant, At);

        internal CreateOptions Options(string nonce) =>
            new("entity", "judge", nonce, Principal, Tenant, At, nonce);

        internal async Task<IReadOnlyList<Entity>> PersistedAsync()
        {
            var rows = new List<Entity>();
            await foreach (var entity in Reader.QueryAsync(new EntityQuery(Tenant: Tenant)))
                rows.Add(entity);
            return rows;
        }

        /// <summary>
        /// Every trace fact the refused write left behind, read through whatever the candidate recorded it
        /// on: the authorization trace steps the node already projects, plus any audit body the judge can
        /// reach from the composed container.
        /// </summary>
        internal async Task<string[]> TraceFactsAsync()
        {
            var facts = new List<string>();
            var trail = _provider.GetService<Harborline.Api.Kernel.Audit.IAuditTrail>();
            if (trail is null) return [];
            await foreach (var entry in trail.QueryAsync(new Harborline.Api.Kernel.Audit.AuditQuery(Tenant)))
            {
                facts.Add(entry.EventType.ToString());
                facts.Add(JsonSerializer.Serialize(entry.Payload.Payload));
                if (entry.AuthoritySnapshot is { } snapshot) facts.Add(JsonSerializer.Serialize(snapshot));
            }
            return [.. facts];
        }

        internal static async Task<Harness> CreateAsync(
            ISchemaRegistry? registry = null,
            bool gateAllows = true,
            SchemaId? schemaId = null,
            bool allowNullValidator = false)
        {
            var counting = new CountingRegistry(
                registry ?? new InMemorySchemaRegistry(new JudgeClock(At)));
            var provider = Compose(counting, gateAllows, configure: null);
            var validator = ComposedValidator(provider, allowNullValidator);
            var schema = schemaId ?? (await counting.RegisterAsync(RecordsTypeSchema)).Id;

            return new Harness(
                provider,
                provider.GetRequiredService<IEntityStore>(),
                provider.GetRequiredService<NodeEntityWriter>(),
                validator,
                counting,
                schema);
        }

        internal static string ComposedValidatorTypeName()
        {
            using var provider = Compose(null, gateAllows: true, configure: null);
            var validator = ComposedValidator(provider, allowNullValidator: true);
            return validator.GetType().FullName ?? validator.GetType().Name;
        }

        /// <summary>
        /// The validator the candidate own composition puts on the record-write path: the
        /// <see cref="IEntityValidator"/> slot when it is not the null object, else a concrete validator the
        /// graph registered under its own type (a slot left as the null object is itself a finding), else the
        /// single production implementation activated from this container.
        /// </summary>
        private static IEntityValidator ComposedValidator(ServiceProvider provider, bool allowNullValidator)
        {
            var resolved = provider.GetRequiredService<IEntityValidator>();
            if (resolved.GetType().FullName != NullValidatorTypeName) return resolved;

            var candidates = ProductionValidatorTypes();
            foreach (var type in candidates)
            {
                if (provider.GetService(type) is IEntityValidator registered) return registered;
            }
            if (allowNullValidator) return resolved;

            Assert.True(candidates.Length > 0,
                "The composed IEntityValidator is the always-accepting NullEntityValidator and no other "
                + "production implementation exists: L1418 is open.");
            Assert.True(candidates.Length == 1,
                "More than one production IEntityValidator implementation; the judge cannot choose: "
                + string.Join(", ", candidates.Select(type => type.FullName)));
            return (IEntityValidator)ActivatorUtilities.CreateInstance(provider, candidates[0]);
        }

        private static Type[] ProductionValidatorTypes() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name is { } name
                    && name.StartsWith("Harborline.", StringComparison.Ordinal)
                    && !name.Contains("Tests", StringComparison.Ordinal))
                .SelectMany(SafeTypes)
                .Where(type => typeof(IEntityValidator).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                    && type.FullName != NullValidatorTypeName)
                .Distinct()
                .ToArray();

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
        }

        /// <summary>
        /// Concrete collaborators the candidate coordinator constructor asks for that its own composition
        /// registers outside <c>AddNodeForms</c> (the Program.cs block). Registering them here keeps the
        /// judge able to build the production coordinator; needing it is recorded as a finding.
        /// </summary>
        private static Type[] CoordinatorDependencies() =>
            typeof(NodeEntityWriter).GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericType: false }
                    && type.Assembly == typeof(NodeEntityWriter).Assembly
                    && type.GetConstructors().Length == 1)
                .Distinct()
                .ToArray();

        private static ServiceProvider Compose(
            ISchemaRegistry? registry,
            bool gateAllows,
            Action<IServiceCollection>? configure)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFrozenKernelClock(new JudgeClock(At));
            services.AddSingleton(Substitute.For<IDbContextFactory<LocalNodeDbContext>>());
            services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            if (registry is not null) services.AddSingleton(registry);
            configure?.Invoke(services);
            services.AddTestAuthorizationGate();
            if (!gateAllows) services.AddSingleton(TestAuthorization.Gate(false));
            // ActivatorUtilities, not a hand-written new: a candidate may change the coordinator's
            // constructor (candidate C replaces the store port and the validator with its own seams).
            services.AddTestNodeForms(configureWriters: (inner, entityMutations, _) =>
            {
                inner.AddSingleton(sp => entityMutations(sp));
                foreach (var dependency in CoordinatorDependencies())
                    inner.TryAddSingleton(dependency);
                inner.AddSingleton(sp => ActivatorUtilities.CreateInstance<NodeEntityWriter>(sp));
            });
            return services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
    }

    /// <summary>
    /// Counts the validation work that actually reaches the authority schema registry. Counting here rather
    /// than by replacing the <see cref="IEntityValidator"/> registration leaves each candidate composition
    /// exactly as it shipped.
    /// </summary>
    private sealed class CountingRegistry(ISchemaRegistry inner) : ISchemaRegistry
    {
        private int _calls;
        private int _reads;

        internal int Calls => _calls;

        /// <summary>
        /// Board amendment: reads of either shape, so a row asserting "the validator consulted the authority"
        /// (2a) or "it did not" (3) holds for a candidate that resolves by id as well as one that calls
        /// ValidateAsync.
        /// </summary>
        internal int Reads => _reads;

        public ValueTask<SchemaValidationResult> ValidateAsync(
            SchemaId id, ReadOnlyMemory<byte> documentBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _reads);
            return inner.ValidateAsync(id, documentBytes, ct);
        }

        public ValueTask<Schema?> GetAsync(SchemaId id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return inner.GetAsync(id, ct);
        }

        public ValueTask<Schema> RegisterAsync(
            string jsonSchemaText, IReadOnlyList<SchemaId>? parents = null, IReadOnlyList<string>? tags = null,
            int? blobThreshold = null, CancellationToken ct = default)
            => inner.RegisterAsync(jsonSchemaText, parents, tags, blobThreshold, ct);

        public IAsyncEnumerable<Schema> ListAsync(string? tagFilter = null, CancellationToken ct = default)
            => inner.ListAsync(tagFilter, ct);

        public ValueTask<MigrationPlan> PlanMigrationAsync(
            SchemaId from, SchemaId to, CancellationToken ct = default)
            => inner.PlanMigrationAsync(from, to, ct);

        public ValueTask<ReadOnlyMemory<byte>> MigrateAsync(
            SchemaId from, SchemaId to, ReadOnlyMemory<byte> document, CancellationToken ct = default)
            => inner.MigrateAsync(from, to, document, ct);

        public Harborline.Api.Kernel.SchemaRegistry.Lenses.LensGraph Lenses => inner.Lenses;

        public Harborline.Api.Kernel.SchemaRegistry.Upcasters.UpcasterChain Upcasters => inner.Upcasters;

        public Harborline.Api.Kernel.SchemaRegistry.Epochs.IEpochCoordinator Epochs => inner.Epochs;
    }

    /// <summary>
    /// The production entity route, mapped as <c>HostedEntityApiEndpoint</c> maps it.
    /// <para>
    /// ADAPTATION (D2): the judge shipped this harness as a bare <c>WebApplication</c> with no route
    /// mapped, and row 4b skipped, because the record-write route needs an EF-backed store. It is wired
    /// here the way <c>EntityRouteTests</c> wires it — the SAME <c>EntityRoutes.Map</c> production
    /// registration over a migrated temp SQLite file, the real <c>NodeEntityWriter</c>, and the real
    /// compile-at-activation validator over a registry with the node's baseline record schemas activated
    /// — so the row is scored by RUNNING it under <c>HARBORLINE_151_ROUTE=1</c>.
    /// </para>
    /// </summary>
    private sealed class RouteHarness : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
        private readonly HttpClient _client;
        private readonly string _directory;

        private RouteHarness(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, string directory)
        {
            _app = app;
            _client = client;
            _directory = directory;
        }

        internal static async Task<RouteHarness> CreateAsync(Harness harness)
        {
            ArgumentNullException.ThrowIfNull(harness);
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddTestKernelClock();
            builder.Services.AddSingleton(
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.AllowAll());

            var directory = Path.Combine(
                Path.GetTempPath(), "harborline-judge-151-route-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            builder.Services.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
            // The ledger module's model needs the periods module beside it (EntityRouteTests registers the
            // same pair); BlockEntityModuleDiscoveryTests fences every ledger composition on that pairing.
            builder.Services.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
            builder.Services.AddDbContextFactory<LocalNodeDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "judge-151.db")};Pooling=False"));

            builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(
                new Harborline.Api.LocalNodeHost.Tests.Packs.MutableAuthorizationContext());
            builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Identity
                .ISelectedSessionPermissionResolver,
                Harborline.Api.LocalNodeHost.Data.Identity.FailClosedSelectedSessionPermissionResolver>();

            var app = builder.Build();
            // In production the listener binds this feature before the middleware; the route family is
            // device-reachable product data, so without it the fence refuses the POST with a 403 before
            // any validation runs (EntityRouteTests installs the same seam).
            Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, next) =>
            {
                context.Features.Set(
                    Harborline.Api.LocalNodeHost.Health.DesktopPlaneRequestFeature.Instance);
                await next(context);
            });
            var factory = app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();

            var writer = new NodeEntityWriter(
                factory,
                harness.Validator,
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
            Harborline.Api.LocalNodeHost.Health.EntityRoutes.Map(
                Harborline.Api.LocalNodeHost.Health.DeviceReachableProductDataRouteFence
                    .MapDeviceReachableProductDataGroup(app),
                factory,
                new JudgeActiveTeam(),
                writer,
                new JudgeClock(At));

            await app.StartAsync();
            var addresses = app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
            var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return new RouteHarness(app, client, directory);
        }

        /// <summary>
        /// Posts a record body to the node's record-write route. The judge asks the candidate's own route:
        /// when a candidate ships no record-write route, the refusal shape is judged on the legal-entity
        /// POST the slice already validates.
        /// </summary>
        internal async Task<HttpResponseMessage> PostRecordAsync(string body) =>
            await _client.PostAsync(
                Harborline.Api.LocalNodeHost.Health.EntityRoutes.RouteBase,
                new StringContent(body, Encoding.UTF8, "application/json"));

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>One fixed active team, so the route's tenant resolution has something to resolve.</summary>
    private sealed class JudgeActiveTeam : Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor
    {
        public Harborline.Api.Kernel.Runtime.Teams.TeamContext? Active { get; } = new(
            new Harborline.Api.Kernel.Runtime.Teams.TeamId(
                Guid.Parse("7e570151-0000-0000-0000-00000000d2d2")),
            "Judge 151",
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System);

        public Task SetActiveAsync(Harborline.Api.Kernel.Runtime.Teams.TeamId teamId, CancellationToken ct) =>
            throw new NotSupportedException("The judge route harness pins one active team.");

#pragma warning disable CS0067 // one pinned team never changes, but the seam declares the event
        public event EventHandler<Harborline.Api.Kernel.Runtime.Teams.ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067
    }

    /// <summary>A registry that cancels while validating (board amendment row 2e).</summary>
    private static ISchemaRegistry CancellingRegistry()
    {
        var registry = Substitute.For<ISchemaRegistry>();
        registry.GetAsync(Arg.Any<SchemaId>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Schema?>>(_ => throw new OperationCanceledException());
        registry.ValidateAsync(Arg.Any<SchemaId>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<SchemaValidationResult>>(_ => throw new OperationCanceledException());
        return registry;
    }

    /// <summary>A registry whose reads fail (the fail-closed row).</summary>
    private static ISchemaRegistry ThrowingRegistry()
    {
        var registry = Substitute.For<ISchemaRegistry>();
        registry.GetAsync(Arg.Any<SchemaId>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Schema?>>(_ => throw new InvalidOperationException(
                "judge: the schema registry is unavailable."));
        registry.ValidateAsync(Arg.Any<SchemaId>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<SchemaValidationResult>>(_ => throw new InvalidOperationException(
                "judge: the schema registry is unavailable."));
        return registry;
    }

    /// <summary>
    /// One stable <see cref="SchemaId"/> whose CONTENT changes when the pack is re-activated — the stale
    /// compiled-schema probe. Evaluation stays real: the id is mapped onto a really registered schema.
    /// </summary>
    private sealed class FlippableRegistry
    {
        internal static readonly SchemaId RecordsType = new("judge-151-records-type");

        private readonly InMemorySchemaRegistry _inner = new(TimeProvider.System);
        private readonly string _v2;
        private SchemaId _active;

        internal FlippableRegistry(string v1, string v2)
        {
            _v2 = v2;
            _active = _inner.RegisterAsync(v1).GetAwaiter().GetResult().Id;
            Registry = Build();
        }

        internal ISchemaRegistry Registry { get; }

        internal void ActivateV2() => _active = _inner.RegisterAsync(_v2).GetAwaiter().GetResult().Id;

        private SchemaId Map(SchemaId id) => id == RecordsType ? _active : id;

        private ISchemaRegistry Build()
        {
            var registry = Substitute.For<ISchemaRegistry>();
            registry.GetAsync(Arg.Any<SchemaId>(), Arg.Any<CancellationToken>())
                .Returns(call => GetAsync(call.ArgAt<SchemaId>(0), call.ArgAt<CancellationToken>(1)));
            registry.ValidateAsync(
                    Arg.Any<SchemaId>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(call => _inner.ValidateAsync(
                    Map(call.ArgAt<SchemaId>(0)), call.ArgAt<ReadOnlyMemory<byte>>(1),
                    call.ArgAt<CancellationToken>(2)));
            return registry;
        }

        private async ValueTask<Schema?> GetAsync(SchemaId id, CancellationToken ct)
        {
            var schema = await _inner.GetAsync(Map(id), ct).ConfigureAwait(false);
            return schema is null ? null : schema with { Id = id };
        }
    }
}
