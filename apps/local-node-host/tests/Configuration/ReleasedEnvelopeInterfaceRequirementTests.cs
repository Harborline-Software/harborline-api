using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-615 records-ck-41 into T-396: the landed platform producer states a definition's interface requirement on
/// its own envelope (<c>envelope.requires[].capability</c>, <c>pack-key@N</c>), while live activation's
/// <see cref="PackInterfaceRequirementCheck"/> read only <c>definitionEnvelope.requires</c>. These controls run
/// the genuine enforcing caller, <c>PackInstaller.ActivateAsync</c>, over released-shape seed content, and the
/// real producer through <see cref="ReleasedPackInstaller"/> for the release path. They cover live Active-pack
/// activation only; pinned Records admission against the signed dependency closure is a separate gate.
/// </summary>
/// <remarks>
/// Oracles. Every body, key, version and interface number is a literal authored here; refusal codes, details,
/// pointers and audit text are literals, never the production constants. The producer-ignored requirement
/// shapes are the malformed-entry list of Platform 2c7bef ConfigurationProposalTests (its Layout-kind theory),
/// extended with the entries the Records parse (RecordsRequirement.TryParse, NumberStyles.None) names no package
/// for: a bare key, @0, a signed or padded version, and a Rule-style string entry. No fact pins self-reference:
/// no ruling decides whether a pack satisfies its own requirement (gap D-1 in the handoff). The release-path
/// refusals assume release atomicity: a refused release rolls back whole, leaving no Draft, seed or watermark.
/// </remarks>
public sealed class ReleasedEnvelopeInterfaceRequirementTests : IAsyncLifetime
{
    private const string ReleaseKey = "tenant.release";
    private const string ConsumerKey = "tenant.consumer";
    private const string LedgerKey = "acme.ledger";
    private const string FinanceKey = "acme.finance";
    private const string Revision = "1.1.0";
    private const string AssetKind = "AssetTypeDefinition";
    private const string FinanceInvoiceSeed = """{"id":"forms/invoice","pack":"acme.finance"}""";
    private const string UnmetCode = "pack.install.activate.unmet_interface_requirement";

    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000615"));
    private static readonly DateTimeOffset Frozen = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationProposalStore _proposals = null!;
    private NodePrincipalSigner _node = null!;
    private PackInstaller _installer = null!;
    private InMemoryPackInstallAudit _audit = null!;
    private IPackTrustStore _trustStore = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 61);
        _store = new DurablePackInstallStore(_db.Factory);
        var gate = TestPackGate.AllowAll();
        var activation = new ConfigurationActivationTarget(_db.Factory, _store, gate, evidence: null);

        var seed = new byte[32];
        for (var index = 0; index < seed.Length; index++) seed[index] = (byte)(index + 61);
        _node = new NodePrincipalSigner(seed);
        _proposals = new ConfigurationProposalStore(_db.Factory, activation, _node.Signer);

        var codec = new PackFileCodec();
        _audit = new InMemoryPackInstallAudit();
        _installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec),
            _store, new WorkflowRefusingPackContentAdmission(), _audit,
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        _trustStore = HostedPackInstallApiEndpoint.BuildTrustStore(_node, NullLogger.Instance);
        var releases = new ReleasedPackInstaller(_proposals, Exporter(), _installer, _node, new Ed25519Verifier(),
            _trustStore, PackRevocationList.Empty, _store, gate);

        var activeTeam = new ActiveTeam(new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);
        PlatformPackTestPreload.Activate(_store, _tenant);

        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ConfigurationProposalRoutes.Map(_app, _proposals, activeTeam, gate, new FrozenClock(Frozen), NullLogger.Instance, releases);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _node?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ---- The enforcing caller over released-shape content ----

    [Fact]
    public async Task EnvelopeRequirementMetByExactActiveInterfaceActivatesWithBodyUnchanged()
    {
        const string body = """{"title":"Alpha","envelope":{"requires":[{"capability":"acme.ledger@1"}]}}""";
        Seed(LedgerKey, "1.0.0", 1, ["ledger/account"], ("ledger/account", PackContentKind.AssetTypeDefinition, """{"title":"Account"}"""));
        await InstallConsumerAsync(("consumer/alpha", body));

        var activation = await _installer.ActivateAsync(Context(), ConsumerKey, "1.0.0");

        Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
        var item = Assert.Single(_store.GetActive(_tenant, ConsumerKey)!.SeedItems);
        // Read where it was signed: no definitionEnvelope is synthesized into the transported body.
        AssertBody(body, item.ParseContent());
    }

    [Fact]
    public Task EnvelopeRequirementOnMissingPackRefusesActivationBeforeWrite() =>
        AssertActivationRefusedAsync("""{"title":"Alpha","envelope":{"requires":[{"capability":"acme.ledger@1"}]}}""",
            "interface requirement 'acme.ledger@1' declared by 'consumer/alpha' is not exposed by any active pack.");

    [Fact]
    public Task EnvelopeRequirementAtOtherInterfaceVersionRefusesActivationBeforeWrite()
    {
        Seed(LedgerKey, "1.0.0", 1, ["ledger/account"], ("ledger/account", PackContentKind.AssetTypeDefinition, """{"title":"Account"}"""));
        return AssertActivationRefusedAsync("""{"title":"Alpha","envelope":{"requires":[{"capability":"acme.ledger@2"}]}}""",
            "interface requirement 'acme.ledger@2' declared by 'consumer/alpha' is not exposed by any active pack.");
    }

    [Fact]
    public Task EnvelopeRequirementOnPackDeclaringNoInterfaceRefusesActivationBeforeWrite()
    {
        Seed(LedgerKey, "1.0.0", null, null, ("ledger/account", PackContentKind.AssetTypeDefinition, """{"title":"Account"}"""));
        return AssertActivationRefusedAsync("""{"title":"Alpha","envelope":{"requires":[{"capability":"acme.ledger@1"}]}}""",
            "interface requirement 'acme.ledger@1' declared by 'consumer/alpha' is not exposed by any active pack.");
    }

    [Fact]
    public async Task EnvelopeShapesTheProducerIgnoresStateNoRequirement()
    {
        const string ignored = """{"title":"Ignored","envelope":{"requires":[null,7,{},{"capability":7},{"capability":null},{"capability":""},{"capability":" "},{"capability":"acme.ledger"},{"capability":"acme.ledger@0"},{"capability":"acme.ledger@ 1"},{"capability":"acme.ledger@+1"},{"capability":"acme.ledger@1 "},"acme.ledger@1"]}}""";
        const string legacy = """{"title":"Legacy","envelope":"legacy"}""";
        await InstallConsumerAsync(("consumer/ignored", ignored), ("consumer/legacy", legacy));

        var activation = await _installer.ActivateAsync(Context(), ConsumerKey, "1.0.0");

        Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
        Assert.Equal(2, _store.GetActive(_tenant, ConsumerKey)!.SeedItems.Count);
    }

    // ---- The real producer through the released-package install ----

    [Fact]
    public async Task RealReleaseRequirementMetByExactActiveInterfaceInstallsWithSignedBodyUnchanged()
    {
        const string body = """{"title":"Alpha","envelope":{"requires":[{"capability":"acme.finance@1"}]}}""";
        Seed(FinanceKey, "1.0.0", 1, ["forms/invoice"], ("forms/invoice", PackContentKind.FormDefinition, FinanceInvoiceSeed));
        var digest = await ReleaseAsync("proposal-615-met", body);
        AssertReleasedBody(digest, body);

        var installed = await InstallAsync(digest);

        Assert.Equal(HttpStatusCode.OK, installed.Status);
        Assert.Equal("installed", installed.Body.GetProperty("status").GetString());
        var pack = _store.GetActive(_tenant, ReleaseKey)!;
        Assert.Equal(Revision, pack.Version);
        AssertBody(body, Assert.Single(pack.SeedItems).ParseContent());
    }

    [Fact]
    public Task RealReleaseRequirementAtOtherInterfaceVersionRefusesActivation()
    {
        Seed(FinanceKey, "1.0.0", 1, ["forms/invoice"], ("forms/invoice", PackContentKind.FormDefinition, FinanceInvoiceSeed));
        return AssertReleaseActivationRefusedAsync("proposal-615-mismatch",
            """{"title":"Alpha","envelope":{"requires":[{"capability":"acme.finance@2"}]}}""",
            "pack.install.activate.unmet_interface_requirement: interface requirement 'acme.finance@2' declared by 'release.alpha' is not exposed by any active pack.");
    }

    /// <summary>The unrevised provider shape of ReleasedPackProducerExposureIntegrationTests control 2:
    /// acme.finance declares no interface, so a definition requiring acme.finance@1 cannot activate.</summary>
    [Fact]
    public Task RealReleaseRequirementOnPackDeclaringNoInterfaceRefusesActivation()
    {
        Seed(FinanceKey, "1.0.0", null, null, ("forms/invoice", PackContentKind.FormDefinition, FinanceInvoiceSeed));
        return AssertReleaseActivationRefusedAsync("proposal-615-undeclared",
            """{"title":"Alpha","envelope":{"requires":[{"capability":"acme.finance@1"}]}}""",
            "pack.install.activate.unmet_interface_requirement: interface requirement 'acme.finance@1' declared by 'release.alpha' is not exposed by any active pack.");
    }

    private async Task AssertActivationRefusedAsync(string body, string detail)
    {
        await InstallConsumerAsync(("consumer/alpha", body));
        var before = CatalogueHash();

        var activation = await _installer.ActivateAsync(Context(), ConsumerKey, "1.0.0");

        Assert.False(activation.Activated);
        Assert.Equal(UnmetCode, activation.Error);
        Assert.Equal(detail, activation.Detail);
        Assert.Equal(new PackInstallRefusal(UnmetCode, "/contents/0/contentBase64"), activation.Refusal);
        Assert.Equal(before, CatalogueHash());
        Assert.Null(_store.GetActive(_tenant, ConsumerKey));
    }

    private async Task AssertReleaseActivationRefusedAsync(string proposalId, string body, string auditDetail)
    {
        var digest = await ReleaseAsync(proposalId, body);
        AssertReleasedBody(digest, body);
        var finance = _store.GetActive(_tenant, FinanceKey)!;
        var live = PackState(_store);
        var durable = PackState(new DurablePackInstallStore(_db.Factory));

        var refused = await InstallAsync(digest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("refused", refused.Body.GetProperty("status").GetString());
        var refusal = Assert.Single(refused.Body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-activation-refused", refusal.GetProperty("code").GetString());
        Assert.Equal("releasedPackage", refusal.GetProperty("target").GetString());
        Assert.Equal(UnmetCode, refusal.GetProperty("message").GetString());
        Assert.Contains(_audit.Query(_tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.PackKey == ReleaseKey && entry.Version == Revision && entry.Detail == auditDetail);
        // Release atomicity: the refused release is one write that rolls back whole. No release version, seed or
        // watermark remains, and the pack state reads as before, live and from a fresh store over the same database.
        Assert.Null(_store.GetVersion(_tenant, ReleaseKey, Revision));
        Assert.DoesNotContain(_store.ListInstalled(_tenant), pack => pack.PackKey == ReleaseKey);
        Assert.Null(_store.GetWatermark(_tenant, ReleaseKey));
        Assert.Equal(live, PackState(_store));
        Assert.Equal(durable, PackState(new DurablePackInstallStore(_db.Factory)));
        var provider = _store.GetActive(_tenant, FinanceKey)!;
        Assert.Equal((finance.Version, finance.InterfaceVersion), (provider.Version, provider.InterfaceVersion));
        Assert.Equal(finance.SeedItems.Select(item => item.CanonicalJson), provider.SeedItems.Select(item => item.CanonicalJson));
    }

    /// <summary>The tenant's installed versions and seeds and its key ownership, with the release's and acme.finance's
    /// watermarks and overlays, as the store reads them.</summary>
    private string PackState(DurablePackInstallStore store) => JsonSerializer.Serialize(new
    {
        Installed = store.ListInstalled(_tenant),
        Watermarks = new[] { ReleaseKey, FinanceKey }.Select(key => store.GetWatermark(_tenant, key)),
        Overrides = new[] { ReleaseKey, FinanceKey }.SelectMany(key => store.GetOverrides(_tenant, key)
            .Select(row => $"{key}/{row.ContentKey}={row.OverlayPatch.ToJsonString()}")),
        Ownership = store.GetKeyOwnership(_tenant).OrderBy(owner => owner.Key, StringComparer.Ordinal)
            .Select(owner => $"{owner.Key}={owner.Value}"),
    });

    private async Task InstallConsumerAsync(params (string Key, string Body)[] items)
    {
        var outcome = await Exporter().ExportAsync(new PackExportRequest(ConsumerKey, "1.0.0", ConsumerKey,
            "Released-envelope interface requirement", PackScopeTier.Vertical,
            items.Select(item => new PackContentSource(item.Key, PackContentKind.AssetTypeDefinition, "1.0.0",
                JsonNode.Parse(item.Body)!)).ToArray(),
            Array.Empty<PackDependencyRef>(), Array.Empty<string>(), PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(_node.Signer.IssuerId.ToBase64Url())), _node.Signer);
        Assert.True(outcome.Succeeded, string.Join(",", outcome.Validation.Errors.Select(error => error.Code)));
        var installed = await _installer.InstallAsync(outcome.FileBytes!, Context());
        Assert.True(installed.Installed, string.Join(",", installed.RefusalCodes));
    }

    private PackInstallContext Context() => new(_tenant, _trustStore, PackRevocationList.Empty,
        AdmittedInstant.FromRecordedAct(Frozen), PackInstallRoutes.RevocationMaxAge, Principal: "interface-mapping-test");

    private static PackExporter Exporter() => new(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
        new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
        new PackFileCodec(), TimeProvider.System);

    private async Task<string> ReleaseAsync(string proposalId, string body)
    {
        var proposal = $"/api/local-node/configuration/proposals/{proposalId}";
        using (var started = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId }))
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var edited = await _client.PutAsJsonAsync($"{proposal}/edits",
            new { definitionKey = "release.alpha", packageKey = ReleaseKey, bodyJson = body, contentKind = AssetKind }))
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        using (var saved = await _client.PostAsJsonAsync($"{proposal}/versions", new { rationale = "Require an interface." }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var checked_ = await _client.PostAsJsonAsync($"{proposal}/checks", new { receiptId = "receipt-615" }))
            Assert.Equal(HttpStatusCode.OK, checked_.StatusCode);
        using var released = await _client.PostAsJsonAsync($"{proposal}/release",
            new { ordinal = 1, packageKey = ReleaseKey, revision = Revision });
        var dto = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(released.StatusCode == HttpStatusCode.OK, dto.ToString());
        return dto.GetProperty("releasedPackage").GetProperty("digest").GetString()!;
    }

    /// <summary>The signed released document carries the body as authored and pins acme.finance at its baseline.</summary>
    private void AssertReleasedBody(string digest, string body)
    {
        var offer = Assert.Single(_proposals.Offered(_tenant), candidate => candidate.Released.Digest == digest);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(offer.Released.Document.Span)));
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, new Ed25519Verifier()));
        using var parsed = JsonDocument.Parse(offer.Released.Document);
        var payload = parsed.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("content").GetProperty("payload"))
            .Single(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("definitionKey", out _));
        AssertBody(body, JsonNode.Parse(payload.GetProperty("body").GetRawText()));
        Assert.Equal(new[] { "acme.finance@1.0.0" }, parsed.RootElement.GetProperty("closure").GetProperty("dependencies")
            .EnumerateArray().Select(pin => $"{pin.GetProperty("key").GetString()}@{pin.GetProperty("version").GetString()}").ToArray());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> InstallAsync(string digest)
    {
        using var response = await _client.PostAsJsonAsync($"/api/local-node/configuration/releases/{digest}/install", new { });
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static void AssertBody(string expectedLiteral, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedLiteral), actual),
            $"The body differs from the complete literal: {expectedLiteral}");

    private string CatalogueHash() => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_store.ListInstalled(_tenant)))));

    private void Seed(string packKey, string version, int? interfaceVersion, IReadOnlyList<string>? exposes,
        params (string Key, PackContentKind Kind, string Json)[] items)
    {
        var seeds = items.Select(item => new PackSeedItem(item.Key, item.Kind, "1", item.Json,
            Cid.FromBytes(Encoding.UTF8.GetBytes(item.Json)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>(), Exposes: exposes, InterfaceVersion: interfaceVersion);
        _store.Commit(new PackInstallTransaction(_tenant, pack, new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));
        _store.Activate(_tenant, packKey, version);
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
