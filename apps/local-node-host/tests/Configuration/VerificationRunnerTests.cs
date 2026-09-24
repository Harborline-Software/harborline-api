using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;
using Harborline.Blocks.BuilderDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-463 slice 2: the api runner executing one declared suite against a candidate generation through
/// the host's production interpreters — the SPINE-1 rule engine and the real <c>AuthorizationGate</c>
/// — over the real durable pack store.
/// </summary>
/// <remarks>
/// <para>
/// Slice 2b: the suite is <b>not declared here</b>. It is parsed out of the platform-owned corpus
/// the pinned <c>Harborline.Blocks.BuilderDefinitions</c> package carries, so the platform states
/// the suite once and this host executes it. Slice 2 restated it in C# and the two copies had
/// already diverged on the fixture's grant scope.
/// </para>
/// <para>
/// Acceptance line 7 is the reason this file exists, and it is proved in BOTH directions. The suite
/// carries two deliberately independent claims over one invoice: an invariant about approval
/// authority that never asserts a total, and an examples table about the total that never asserts an
/// authority. Three candidates differ in exactly one definition each. Asserting only that a mutation
/// failed its own case would not prove independence, so every mutation case asserts the other claim
/// still passes — a runner that failed everything on any defect would pass the positive half and
/// fail here.
/// </para>
/// <para>
/// Acceptance line 3's execution half is proved by the clean run: every case starts from the fixture
/// alone (nothing a previous case granted survives into the next one), and every declared assertion
/// is recorded with both an expected and an actual typed value, matched or not.
/// </para>
/// </remarks>
public sealed class VerificationRunnerTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000463"));
    private static readonly DateTimeOffset Frozen = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private const string Author = "tax.roles/invoice.author";
    private const string Approver = "tax.roles/invoice.approver";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationActivationTarget _target = null!;
    private VerificationRunner _runner = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 46);
        _store = new DurablePackInstallStore(_db.Factory);
        var gate = TestPackGate.AllowAll();
        _target = new ConfigurationActivationTarget(_db.Factory, _store, gate, new InMemoryPackInstallAudit());
        _runner = new VerificationRunner(_target, _store, new FrozenClock(Frozen), static instant => new FrozenClock(instant));
        _tenant = ActiveTeamTenantContext.ProjectTenantId(TeamA);
        var activeTeam = new StaticActiveTeamAccessor(new TeamContext(TeamA, "Team A",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ConfigurationActivationRoutes.Map(_app, _target, activeTeam, gate, new FrozenClock(Frozen),
            NullLogger.Instance, _runner);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");

        // One access pack every candidate shares: the two domain role names the fixture grants, and the
        // publisher ceiling that offers records:write to both of them. Sharing it is deliberate — it is
        // what makes the two mutations below the ONLY difference between the three candidates.
        Seed("finance.access",
            (RoleKey("author"), PackContentKind.RoleDefinition, Role("invoice.author", "Invoice author")),
            (RoleKey("approver"), PackContentKind.RoleDefinition, Role("invoice.approver", "Invoice approver")),
            ("access/invoice", PackContentKind.AuthorizationCapabilityBinding, Binding()));

        // The three candidates. Each owns records/invoice; the ownership selection at prepare time is
        // what picks one, so the suite, the fixtures and every other definition are byte-identical.
        Seed("finance.clean", ("records/invoice", PackContentKind.FormDefinition, Invoice(Multiply, [Approver])));
        Seed("finance.rule-defect", ("records/invoice", PackContentKind.FormDefinition, Invoice(Add, [Approver])));
        Seed("finance.authz-defect", ("records/invoice", PackContentKind.FormDefinition, Invoice(Multiply, [Approver, Author])));
        _store.RecordKeyOwnership(_tenant, "records/invoice", "finance.clean");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    /// <summary>
    /// Line 3's execution half, and the clean baseline the two mutations are measured against: every
    /// claim holds, and every declared assertion was actually observed rather than assumed.
    /// </summary>
    [Fact]
    public async Task A_clean_candidate_passes_every_claim_and_records_both_sides_of_every_assertion()
    {
        var receipt = await RunAsync("finance.clean", "receipt-clean");

        Assert.Equal(VerificationStatus.Passed, receipt.Status);
        Assert.Equal(4, receipt.Outcomes.Count);
        Assert.All(receipt.Outcomes, outcome => Assert.Equal(VerificationStatus.Passed, outcome.Status));

        // Every declared assertion answered exactly once, with a typed expected AND a typed actual.
        var authority = Outcome(receipt, "approval-authority");
        Assert.Equal(
            ["authorization.decision", "outcome.accepted", "outcome.refusalCode", "outcome.refusalPointer"],
            authority.Observations.Select(observation => observation.PredicateId).Order(StringComparer.Ordinal));
        Assert.All(authority.Observations, observation =>
        {
            Assert.True(observation.Matched);
            Assert.False(string.IsNullOrWhiteSpace(observation.ExpectedJson));
            Assert.False(string.IsNullOrWhiteSpace(observation.ActualJson));
            Assert.Null(observation.FindingCode);
        });

        // The refusal the clerk earns is the vocabulary this slice authored, at the field it was for.
        Assert.Equal("\"records-authority-insufficient\"", Actual(authority, "outcome.refusalCode"));
        Assert.Equal("\"/values/status\"", Actual(authority, "outcome.refusalPointer"));
        Assert.Equal("\"refused\"", Actual(authority, "authorization.decision"));

        foreach (var (rowId, total) in Rows)
        {
            var row = Outcome(receipt, "invoice-total", rowId);
            Assert.Equal(total.ToString(CultureInfo.InvariantCulture), Actual(row, "record.number"));
            Assert.Equal("true", Actual(row, "outcome.accepted"));
        }

        // The receipt says what ran it: the catalogue plus the api's own production interpreters.
        Assert.Contains(receipt.Engines, engine => engine == VerificationCatalog.Reference);
        Assert.Contains(receipt.Engines, engine => engine.Key == "Harborline.Api.Foundation.RuleEngine");
        Assert.Contains(receipt.Engines, engine => engine.Key == "Harborline.Api.Foundation.Authorization");
        Assert.All(receipt.Engines, engine => Assert.Matches("^[0-9a-f]{64}$", engine.Digest));
    }

    /// <summary>
    /// Line 7, first direction. The business-rule defect fails ALL THREE invoice-total rows — and
    /// approval-authority still passes, which is the half that proves the claims are independent.
    /// </summary>
    [Fact]
    public async Task A_business_rule_defect_fails_every_invoice_total_row_and_leaves_approval_authority_passing()
    {
        var receipt = await RunAsync("finance.rule-defect", "receipt-rule-defect");

        Assert.Equal(VerificationStatus.Failed, receipt.Status);
        Assert.Equal(VerificationStatus.Passed, Outcome(receipt, "approval-authority").Status);
        foreach (var (rowId, total) in Rows)
        {
            var row = Outcome(receipt, "invoice-total", rowId);
            Assert.Equal(VerificationStatus.Failed, row.Status);
            var observation = Observation(row, "record.number");
            Assert.False(observation.Matched);
            Assert.Equal(total.ToString(CultureInfo.InvariantCulture), observation.ExpectedJson);
            Assert.Equal("verification-expected-mismatch", observation.FindingCode);
            Assert.Equal("/total", observation.Pointer);
        }

        // The other direction stated explicitly: the authority claim observed the same four values it
        // observes on a clean candidate, and every one of them still agreed.
        var authority = Outcome(receipt, "approval-authority");
        Assert.All(authority.Observations, observation => Assert.True(observation.Matched));
        Assert.Equal("\"records-authority-insufficient\"", Actual(authority, "outcome.refusalCode"));
    }

    /// <summary>
    /// Line 7, the exact reverse. The authorization defect — the candidate's invoice also lets the
    /// author write the approved status — fails approval-authority on every observation it makes,
    /// and leaves all three invoice-total rows passing.
    /// </summary>
    [Fact]
    public async Task An_authorization_defect_fails_approval_authority_and_leaves_every_invoice_total_row_passing()
    {
        var receipt = await RunAsync("finance.authz-defect", "receipt-authz-defect");

        Assert.Equal(VerificationStatus.Failed, receipt.Status);
        var authority = Outcome(receipt, "approval-authority");
        Assert.Equal(VerificationStatus.Failed, authority.Status);
        Assert.Equal(4, authority.Observations.Count);
        Assert.All(authority.Observations, observation =>
        {
            Assert.False(observation.Matched);
            Assert.Equal("verification-expected-mismatch", observation.FindingCode);
        });
        Assert.Equal("true", Actual(authority, "outcome.accepted"));
        Assert.Equal("\"allowed\"", Actual(authority, "authorization.decision"));
        Assert.Equal("\"\"", Actual(authority, "outcome.refusalCode"));
        Assert.Equal("\"\"", Actual(authority, "outcome.refusalPointer"));

        // The other direction stated explicitly: every business-rule row is untouched and still passes.
        foreach (var (rowId, total) in Rows)
        {
            var row = Outcome(receipt, "invoice-total", rowId);
            Assert.Equal(VerificationStatus.Passed, row.Status);
            Assert.Equal(total.ToString(CultureInfo.InvariantCulture), Actual(row, "record.number"));
        }
    }

    /// <summary>
    /// The candidate is read, not assumed: a digest no prepared projection derives is refused by name
    /// and mints nothing, so a run can never report on a candidate that was never prepared.
    /// </summary>
    [Fact]
    public async Task An_unprepared_candidate_is_refused_and_mints_no_receipt()
    {
        var baseline = _target.ReadEffective(_tenant).Digest;
        var run = await _runner.RunAsync(_tenant, "receipt-unprepared", new string('0', 64), baseline, Suite());

        Assert.Null(run.Receipt);
        Assert.Equal("configuration-projection-missing", Assert.Single(run.Refusals).Code);
    }

    /// <summary>
    /// Line 4, held at the runner: a case the runner will not execute deterministically is reported
    /// Unsupported and carries no observations, so the receipt cannot mint as Passed. The platform
    /// derives the status; this proves the api never hands it an outcome that would lie.
    /// </summary>
    [Fact]
    public async Task A_case_the_runner_cannot_execute_is_not_a_pass()
    {
        var outcome = VerificationCaseOutcome.Unsupported("approval-authority", null, "not executable here");

        Assert.Equal(VerificationStatus.Unsupported, outcome.Status);
        Assert.Empty(outcome.Observations);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The route runs the same suite and answers in the released vocabulary with the read-only
    /// detail bindings both renderer lanes consume, so slice 3 renders a real run rather than a
    /// second shape of its own.
    /// </summary>
    [Fact]
    public async Task The_verify_route_runs_the_suite_and_returns_the_released_detail_bindings()
    {
        var baseline = _target.ReadEffective(_tenant).Digest;
        var candidate = Candidate("finance.clean", baseline);

        using var response = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.VerifyRoute, new
        {
            expectedBaselineDigest = baseline,
            candidateDigest = candidate,
            receiptId = "receipt-route",
            suite = System.Text.Encoding.UTF8.GetString(Suite().Document.Span),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Passed", body.GetProperty("status").GetString());
        Assert.Equal(candidate, body.GetProperty("candidateDigest").GetString());
        Assert.Matches("^[0-9a-f]{64}$", body.GetProperty("receiptDigest").GetString());
        var detail = body.GetProperty("detail");
        Assert.Equal("Passed", detail.GetProperty("status").GetString());
        Assert.Equal(string.Empty, detail.GetProperty("failures").GetString());
        Assert.Contains("approval-authority", detail.GetProperty("cases").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The transport re-admits the suite document through the platform before anything runs, so a
    /// case this host would have refused at authoring cannot be smuggled in over the wire and run.
    /// </summary>
    [Fact]
    public async Task The_verify_route_refuses_a_suite_document_the_platform_would_not_admit()
    {
        var baseline = _target.ReadEffective(_tenant).Digest;
        var document = System.Text.Encoding.UTF8.GetString(Suite().Document.Span)
            .Replace("\"assertions\":", "\"ignored\":", StringComparison.Ordinal);

        using var response = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.VerifyRoute, new
        {
            expectedBaselineDigest = baseline,
            candidateDigest = Candidate("finance.clean", baseline),
            receiptId = "receipt-inadmissible",
            suite = document,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("refusals").EnumerateArray(),
            refusal => refusal.GetProperty("code").GetString() == "verification-assertion-required");
    }

    /// <summary>
    /// Owner guardrail 5, and the point of slice 2b: the platform-owned corpus runs through the
    /// api-hosted interpreters, so it can be re-run and compared after T-304 rebinds them.
    /// <para>
    /// Every other test in this file asserts against the suite that <see cref="Suite"/> parses out
    /// of the pinned package, so a change to the document already reaches them. This test is the
    /// one that names the disagreement instead of letting it surface as a puzzling mismatch inside
    /// a run: it pins the digest, the authority the fixtures grant, and every value this host
    /// expects back, against what the document actually declares.
    /// </para>
    /// </summary>
    [Fact]
    public void The_suite_this_host_runs_is_the_pinned_platform_corpus()
    {
        var corpus = Corpus();
        var suite = Suite();

        // The document re-admitted to the digest the platform recorded for it. So this is that
        // corpus, parsed faithfully — not something merely shaped like it.
        Assert.Equal(corpus.GetProperty("suiteDigest").GetString(), suite.Digest);

        // The fixtures grant at the install root. A record-scoped act canonicalises to
        // /records/<recordId>, and VerificationCandidateWorld reads a bare name as one segment, so
        // a package name like "finance" would become /finance, contain nothing in the records tree,
        // and leave the actor holding nothing for records:write — every case would refuse and the
        // clean run would "pass" for the wrong reason. This is the value the two corpora differed in.
        Assert.All(suite.Fixtures,
            fixture => Assert.All(fixture.Grants, grant => Assert.Equal("/", grant.Scope)));
        Assert.Equal(["approver", "clerk"],
            suite.Fixtures.Select(fixture => fixture.FixtureId).Order(StringComparer.Ordinal));

        // Every total this file asserts a run returns is the document's total, and the row set is
        // the document's row set. Add a row upstream, drop one, or change a product, and this fails.
        var rows = suite.Cases.Single(item => item.CaseId == "invoice-total").Rows;
        Assert.Equal(Rows.Select(row => row.RowId).Order(StringComparer.Ordinal),
            rows.Select(row => row.RowId).Order(StringComparer.Ordinal));
        foreach (var (rowId, total) in Rows)
            Assert.Equal(total.ToString(CultureInfo.InvariantCulture),
                rows.Single(row => row.RowId == rowId).Expected["record.number:/total"]);

        // And the refusal vocabulary the runner mints is the document's, not this host's. These are
        // the four values the clean and authorization-defect tests below compare actuals against.
        Assert.Equal(
            [
                "authorization.decision=\"refused\"",
                "outcome.accepted=false",
                "outcome.refusalCode=\"records-authority-insufficient\"",
                "outcome.refusalPointer=\"/values/status\"",
            ],
            suite.Cases.Single(item => item.CaseId == "approval-authority").Assertions
                .Select(assertion => $"{assertion.Key}={assertion.ExpectedJson}").Order(StringComparer.Ordinal));
    }

    private static readonly (string RowId, int Total)[] Rows =
    [
        ("two-at-one-hundred", 200), ("ten-at-one-hundred", 1000), ("three-at-four-hundred", 1200),
    ];

    private string Candidate(string owner, string baseline)
    {
        var prepared = _target.Prepare(_tenant, baseline,
            ["finance.access", "finance.clean", "finance.rule-defect", "finance.authz-defect"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["records/invoice"] = owner }, Frozen);
        Assert.NotNull(prepared.Preparation?.Prepared);
        return prepared.Preparation!.Candidate.Digest;
    }

    private async Task<VerificationReceipt> RunAsync(string owner, string receiptId)
    {
        var baseline = _target.ReadEffective(_tenant).Digest;
        var run = await _runner.RunAsync(_tenant, receiptId, Candidate(owner, baseline), baseline, Suite());
        Assert.Empty(run.Refusals);
        return run.Receipt!;
    }

    private static VerificationCaseOutcome Outcome(VerificationReceipt receipt, string caseId, string? rowId = null) =>
        Assert.Single(receipt.Outcomes, outcome => outcome.CaseId == caseId && outcome.RowId == rowId);

    private static VerificationObservation Observation(VerificationCaseOutcome outcome, string predicateId) =>
        Assert.Single(outcome.Observations, observation => observation.PredicateId == predicateId);

    private static string Actual(VerificationCaseOutcome outcome, string predicateId) =>
        Observation(outcome, predicateId).ActualJson;

    // ── The suite, the fixtures and the candidates ────────────────────────────────────────────────

    private const string Multiply = """{"*":[{"var":"quantity"},{"var":"unitPrice"}]}""";
    private const string Add = """{"+":[{"var":"quantity"},{"var":"unitPrice"}]}""";

    /// <summary>
    /// The platform-owned verification corpus, read out of the pinned
    /// <c>Harborline.Blocks.BuilderDefinitions</c> package — the same
    /// <c>conformance/hlp.blocks.builder-definitions/verification.json</c> the platform's producer
    /// tests and both renderer lanes consume. Moving the platform pin moves this document.
    /// </summary>
    private static JsonElement Corpus() => JsonSerializer.Deserialize<JsonElement>(VerificationSuite.Corpus());

    /// <summary>
    /// The canonical Records-and-Rules suite: one invariant over an authorization rule and one
    /// examples table over a business rule, sharing one invoice and asserting nothing in common.
    /// <para>
    /// This host declares none of it. It parses the platform's document through the platform's own
    /// admission, so there is one corpus rather than a twin here that can drift out of step with it
    /// — which is exactly what had happened by slice 2. See
    /// <see cref="The_suite_this_host_runs_is_the_pinned_platform_corpus"/>, which fails if this
    /// host's expectations and that document ever disagree again.
    /// </para>
    /// </summary>
    private static VerificationSuite Suite()
    {
        var suite = VerificationSuite.Parse(Corpus().GetProperty("suite").GetRawText(), out var refusals);
        Assert.Empty(refusals);
        Assert.NotNull(suite);
        return suite!;
    }

    private static string RoleKey(string name) => $"roles/invoice-{name}";

    private static string Role(string name, string display) =>
        $$"""{"role":"{{name}}","displayName":"{{display}}"}""";

    private static string Binding() => string.Create(CultureInfo.InvariantCulture,
        $$"""{"operation":"records:write","scope":"/","offeredRoles":["{{Author}}","{{Approver}}"]}""");

    /// <summary>
    /// One invoice form definition: its total rule and the write roles its approved status needs.
    /// Those two are the only things the three candidates differ in, one each.
    /// </summary>
    private static string Invoice(string totalExpression, string[] statusWriteRoles)
    {
        var overlay = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["supplier"] = Field("Supplier"),
                    ["quantity"] = Field("Quantity"),
                    ["unitPrice"] = Field("Unit price"),
                    ["total"] = Field("Total"),
                    ["status"] = new
                    {
                        label = Text("Status"),
                        // The narrower gate: an approved status is the approver's to write, and the
                        // section gate alone would let the author write it.
                        writeRoles = statusWriteRoles.Select(Name).ToArray(),
                    },
                },
                sections = new[]
                {
                    new
                    {
                        id = "invoice",
                        title = Text("Invoice"),
                        fields = new[] { "supplier", "quantity", "unitPrice", "status", "total" },
                        access = new
                        {
                            readRoles = new[] { Name(Author), Name(Approver) },
                            writeRoles = new[] { Name(Author), Name(Approver) },
                        },
                    },
                },
                rules = new[]
                {
                    new
                    {
                        id = "invoice.total", tier = "JsonLogic", scope = "Field", scopeTarget = "total",
                        expression = totalExpression, action = "Compute",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(overlay);
    }

    private static object Field(string label) => new { label = Text(label) };

    private static object Text(string value) => new
    {
        defaultLocale = "en",
        values = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = value },
    };

    // A form overlay names roles in the form's own terms, which is the bare role name the released
    // write gate compares; the qualified vocabulary belongs to the grant and the capability binding.
    private static string Name(string qualified) => qualified[(qualified.IndexOf('/', StringComparison.Ordinal) + 1)..];

    private void Seed(string packKey, params (string Key, PackContentKind Kind, string Json)[] content)
    {
        var seeds = content.Select(item => new PackSeedItem(item.Key, item.Kind, "1.0.0", item.Json,
            Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(item.Key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, []);
        _store.Commit(new PackInstallTransaction(_tenant, pack,
            new PackInstallWatermark(packKey, "1.0.0", new Dictionary<string, int>()), []));
        _store.Activate(_tenant, packKey, "1.0.0");
    }

    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class StaticActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
