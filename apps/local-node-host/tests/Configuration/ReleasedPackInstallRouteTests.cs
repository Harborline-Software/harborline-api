using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-667: a Released package installs. Release (T-461) produced a provider-neutral document and
/// activation (T-644, T-460) consumed installed Active packs, and nothing joined them, so
/// <c>POST /configuration/prepare</c> could not resolve a candidate over a Released package. These run
/// the whole chain over the REAL exporter, the REAL install engine and the real SQLCipher packs
/// database: propose, save, check, release, install, prepare, activate.
/// </summary>
public sealed class ReleasedPackInstallRouteTests : IAsyncLifetime
{
    private const string RecordsEdit = """{"recordType":"invoice","fields":[{"name":"purchaseOrderNumber","kind":"identifier"}]}""";
    private const string FormsEdit = """{"formId":"invoice","sections":[{"id":"header","fields":["purchaseOrderNumber"]}]}""";
    private const string Rationale = "Capture the purchase order number on invoices.";
    private const string PackageKey = "acme.invoice-purchase-order";
    private const string Revision = "1.1.0";
    // The author states the kind. These are the transport's own names, and nothing in the api maps a
    // definition key to one of them — that is the invariant Only_the_producer_states_the_content_kind holds.
    private const string RecordsKind = "AssetTypeDefinition";
    private const string FormsKind = "FormDefinition";
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000667"));
    private static readonly DateTimeOffset Frozen = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationProposalStore _proposals = null!;
    private ConfigurationActivationTarget _activation = null!;
    private NodePrincipalSigner _node = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 67);
        _store = new DurablePackInstallStore(_db.Factory);
        var gate = TestPackGate.AllowAll();
        _activation = new ConfigurationActivationTarget(_db.Factory, _store, gate, new InMemoryPackInstallAudit());

        var seed = new byte[32];
        for (var index = 0; index < seed.Length; index++) seed[index] = (byte)(index + 67);
        _node = new NodePrincipalSigner(seed);
        _proposals = new ConfigurationProposalStore(_db.Factory, _activation, _node.Signer);

        var codec = new PackFileCodec();
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, TimeProvider.System);
        var installer = new PackInstaller(new Harborline.Api.Foundation.Packs.Verify.PackVerifier(new Ed25519Verifier(), codec),
            _store, new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        // The same trust surface the composition root builds: a Released package is verified against the
        // node's own roster root, not against a root this path invented for itself.
        var trustStore = HostedPackInstallApiEndpoint.BuildTrustStore(_node, NullLogger.Instance);
        var releases = new ReleasedPackInstaller(_proposals, exporter, installer, _node, new Ed25519Verifier(),
            trustStore, PackRevocationList.Empty);

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
        ConfigurationProposalRoutes.Map(_app, _proposals, activeTeam, gate, clock, NullLogger.Instance, releases);
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
        _node?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    /// <summary>
    /// Acceptance 1. One test from the release to the effective digest, with no hand conversion in the
    /// middle: what the author released is installed by its own digest, and the generation that becomes
    /// effective is resolved over the pack that release produced.
    /// </summary>
    [Fact]
    public async Task A_released_package_is_installed_prepared_and_activated_with_no_manual_step_between()
    {
        // Pin an explicit effective pointer first, so "effective" is a committed generation rather than
        // whatever the installed set happens to resolve to. Installing a pack then cannot move it, and the
        // digest this test ends at is the one the switch committed.
        var baseline = await PinEffectiveAsync();
        var releasedDigest = await ReleaseAsync();

        // Before installation the released package is invisible to preparation: it is a document, not a pack.
        using (var tooSoon = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute,
            new { expectedBaselineDigest = baseline, activePackageKeys = new[] { "acme.finance", PackageKey } }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSoon.StatusCode);
            var refused = await tooSoon.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("refused", refused.GetProperty("status").GetString());
            Assert.Equal("configuration-package-not-active",
                refused.GetProperty("refusals")[0].GetProperty("code").GetString());
        }

        var installed = await InstallAsync(releasedDigest);
        Assert.Equal("installed", installed.GetProperty("status").GetString());
        // Installing does not activate a configuration generation; the pointer is still the baseline.
        Assert.Equal(baseline, await EffectiveDigestAsync());
        Assert.Equal(PackageKey, installed.GetProperty("packKey").GetString());
        Assert.Equal(Revision, installed.GetProperty("version").GetString());

        // The installed pack carries exactly the edited definitions, under the kinds the author stated.
        var pack = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);
        Assert.Equal(PackLifecycleState.Active, pack.Lifecycle);
        Assert.Equal(
            new[] { ("forms/invoice", PackContentKind.FormDefinition), ("records/invoice", PackContentKind.AssetTypeDefinition) },
            pack.SeedItems.Select(item => (item.Key, item.Kind)).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());

        // Prepare names the released package as an active root and chooses it as the owner of both
        // definitions the finance pack also claims, then the switch makes that generation effective.
        using var prepared = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute, new
        {
            expectedBaselineDigest = baseline,
            activePackageKeys = new[] { "acme.finance", PackageKey },
            ownership = new[]
            {
                new { definitionKey = "records/invoice", packageKey = PackageKey },
                new { definitionKey = "forms/invoice", packageKey = PackageKey },
            },
        });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var candidate = (await prepared.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("preparing", candidate.GetProperty("status").GetString());
        var candidateDigest = candidate.GetProperty("candidateDigest").GetString()!;
        Assert.NotEqual(baseline, candidateDigest);

        using var activated = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.ActivateRoute, new
        {
            expectedBaselineDigest = baseline,
            candidateDigest,
            evidenceIntent = new { id = "intent-667", reason = "Activate the released purchase-order change." },
        });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var outcome = await activated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("effective", outcome.GetProperty("status").GetString());
        Assert.Equal(candidateDigest, outcome.GetProperty("effectiveDigest").GetString());
        Assert.Equal(candidateDigest, await EffectiveDigestAsync());
    }

    /// <summary>
    /// Acceptance 3. A tampered Released package is refused on THIS path, before anything is converted,
    /// exported or installed — not only on the release path that wrote it.
    /// </summary>
    [Fact]
    public async Task A_tampered_released_package_fails_signature_verification_on_the_installation_path()
    {
        var releasedDigest = await ReleaseAsync();

        // Edit one byte of the definition body inside the stored released document. The signature covers
        // the SHA-256 of these exact bytes, so the row now offers an artifact the signature never named.
        using (var context = _db.CreateContext())
        {
            var row = context.ReleasedPackages.Single();
            var document = Encoding.UTF8.GetString(row.Document);
            Assert.Contains("purchaseOrderNumber", document, StringComparison.Ordinal);
            row.Document = Encoding.UTF8.GetBytes(document.Replace("purchaseOrderNumber", "purchaseOrderNumbeR", StringComparison.Ordinal));
            context.SaveChanges();
        }

        using var refused = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Equal("configuration-release-missing",
            body.GetProperty("refusals")[0].GetProperty("code").GetString());
        // Nothing installed, so nothing could be prepared over: the tamper is terminal, not cosmetic.
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);

        // Installing under the digest the tampered bytes now hash to is refused on the signature itself:
        // the row is found, and the node's signature over the released artifact does not cover it.
        using var context2 = _db.CreateContext();
        var tampered = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(context2.ReleasedPackages.Single().Document));
        Assert.NotEqual(releasedDigest, tampered);
        using var byTamperedDigest = await _client.PostAsJsonAsync(InstallRoute(tampered), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byTamperedDigest.StatusCode);
        Assert.Equal("configuration-release-signature-invalid",
            (await byTamperedDigest.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);

        // Restore the document and re-point the row's own digest at it, so the only thing left wrong is
        // the signature itself. The Ed25519 verification is therefore on this path in its own right,
        // not merely a digest comparison that happens to disagree.
        using (var context = _db.CreateContext())
        {
            var row = context.ReleasedPackages.Single();
            var document = Encoding.UTF8.GetString(row.Document);
            row.Document = Encoding.UTF8.GetBytes(document.Replace("purchaseOrderNumbeR", "purchaseOrderNumber", StringComparison.Ordinal));
            var signature = JsonDocument.Parse(row.SignatureJson).RootElement.GetProperty("signature").GetString()!;
            var flipped = (signature[0] == 'A' ? 'B' : 'A') + signature[1..];
            row.SignatureJson = row.SignatureJson.Replace(signature, flipped, StringComparison.Ordinal);
            context.SaveChanges();
        }
        using var forged = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), OwnedByRelease());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forged.StatusCode);
        Assert.Equal("configuration-release-signature-invalid",
            (await forged.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);
    }

    /// <summary>
    /// Acceptance 2. The definition-key to content-kind mapping is stated in exactly one place — the
    /// producer's edit — and this fails if a second place states it.
    /// </summary>
    /// <remarks>
    /// The mechanism, because "stated in one place" is easy to assert and hard to enforce. A second
    /// statement can only be a rule that reads a definition key and produces a kind, whether as the
    /// answer, as an override or as a fallback. All three are caught behaviourally rather than by
    /// scanning for text:
    /// <list type="number">
    /// <item>Key independence: rename every definition key and keep the stated kinds, and the kinds the
    /// conversion produces are unchanged. A table consulted as the answer or as an override fails here.</item>
    /// <item>Kind fidelity: keep the definition keys and change the stated kinds, and the produced kinds
    /// follow the statement. A table consulted as an override fails here too, from the other side.</item>
    /// <item>No fallback: a stated kind this node's transport does not define is refused by name. A table
    /// consulted only when the stated kind is unusable would answer instead of refusing, and fail here.</item>
    /// </list>
    /// Together they say the produced kind is a function of the stated kind alone, which is what
    /// "stated in exactly one place" means operationally.
    /// </remarks>
    [Fact]
    public async Task Only_the_producer_states_the_content_kind_and_a_second_statement_would_fail_this()
    {
        await ReleaseAsync();
        var document = Encoding.UTF8.GetString(ReleasedDocument());
        var principal = _node.Signer.IssuerId.ToBase64Url();

        static IReadOnlyDictionary<string, PackContentKind> Kinds(string document, string principal) =>
            ReleasedPackConversion.ToExportRequest(Encoding.UTF8.GetBytes(document), principal)
                .Contents.ToDictionary(source => source.Key, source => source.Kind, StringComparer.Ordinal);

        var stated = Kinds(document, principal);
        Assert.Equal(PackContentKind.AssetTypeDefinition, stated["records/invoice"]);
        Assert.Equal(PackContentKind.FormDefinition, stated["forms/invoice"]);

        // (1) Key independence. The definition keys become names nothing could have a rule about; every
        // produced kind is the same one the document states.
        var renamed = Kinds(document
            .Replace("records/invoice", "zzz-first", StringComparison.Ordinal)
            .Replace("forms/invoice", "zzz-second", StringComparison.Ordinal), principal);
        Assert.Equal(PackContentKind.AssetTypeDefinition, renamed["zzz-first"]);
        Assert.Equal(PackContentKind.FormDefinition, renamed["zzz-second"]);
        Assert.Equal(stated.Values.Order().ToArray(), renamed.Values.Order().ToArray());

        // (2) Kind fidelity. The keys stay; the statements swap; the produced kinds swap with them.
        var swapped = Kinds(document
            .Replace($"\"contentKind\":\"{RecordsKind}\"", "\"contentKind\":\"@records@\"", StringComparison.Ordinal)
            .Replace($"\"contentKind\":\"{FormsKind}\"", $"\"contentKind\":\"{RecordsKind}\"", StringComparison.Ordinal)
            .Replace("\"contentKind\":\"@records@\"", $"\"contentKind\":\"{FormsKind}\"", StringComparison.Ordinal), principal);
        Assert.Equal(PackContentKind.FormDefinition, swapped["records/invoice"]);
        Assert.Equal(PackContentKind.AssetTypeDefinition, swapped["forms/invoice"]);

        // (3) No fallback. A kind the transport does not define is a named refusal, naming the definition;
        // nothing answers in its place.
        var unknown = Assert.Throws<ReleasedPackConversionException>(() => Kinds(
            document.Replace($"\"contentKind\":\"{FormsKind}\"", "\"contentKind\":\"InvoiceFormDefinition\"", StringComparison.Ordinal),
            principal));
        Assert.Equal("configuration-release-content-kind-unknown", unknown.Code);
        Assert.Equal("forms/invoice", unknown.Target);

        // And an item that states no kind at all is refused rather than classified from its key.
        var missing = Assert.Throws<ReleasedPackConversionException>(() => Kinds(
            document.Replace($"\"contentKind\":\"{FormsKind}\",", string.Empty, StringComparison.Ordinal), principal));
        Assert.Equal("configuration-release-content-kind-missing", missing.Code);
        Assert.Equal("forms/invoice", missing.Target);

        // And the entry point has nothing to fall back on either: an edit that states no kind is
        // refused where it is authored, so no released document can reach the converter without one.
        using var unstated = await _client.PutAsJsonAsync($"{Proposal("proposal-667")}/edits",
            new { definitionKey = "forms/invoice", packageKey = "acme.finance", bodyJson = FormsEdit });
        Assert.Equal(HttpStatusCode.BadRequest, unstated.StatusCode);
    }

    [Fact]
    public async Task Installing_an_unoffered_digest_refuses_and_installing_twice_is_the_same_pack()
    {
        using (var missing = await _client.PostAsJsonAsync(InstallRoute(new string('0', 64)), new { }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            Assert.Equal("configuration-release-missing",
                (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refusals")[0].GetProperty("code").GetString());
        }

        var releasedDigest = await ReleaseAsync();
        Assert.Equal("installed", (await InstallAsync(releasedDigest)).GetProperty("status").GetString());
        // The same released bytes export the same pack coordinate, so a repeat is the same identity
        // rather than a second one; it never leaves two packs claiming the same definitions.
        using var again = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), OwnedByRelease());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);
    }

    private static string InstallRoute(string digest) => $"/api/local-node/configuration/releases/{digest}/install";

    private static object OwnedByRelease() => new
    {
        ownership = new[]
        {
            new { definitionKey = "records/invoice", packageKey = PackageKey },
            new { definitionKey = "forms/invoice", packageKey = PackageKey },
        },
    };

    private async Task<JsonElement> InstallAsync(string digest)
    {
        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), OwnedByRelease());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        return body;
    }

    private byte[] ReleasedDocument()
    {
        using var context = _db.CreateContext();
        return context.ReleasedPackages.AsNoTracking().Single().Document;
    }

    /// <summary>Runs T-461's path end to end and returns the Released package's own artifact digest.</summary>
    private async Task<string> ReleaseAsync()
    {
        const string proposalId = "proposal-667";
        using (var started = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId }))
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await AutosaveAsync(proposalId, "records/invoice", RecordsEdit, RecordsKind);
        await AutosaveAsync(proposalId, "forms/invoice", FormsEdit, FormsKind);
        using (var saved = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/versions", new { rationale = Rationale }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var checked_ = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/checks", new { receiptId = "receipt-667" }))
            Assert.Equal(HttpStatusCode.OK, checked_.StatusCode);
        using var released = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/release",
            new { ordinal = 1, packageKey = PackageKey, revision = Revision });
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(released.StatusCode == HttpStatusCode.OK, body.ToString());
        return body.GetProperty("releasedPackage").GetProperty("digest").GetString()!;
    }

    private static string Proposal(string proposalId) => $"/api/local-node/configuration/proposals/{proposalId}";

    private async Task AutosaveAsync(string proposalId, string definitionKey, string bodyJson, string contentKind)
    {
        using var response = await _client.PutAsJsonAsync($"{Proposal(proposalId)}/edits",
            new { definitionKey, packageKey = "acme.finance", bodyJson, contentKind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Commits one effective generation over the seeded pack, and returns its digest.</summary>
    private async Task<string> PinEffectiveAsync()
    {
        var derived = await EffectiveDigestAsync();
        using var prepared = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute,
            new { expectedBaselineDigest = derived, activePackageKeys = new[] { "acme.finance" } });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var candidateDigest = (await prepared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidateDigest").GetString()!;
        using var activated = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.ActivateRoute, new
        {
            expectedBaselineDigest = derived,
            candidateDigest,
            evidenceIntent = new { id = "intent-667-baseline", reason = "Pin the baseline generation." },
        });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        return (await activated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effectiveDigest").GetString()!;
    }

    private async Task<string> EffectiveDigestAsync()
    {
        using var response = await _client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("digest").GetString()!;
    }

    private void Seed(string packKey, string version, string[] contentKeys)
    {
        var seeds = contentKeys.Select(key => new PackSeedItem(key, PackContentKind.FormDefinition, "1",
            $$"""{"id":"{{key}}","pack":"{{packKey}}"}""", Cid.FromBytes(Encoding.UTF8.GetBytes(key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
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
