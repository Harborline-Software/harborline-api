using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-461 route-level proof of the api half of propose, save and release, over the REAL durable pack
/// store and the real SQLCipher packs database. It runs the one Records-and-Forms example the platform
/// fixture describes — edit the invoice Record type and the invoice Form, save a version, check it,
/// release it — and then the refusals around it.
/// </summary>
public sealed class ConfigurationProposalRouteTests : IAsyncLifetime
{
    private const string RecordsEdit = """{"recordType":"invoice","fields":[{"name":"purchaseOrderNumber","kind":"identifier"}]}""";
    private const string FormsEdit = """{"formId":"invoice","sections":[{"id":"header","fields":["purchaseOrderNumber"]}]}""";
    private const string FormsEditWithSupplier = """{"formId":"invoice","sections":[{"id":"header","fields":["purchaseOrderNumber","supplier"]}]}""";
    private const string Rationale = "Capture the purchase order number on invoices.";
    private const string PackageKey = "acme.invoice-purchase-order";
    // T-667: the author states the transport content kind; the node never derives one from the key.
    private const string RecordsKind = "AssetTypeDefinition";
    private const string FormsKind = "FormDefinition";
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000461"));
    private static readonly DateTimeOffset Frozen = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationProposalStore _proposals = null!;
    private ConfigurationActivationTarget _activation = null!;
    private KeyPair _keys = null!;
    private Ed25519Verifier _verifier = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 61);
        _store = new DurablePackInstallStore(_db.Factory);
        var gate = TestPackGate.AllowAll();
        _activation = new ConfigurationActivationTarget(_db.Factory, _store, gate, new InMemoryPackInstallAudit());
        _keys = KeyPair.Generate();
        _verifier = new Ed25519Verifier();
        _proposals = new ConfigurationProposalStore(_db.Factory, _activation, new Ed25519Signer(_keys));
        var activeTeam = new ActiveTeam(new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var clock = new FrozenClock(Frozen);
        ConfigurationProposalRoutes.Map(_app, _proposals, activeTeam, gate, clock, NullLogger.Instance);
        ConfigurationActivationRoutes.Map(_app, _activation, activeTeam, gate, clock, NullLogger.Instance);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");

        Seed("acme.finance", "1.0.0", ["records/invoice", "forms/invoice"]);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _keys?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    // Acceptance 1: a Proposed change records its baseline generation and never changes effective
    // behaviour while being edited.
    [Fact]
    public async Task A_proposed_change_records_its_baseline_and_editing_it_leaves_the_effective_generation_alone()
    {
        var baseline = await EffectiveDigestAsync();
        var started = await StartAsync("proposal-1");
        Assert.Equal("Proposed change", started.GetProperty("detail").GetProperty("status").GetString());
        Assert.Equal(baseline, started.GetProperty("baselineDigest").GetString());

        var first = await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        var second = await AutosaveAsync("proposal-1", "forms/invoice", FormsEdit);
        await SaveVersionAsync("proposal-1", Rationale);

        // Neither the digest nor the stored pointer moved, and no effective-generation row was written.
        Assert.Equal(baseline, await EffectiveDigestAsync());
        Assert.Empty(EffectiveRows());
        Assert.Equal(baseline, second.GetProperty("effectiveDigest").GetString());
        // The working digest moved on each edit; the recorded baseline did not.
        Assert.NotEqual(first.GetProperty("workingDigest").GetString(), second.GetProperty("workingDigest").GetString());
        Assert.Equal(baseline, second.GetProperty("baselineDigest").GetString());
    }

    // Acceptance 2: autosave preserves work; Save version creates an immutable checkpoint with
    // authorship and rationale.
    [Fact]
    public async Task Autosave_survives_a_reread_and_a_saved_version_is_an_immutable_authored_checkpoint()
    {
        await StartAsync("proposal-1");
        await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        await AutosaveAsync("proposal-1", "forms/invoice", FormsEdit);

        // Autosave is durable: a fresh read of the proposed change carries both edits.
        var reread = await ReadAsync("proposal-1");
        Assert.Equal(["forms/invoice", "records/invoice"],
            reread.GetProperty("edits").EnumerateArray().Select(e => e.GetProperty("definitionKey").GetString()));
        Assert.Equal("acme.finance", reread.GetProperty("edits")[1].GetProperty("packageKey").GetString());

        var saved = await SaveVersionAsync("proposal-1", Rationale);
        var version = saved.GetProperty("savedVersion");
        Assert.Equal("Saved version", saved.GetProperty("detail").GetProperty("status").GetString());
        Assert.Equal(1, version.GetProperty("ordinal").GetInt32());
        Assert.Equal(Rationale, version.GetProperty("rationale").GetString());
        // The author is the server-derived acting principal, and it is not blank.
        Assert.False(string.IsNullOrWhiteSpace(version.GetProperty("author").GetString()));
        Assert.Equal(Frozen, version.GetProperty("savedAt").GetDateTimeOffset());
        var frozenDigest = version.GetProperty("digest").GetString();

        // A later edit changes the working state and leaves the checkpoint's stored row untouched.
        await AutosaveAsync("proposal-1", "forms/invoice", FormsEditWithSupplier);
        var row = Assert.Single(SavedVersionRows());
        Assert.Equal(frozenDigest, row.Digest);
        Assert.Contains("purchaseOrderNumber", row.EditsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("supplier", row.EditsJson, StringComparison.Ordinal);
        Assert.NotEqual(frozenDigest, (await ReadAsync("proposal-1")).GetProperty("workingDigest").GetString());

        // A second save is a second ordinal, never a rewrite of the first.
        var again = await SaveVersionAsync("proposal-1", "Also show the supplier.");
        Assert.Equal(2, again.GetProperty("savedVersion").GetProperty("ordinal").GetInt32());
        Assert.Equal(2, SavedVersionRows().Count);
        Assert.Equal(frozenDigest, SavedVersionRows().Single(r => r.Ordinal == 1).Digest);
    }

    // Acceptance 2, refusal half: a Saved version with no rationale is refused, never defaulted.
    [Fact]
    public async Task A_saved_version_without_a_rationale_or_without_any_edit_is_refused()
    {
        await StartAsync("proposal-1");
        using (var blank = await _client.PostAsJsonAsync(Route("proposal-1", "versions"), new { rationale = "  " }))
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        using (var empty = await _client.PostAsJsonAsync(Route("proposal-1", "versions"), new { rationale = Rationale }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
            var body = await empty.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("configuration-proposal-empty", body.GetProperty("code").GetString());
        }
        Assert.Empty(SavedVersionRows());
    }

    // Acceptance 3: release signs the exact saved candidate, and any edit after a check invalidates it.
    [Fact]
    public async Task Release_signs_the_exact_saved_candidate_and_a_later_edit_invalidates_the_check()
    {
        await StartAsync("proposal-1");
        await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        await AutosaveAsync("proposal-1", "forms/invoice", FormsEdit);
        var saved = await SaveVersionAsync("proposal-1", Rationale);
        var savedDigest = saved.GetProperty("savedVersion").GetProperty("digest").GetString()!;
        var checked1 = await CheckAsync("proposal-1", "receipt-1");
        Assert.True(checked1.GetProperty("check").GetProperty("isCurrent").GetBoolean());

        using var released = await ReleaseAsync("proposal-1", 1);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("released", body.GetProperty("status").GetString());
        Assert.Equal("Released package", body.GetProperty("detail").GetProperty("status").GetString());
        var package = body.GetProperty("releasedPackage");
        Assert.Equal(savedDigest, package.GetProperty("savedVersionDigest").GetString());

        // The signature is the api's, over the artifact digest, and it verifies against the stored bytes.
        var offer = Assert.Single(_proposals.Offered(_tenant));
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, _verifier));
        Assert.Equal(package.GetProperty("digest").GetString(), offer.Released.Digest);

        // A tampered document no longer verifies, because the signature covers the artifact digest and
        // the digest is re-derived from the bytes rather than read back from its own column.
        using (var context = _db.CreateContext())
        {
            var row = context.ReleasedPackages.Single();
            var tampered = row.Document.ToArray();
            tampered[^2] ^= 0x01;
            row.Document = tampered;
            context.SaveChanges();
        }
        var tamperedOffer = Assert.Single(_proposals.Offered(_tenant));
        Assert.NotEqual(offer.Released.Digest, tamperedOffer.Released.Digest);
        Assert.False(ConfigurationProposalStore.VerifyOffer(tamperedOffer, _verifier));

        // One more edit after the check, and releasing the same saved version refuses by name and
        // writes no second artifact.
        await AutosaveAsync("proposal-1", "forms/invoice", FormsEditWithSupplier);
        Assert.False((await ReadAsync("proposal-1")).GetProperty("check").GetProperty("isCurrent").GetBoolean());
        using var refused = await ReleaseAsync("proposal-1", 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("configuration-check-invalidated", refusedBody.GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, refusedBody.GetProperty("releasedPackage").ValueKind);
        Assert.Single(ReleasedRows());
    }

    // Acceptance 3, second half: releasing with no check at all, or against a baseline that moved,
    // refuses and writes nothing.
    [Fact]
    public async Task Release_refuses_without_a_check_and_refuses_when_the_baseline_moved()
    {
        var baseline = await EffectiveDigestAsync();
        await StartAsync("proposal-1");
        await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        await SaveVersionAsync("proposal-1", Rationale);

        using (var unchecked1 = await ReleaseAsync("proposal-1", 1))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unchecked1.StatusCode);
            var body = await unchecked1.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("configuration-check-required", body.GetProperty("refusals")[0].GetProperty("code").GetString());
        }
        Assert.Empty(ReleasedRows());

        await CheckAsync("proposal-1", "receipt-1");
        // The tenant's effective generation moves under the author: a new pack becomes Active.
        Seed("acme.tax", "1.0.0", ["records/tax-code"]);
        Assert.NotEqual(baseline, await EffectiveDigestAsync());
        using (var stale = await ReleaseAsync("proposal-1", 1))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
            var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
            var refusal = body.GetProperty("refusals")[0];
            Assert.Equal("configuration-baseline-stale", refusal.GetProperty("code").GetString());
            // The refusal names both generation identities, so the author can see what moved.
            Assert.Contains(baseline, refusal.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.Contains(await EffectiveDigestAsync(), refusal.GetProperty("message").GetString()!, StringComparison.Ordinal);
        }
        Assert.Empty(ReleasedRows());
    }

    // Acceptance 5: the released digest shown to the author is the digest of the artifact later
    // offered for activation.
    [Fact]
    public async Task The_released_digest_shown_to_the_author_is_the_digest_offered_for_activation()
    {
        await StartAsync("proposal-1");
        await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        await AutosaveAsync("proposal-1", "forms/invoice", FormsEdit);
        await SaveVersionAsync("proposal-1", Rationale);
        await CheckAsync("proposal-1", "receipt-1");

        using var released = await ReleaseAsync("proposal-1", 1);
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        var shown = body.GetProperty("releasedPackage").GetProperty("digest").GetString()!;
        // What the surface renders through the released Form is the same digest.
        Assert.Contains(shown, body.GetProperty("detail").GetProperty("releasedPackage").GetString()!, StringComparison.Ordinal);

        using var offers = await _client.GetAsync(ConfigurationProposalRoutes.ReleasesRoute);
        Assert.Equal(HttpStatusCode.OK, offers.StatusCode);
        var offered = Assert.Single((await offers.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(shown, offered.GetProperty("digest").GetString());

        // And it is the digest of the bytes themselves, not a stored label beside them.
        var stored = Assert.Single(ReleasedRows());
        Assert.Equal(shown, Convert.ToHexStringLower(SHA256.HashData(stored.Document)));
        Assert.Equal(shown, stored.Digest);

        // Releasing the same saved version again yields the same artifact and the same offer.
        using var again = await ReleaseAsync("proposal-1", 1);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(shown, (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("releasedPackage").GetProperty("digest").GetString());
        Assert.Single(ReleasedRows());
    }

    // Acceptance 6: the domain-facing vocabulary is what the api emits, bound by the platform.
    [Fact]
    public async Task Every_step_carries_the_released_proposed_change_saved_version_and_released_package_vocabulary()
    {
        await StartAsync("proposal-1");
        await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        await AutosaveAsync("proposal-1", "forms/invoice", FormsEdit);
        Assert.Equal("Proposed change", (await ReadAsync("proposal-1")).GetProperty("detail").GetProperty("status").GetString());
        Assert.Equal("Saved version", (await SaveVersionAsync("proposal-1", Rationale)).GetProperty("detail").GetProperty("status").GetString());
        await CheckAsync("proposal-1", "receipt-1");
        using var released = await ReleaseAsync("proposal-1", 1);
        var detail = (await released.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail");
        Assert.Equal("Released package", detail.GetProperty("status").GetString());
        Assert.Equal(Rationale, detail.GetProperty("rationale").GetString());
        Assert.Contains("records/invoice", detail.GetProperty("editedDefinitions").GetString()!, StringComparison.Ordinal);
        Assert.Contains("forms/invoice", detail.GetProperty("editedDefinitions").GetString()!, StringComparison.Ordinal);
    }

    // The bar this slice draws: releasing is never an implicit activation.
    [Fact]
    public async Task Releasing_a_package_does_not_make_it_effective()
    {
        var baseline = await EffectiveDigestAsync();
        await StartAsync("proposal-1");
        await AutosaveAsync("proposal-1", "records/invoice", RecordsEdit, RecordsKind);
        await SaveVersionAsync("proposal-1", Rationale);
        await CheckAsync("proposal-1", "receipt-1");
        using var released = await ReleaseAsync("proposal-1", 1);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(baseline, await EffectiveDigestAsync());
        Assert.Empty(EffectiveRows());
        Assert.Empty(PreparedProjections());
    }

    [Fact]
    public async Task An_unknown_proposed_change_and_a_duplicate_identity_both_refuse()
    {
        using (var missing = await _client.GetAsync(Route("nope", null)))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var edit = await _client.PutAsJsonAsync(Route("nope", "edits"),
            new { definitionKey = "records/invoice", packageKey = "acme.finance", bodyJson = RecordsEdit, contentKind = RecordsKind }))
            Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        await StartAsync("proposal-1");
        using var duplicate = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId = "proposal-1" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal("configuration-proposal-exists",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unparseable_definition_body_refuses_rather_than_being_repaired()
    {
        await StartAsync("proposal-1");
        using var response = await _client.PutAsJsonAsync(Route("proposal-1", "edits"),
            new { definitionKey = "records/invoice", packageKey = "acme.finance", bodyJson = "not json", contentKind = RecordsKind });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("configuration-proposal-body-invalid",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty((await ReadAsync("proposal-1")).GetProperty("edits").EnumerateArray());
    }

    private static string Route(string proposalId, string? suffix) =>
        $"/api/local-node/configuration/proposals/{proposalId}" + (suffix is null ? string.Empty : $"/{suffix}");

    private async Task<JsonElement> StartAsync(string proposalId)
    {
        using var response = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ReadAsync(string proposalId)
    {
        using var response = await _client.GetAsync(Route(proposalId, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> AutosaveAsync(string proposalId, string definitionKey, string bodyJson,
        string contentKind = FormsKind)
    {
        using var response = await _client.PutAsJsonAsync(Route(proposalId, "edits"),
            new { definitionKey, packageKey = "acme.finance", bodyJson, contentKind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> SaveVersionAsync(string proposalId, string rationale)
    {
        using var response = await _client.PostAsJsonAsync(Route(proposalId, "versions"), new { rationale });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> CheckAsync(string proposalId, string receiptId)
    {
        using var response = await _client.PostAsJsonAsync(Route(proposalId, "checks"), new { receiptId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> ReleaseAsync(string proposalId, int ordinal) =>
        _client.PostAsJsonAsync(Route(proposalId, "release"), new { ordinal, packageKey = PackageKey, revision = "1.1.0" });

    private async Task<string> EffectiveDigestAsync()
    {
        using var response = await _client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("digest").GetString()!;
    }

    private void Seed(string packKey, string version, string[] contentKeys)
    {
        var seeds = contentKeys.Select(key => new PackSeedItem(key, PackContentKind.FormDefinition, "1",
            $$"""{"id":"{{key}}","pack":"{{packKey}}"}""", Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        _store.Commit(new PackInstallTransaction(_tenant, pack, new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));
        _store.Activate(_tenant, packKey, version);
    }

    private List<ConfigurationSavedVersionRow> SavedVersionRows()
    {
        using var context = _db.CreateContext();
        return context.SavedVersions.AsNoTracking().ToList();
    }

    private List<ConfigurationReleasedPackageRow> ReleasedRows()
    {
        using var context = _db.CreateContext();
        return context.ReleasedPackages.AsNoTracking().ToList();
    }

    private List<ConfigurationEffectiveGenerationRow> EffectiveRows()
    {
        using var context = _db.CreateContext();
        return context.EffectiveGenerations.AsNoTracking().ToList();
    }

    private List<ConfigurationPreparedProjectionRow> PreparedProjections()
    {
        using var context = _db.CreateContext();
        return context.PreparedProjections.AsNoTracking().ToList();
    }

    private sealed class FrozenClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private sealed class ActiveTeam(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
