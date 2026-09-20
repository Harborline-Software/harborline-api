using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>What a run produced: the receipt, or the host's named refusal and nothing else.</summary>
/// <param name="Receipt">The minted receipt, or null when the run was refused before any case ran.</param>
/// <param name="Refusals">Why the run was refused; empty on success.</param>
public sealed record VerificationRunResult(VerificationReceipt? Receipt,
    IReadOnlyList<VerificationRefusal> Refusals);

/// <summary>
/// T-463 slice 2: the api half of running a declared verification suite against a candidate
/// configuration generation. The platform (slice 1) produces the contracts — the suite, its
/// admission and the receipt; this executes one admitted suite through the interpreters the host
/// actually runs and mints the receipt the platform defines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which interpreters.</b> Under the coordinator's ruling on T-463 the platform produces the
/// verification contracts and the api hosts execution through whatever interpreters production
/// actually runs. Those are two: <see cref="RuleCompiler"/> /
/// <see cref="FormRuleGraph.EvaluateInstance"/> for the business rules, and the real
/// <see cref="AuthorizationGate"/> for authority. Both are the host's own production engines, and
/// the receipt names each of them with the exact module that ran. When [[T-304]] retires the api
/// copies it must repoint this runner; until then a run is host evidence, not producer evidence.
/// </para>
/// <para>
/// <b>Why the run is isolated.</b> Each case receives a clean world built from the fixture alone:
/// a fresh in-memory <see cref="AccessGrantServiceCollectionExtensions.AddAccessGrantModule"/>
/// container holding only the candidate's own declared roles and capability bindings plus the
/// fixture's declared grants, the fixture's virtual instant, and a rule graph compiled from the
/// candidate's own definitions. Nothing durable is read for the behaviour under examination and
/// nothing at all is written: a verification run cannot create a record, move the effective
/// pointer, emit evidence or leave a grant behind.
/// </para>
/// <para>
/// <b>The candidate is read, never assumed.</b> <see cref="ConfigurationActivationTarget.Reprepare"/>
/// re-reads the prepared candidate by digest through the platform producer, so a run is bound to a
/// candidate that still derives its own digest and still prepares over the stated baseline. A
/// candidate that does not is refused; the runner never falls back to the effective generation.
/// </para>
/// </remarks>
public sealed class VerificationRunner
{
    /// <summary>
    /// The runner's refusal code for an act the acting persona's authority does not cover, whether
    /// the whole write or one field of it. It is this runner's observation vocabulary rather than an
    /// engine string: the concept is the one <c>FormEngine.ComputeWriteDeniedFields</c> raises as
    /// <c>write-denied: &lt;field&gt;</c>, but that message is a private diagnostic carried on a
    /// <c>CapabilityDeniedException</c>, and a verification receipt has to compare stable typed
    /// values that survive being read back years later. The code names the domain fact — the
    /// records authority the actor holds is insufficient for the act — and the accompanying pointer
    /// says exactly where, so a reader of the receipt alone can see what was refused.
    /// </summary>
    public const string AuthorityInsufficient = "records-authority-insufficient";

    /// <summary>The RFC 6901 pointer prefix the refusal pointer addresses: the submitted values document.</summary>
    public const string ValuesPointer = "/values";

    /// <summary>The one action this catalogue registers, and therefore the only one a case may name.</summary>
    private const string CreateAction = "records.create";

    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    private readonly ConfigurationActivationTarget _target;
    private readonly DurablePackInstallStore _packs;
    private readonly TimeProvider _time;

    /// <summary>Composes the runner over the host's candidate reader and its durable pack store.</summary>
    public VerificationRunner(ConfigurationActivationTarget target, DurablePackInstallStore packs, TimeProvider time)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Every engine that produced an observation, including the catalogue that defined them.</summary>
    /// <remarks>
    /// Each identity is the assembly that actually ran and the exact module it was compiled from
    /// (its MVID), so a receipt names the interpreter build rather than a label that could stay put
    /// while the engine underneath it moved. The catalogue reference is the platform's own.
    /// </remarks>
    public static IReadOnlyList<ConfigurationReference> Engines { get; } =
    [
        VerificationCatalog.Reference,
        Engine(typeof(VerificationRunner)),
        Engine(typeof(FormRuleGraph)),
        Engine(typeof(AuthorizationGate)),
    ];

    /// <summary>
    /// Runs one admitted suite against one prepared candidate and mints the receipt. Refusals are
    /// returned by name; nothing partial is minted, because a receipt that omitted a case would be
    /// the very "reports green on the cases it liked" failure the platform's <c>Mint</c> refuses.
    /// </summary>
    public async ValueTask<VerificationRunResult> RunAsync(TenantId tenant, string receiptId,
        string candidateDigest, string expectedBaselineDigest, VerificationSuite suite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suite);
        if (string.IsNullOrWhiteSpace(receiptId))
            return Refused("verification-receipt-id-required", "receiptId", "A receipt identity is required.");

        var prepared = _target.Reprepare(tenant, expectedBaselineDigest, candidateDigest);
        if (prepared.HostRefusal is { } hostRefusal)
            return Refused(hostRefusal.Code, hostRefusal.Target, hostRefusal.Message);
        if (prepared.Preparation is not { Prepared: not null } preparation)
            return Refused("verification-candidate-unprepared", "candidateDigest",
                "The candidate did not prepare over the stated baseline, so there is nothing determinate to verify.");

        var candidate = VerificationCandidateWorld.Resolve(_packs, tenant, preparation.Candidate, out var worldRefusals);
        if (candidate is null) return new(null, worldRefusals);

        var startedAt = _time.GetUtcNow();
        var outcomes = new List<VerificationCaseOutcome>();
        foreach (var item in suite.Cases)
        {
            var fixture = suite.Fixtures.First(entry => entry.FixtureId == item.FixtureId);
            if (item.IsParameterized)
                foreach (var row in item.Rows)
                    outcomes.Add(await RunOneAsync(candidate, fixture, item, row, cancellationToken).ConfigureAwait(false));
            else
                outcomes.Add(await RunOneAsync(candidate, fixture, item, null, cancellationToken).ConfigureAwait(false));
        }

        var receipt = VerificationReceipt.Mint(receiptId, tenant.Value, preparation.Candidate.Digest,
            preparation.Baseline.Digest, suite, Engines, startedAt, _time.GetUtcNow(), outcomes, out var refusals);
        return new(receipt, refusals);
    }

    /// <summary>
    /// One case, or one row of one, from a clean fixture. Every observation the case declared is
    /// made and recorded with both sides typed, whether it agreed or not; an observation is never
    /// skipped because it would fail, which is what makes the row's status derivable.
    /// </summary>
    private async ValueTask<VerificationCaseOutcome> RunOneAsync(VerificationCandidateWorld candidate,
        VerificationFixture fixture, VerificationCase item, VerificationExamplesRow? row,
        CancellationToken cancellationToken)
    {
        if (item.ActionId != CreateAction)
            return VerificationCaseOutcome.Unsupported(item.CaseId, row?.RowId,
                $"The runner executes {CreateAction} only; it will not guess at {item.ActionId}.");

        var inputs = new Dictionary<string, string>(item.Inputs, StringComparer.Ordinal);
        foreach (var (name, value) in row?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal))
            inputs[name] = value;
        if (!TryText(inputs, "recordType", out var recordType) || !TryObject(inputs, "values", out var values))
            return VerificationCaseOutcome.Blocked(item.CaseId, row?.RowId,
                "recordType or values is absent or not of its declared shape, so there is nothing determinate to run.");

        Observed observed;
        try
        {
            observed = await ExecuteCreateAsync(candidate, fixture, recordType, values, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RuleCompilationException exception)
        {
            // A candidate whose own rules do not compile is a real finding about the candidate, but it
            // is not an observation of the claim: say so rather than reporting the claim failed.
            return VerificationCaseOutcome.Blocked(item.CaseId, row?.RowId,
                $"The candidate's rules for {recordType} do not compile: {exception.Message}");
        }
        catch (RuleEngineTimeoutException)
        {
            // The engine's wall-clock liveness guard is an infrastructure fault, not an evaluation
            // outcome (D1). It is this ONE case that could not be observed, so it is blocked here and
            // the rest of the run still reports; a receipt carrying it cannot mint as Passed.
            return VerificationCaseOutcome.Blocked(item.CaseId, row?.RowId,
                $"The candidate's rules for {recordType} exhausted the engine's liveness budget.");
        }

        var made = new List<VerificationObservation>(item.Assertions.Count);
        foreach (var assertion in item.Assertions)
        {
            var predicate = VerificationCatalog.Predicate(assertion.PredicateId)!;
            var expected = row is null
                ? assertion.ExpectedJson!
                : row.Expected[assertion.Key];
            var actual = observed.Read(predicate, assertion.Target);
            var matched = Same(predicate.Expects, expected, actual);
            made.Add(new(assertion.PredicateId, predicate.Version, assertion.Target, expected, actual, matched,
                matched ? null : "verification-expected-mismatch",
                matched ? null : observed.Pointer(predicate, assertion.Target)));
        }
        return VerificationCaseOutcome.Observed(item.CaseId, row?.RowId, made);
    }

    /// <summary>
    /// The act itself. Authority is resolved first and completely: the gate decides the write, and
    /// the roles it derived decide each governed field. Only an act no authority refused reaches the
    /// rules, which is the order production uses — a refused write never computes a total.
    /// </summary>
    private async ValueTask<Observed> ExecuteCreateAsync(VerificationCandidateWorld candidate,
        VerificationFixture fixture, string recordType, JsonObject values, CancellationToken cancellationToken)
    {
        await using var world = await candidate.OpenAsync(fixture, cancellationToken).ConfigureAwait(false);
        var authority = new AuthorizationWriteContext(new ActorId(fixture.Actor), world.Tenant, fixture.Instant);
        var decision = await world.Gate.DecideAsync(authority.Request(RecordsWrite,
            AuthorizationGate.RecordKindFor(RecordsWrite), recordType), cancellationToken).ConfigureAwait(false);
        if (decision.Verdict != AuthorizationVerdict.Allowed)
            return Observed.Refused(ValuesPointer);

        // The roles the GATE derived for this act, not roles the fixture asserted it holds. A fixture
        // declares grants; what those grants amount to under the candidate's own capability bindings
        // is the gate's answer, and it is the answer each governed field is then measured against.
        var held = decision.Derivations.Select(derivation => derivation.Role).ToHashSet();
        var denied = candidate.WriteDeniedFields(recordType, values, held);
        if (denied.Count > 0) return Observed.Refused($"{ValuesPointer}/{Escape(denied[0])}");

        // Authority allowed the act; whether it is ACCEPTED is now the candidate's own rules' answer.
        // A closed save gate is refused under the engine's own released code rather than a second
        // vocabulary invented here, because the engine already named that fault.
        var record = candidate.Evaluate(recordType, values, fixture, out var blocked);
        return blocked is { } fault
            ? new Observed(false, fault.Code, fault.Pointer, "allowed", record, [])
            : new Observed(true, string.Empty, string.Empty, "allowed", record, ["records.created"]);
    }

    /// <summary>
    /// One case's observable surface: the four channels the catalogue's <c>records.create</c>
    /// declares. Reading a channel never re-derives anything — it reports what the act produced.
    /// </summary>
    private sealed record Observed(bool Accepted, string RefusalCode, string RefusalPointer,
        string Decision, JsonObject Record, IReadOnlyList<string> Evidence)
    {
        internal static Observed Refused(string pointer) =>
            new(false, AuthorityInsufficient, pointer, "refused", [], []);

        internal string Read(VerificationPredicate predicate, string? target) => predicate.PredicateId switch
        {
            "outcome.accepted" => Accepted ? "true" : "false",
            "outcome.refusalCode" => JsonSerializer.Serialize(RefusalCode),
            "outcome.refusalPointer" => JsonSerializer.Serialize(RefusalPointer),
            "authorization.decision" => JsonSerializer.Serialize(Decision),
            "evidence.emitted" => JsonSerializer.Serialize(Evidence),
            "record.field" or "record.number" => At(target),
            _ => "null",
        };

        // Where a disagreement was seen, derived from the predicate's own declared channel rather
        // than a list here, so a predicate added to the catalogue later points at the right place
        // instead of quietly pointing into /outcome.
        internal string Pointer(VerificationPredicate predicate, string? target) =>
            predicate.Channel == "record"
                ? target ?? "/record"
                : $"/{predicate.Channel}/{predicate.PredicateId.Split('.')[^1]}";

        // RFC 6901 over the resulting record: the submitted values plus every value the candidate's
        // own rules computed. An absent pointer reads as JSON null, which no well-typed expected
        // value equals, so "the field the claim is about was never produced" fails rather than passes.
        private string At(string? pointer)
        {
            if (string.IsNullOrEmpty(pointer) || pointer[0] != '/') return "null";
            JsonNode? node = Record;
            foreach (var raw in pointer[1..].Split('/'))
            {
                var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                node = node is JsonObject holder && holder.TryGetPropertyValue(token, out var next) ? next : null;
                if (node is null) return "null";
            }
            return node.ToJsonString();
        }
    }

    /// <summary>
    /// Whether the expected and actual values agree as values of the declared kind, never as text.
    /// Numbers compare as canonical decimals so <c>1000</c> and <c>1000.0</c> are one number, and a
    /// text list compares in the order the fixture declared.
    /// </summary>
    private static bool Same(VerificationValueKind kind, string expected, string actual)
    {
        if (!VerificationCatalog.IsWellTyped(kind, actual)) return false;
        if (kind != VerificationValueKind.Number) return JsonNode.DeepEquals(
            JsonNode.Parse(expected), JsonNode.Parse(actual));
        return decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            && decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
            && left == right;
    }

    /// <summary>RFC 6901 escaping, so a field name containing '~' or '/' still addresses itself.</summary>
    internal static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static bool TryText(IReadOnlyDictionary<string, string> inputs, string name, out string value)
    {
        value = string.Empty;
        if (!inputs.TryGetValue(name, out var json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.String) return false;
            value = document.RootElement.GetString()!;
            return value.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryObject(IReadOnlyDictionary<string, string> inputs, string name, out JsonObject value)
    {
        value = [];
        if (!inputs.TryGetValue(name, out var json)) return false;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed) return false;
            value = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static VerificationRunResult Refused(string code, string target, string message) =>
        new(null, [new VerificationRefusal(code, target, message)]);

    private static ConfigurationReference Engine(Type anchor)
    {
        var assembly = anchor.Assembly;
        var name = assembly.GetName();
        var revision = name.Version?.ToString() ?? "0.0.0.0";
        var module = assembly.ManifestModule.ModuleVersionId;
        return new(name.Name ?? anchor.FullName!, revision,
            Convert.ToHexStringLower(SHA256.HashData(module.ToByteArray())));
    }
}
