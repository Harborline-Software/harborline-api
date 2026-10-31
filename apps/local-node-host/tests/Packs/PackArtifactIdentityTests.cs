using System.Text;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackArtifactIdentityTests
{
    [Theory]
    [InlineData("equal")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    public async Task Pack_routes_preserve_200_preview_check_and_truthful_200_or_422_install_with_private_refusals(string kind)
    {
        using var fixture = new Fixture();
        using var services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var team = new RouteTeam(new TeamContext(new TeamId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001098")),
            "Identity", services, new RouteClock()));
        var tenant = NodeTenant.Resolve(team);
        var context = fixture.Admitted with { Tenant = tenant };
        PlatformPackTestPreload.Activate(fixture.Store, tenant);
        var bytes = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(bytes, context)).Installed);
        var store = fixture.Store;
        if (kind == "unknown")
        {
            var legacy = fixture.Store.GetVersion(tenant, Fixture.Pack, "1.0.0")! with { ArtifactIdentity = null };
            store = new InMemoryPackInstallStore();
            PlatformPackTestPreload.Activate(store, tenant);
            store.Commit(new PackInstallTransaction(tenant, legacy, fixture.Store.GetWatermark(tenant, Fixture.Pack)!, []));
        }
        if (kind == "conflict") bytes = await fixture.ResignAsync(bytes, subject => subject with
        { Manifest = subject.Manifest with { DisplayName = "private-display-marker" } });
        var installer = fixture.CreateInstaller(store);
        var builder = WebApplication.CreateBuilder();
        TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.Features.Set(DesktopPlaneRequestFeature.Instance); await next(http); });
        var projector = new PackSeedProjector(store, services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: new RouteClock());
        PackInstallRoutes.Map(app, installer, store, fixture.Trust, new PackRevocationList([], Fixture.Now), team,
            TestPackGate.AllowAll(), new RouteClock(), NullLogger.Instance, fixture.Keys.PrincipalId.ToBase64Url(), projector);
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            var auditBefore = fixture.Audit.Query(tenant).Count();
            var before = Snapshot();
            foreach (var route in new[] { "/api/local-node/packs/preview", "/api/local-node/packs/check" })
            {
                using var preview = await PostAsync(route);
                Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
                using var document = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
                Assert.Equal(kind == "equal" ? "AlreadyInstalled" : "Refused", document.RootElement.GetProperty("verdict").GetString());
                Assert.Equal(auditBefore, fixture.Audit.Query(tenant).Count());
            }
            using var response = await PostAsync("/api/local-node/packs/install");
            var raw = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(raw);
            var body = result.RootElement;
            Assert.Equal(kind == "equal" ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(kind == "equal", body.GetProperty("installed").GetBoolean());
            Assert.Equal(kind == "equal" ? "Unchanged" : "Refused", body.GetProperty("action").GetString());
            if (kind != "equal")
            {
                Assert.Equal("install", body.GetProperty("stage").GetString());
                Assert.Equal(kind == "conflict" ? "pack.install.refused.same_version_conflict" : "pack.install.refused.same_version_identity_unknown",
                    Assert.Single(body.GetProperty("refusalCodes").EnumerateArray()).GetString());
                var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
                Assert.Equal("/", refusal.GetProperty("pointer").GetString());
                Assert.True(!refusal.TryGetProperty("target", out var target) || target.ValueKind == JsonValueKind.Null);
            }
            Assert.DoesNotContain("private-display-marker", raw, StringComparison.Ordinal);
            Assert.Equal(before, Snapshot());
            Assert.Equal(auditBefore + 1, fixture.Audit.Query(tenant).Count());
            Assert.Equal(kind == "equal" ? "Unchanged" : "Refused", fixture.Audit.Query(tenant).Last().Action.ToString());

            async Task<HttpResponseMessage> PostAsync(string route)
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return await client.PostAsync(route, content);
            }
            string Snapshot() => JsonSerializer.Serialize(new
            {
                Versions = store.ListInstalled(tenant), Watermark = store.GetWatermark(tenant, Fixture.Pack),
                Overrides = store.GetOverrides(tenant, Fixture.Pack), Ownership = store.GetKeyOwnership(tenant),
                Admissions = ((IPackProjectionAdmissionStore)store).ListIncompleteProjectionAdmissions(),
            });
        }
        finally { await app.StopAsync(); }
    }

    private sealed class RouteClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Fixture.Now; }
    private sealed class RouteTeam(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    [Fact]
    public async Task Complete_frame_matches_independent_literal_valid_signed_vector()
    {
        using var keys = KeyPair.FromSeed(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Assert.Equal("11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo", keys.PrincipalId.ToBase64Url());
        var manifest = new PackManifest("test.t1098-vector", "1.0.0", "Vector", "Identity vector",
            PackScopeTier.Horizontal, [], [], []);
        var subject = new PackSignatureSubject(manifest, 7);
        var codec = new PackFileCodec();
        var envelope = await new Ed25519Signer(keys).SignAsync(subject, Fixture.Now, Guid.NewGuid());
        var bytes = codec.Encode(new PackFile(envelope, []));
        var trust = new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, keys.PrincipalId, 7, TrustRootStatus.Current)]);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        Assert.Equal("Verified", verifier.Verify(bytes, trust).Verdict.ToString());
        var store = new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate(store, Fixture.Tenant);
        var installer = new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var context = Fixture.Context(trust);
        Assert.True((await installer.InstallAsync(bytes, context)).Installed);
        const string literal = """{"domain":"harborline.pack-install-artifact-identity","signer":"11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo","subject":{"Epoch":7,"Manifest":{"CapabilityRequirements":[],"Contents":[],"Dcp":null,"Dependencies":[],"Description":"Identity vector","Key":"test.t1098-vector","Name":"Vector","ProviderSlot":null,"RenamedFrom":null,"ScopeTier":0,"Version":"1.0.0"}},"version":1}""";
        var evidence = Assert.IsType<string>(store.GetVersion(Fixture.Tenant, "test.t1098-vector", "1.0.0")!.ArtifactIdentity);
        Assert.Equal(Encoding.UTF8.GetBytes(literal), Convert.FromBase64String(evidence));
    }

    [Fact]
    public async Task Fresh_issuance_and_transport_order_preserve_identity_and_select_only_Unchanged()
    {
        using var fixture = new Fixture();
        var first = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(first, fixture.Admitted)).Installed);
        var before = fixture.Snapshot();
        var signedAgain = await fixture.ResignAsync(first, item => item, issuedAt: Fixture.Now.AddSeconds(1));
        var transport = JsonNode.Parse(signedAgain)!.AsObject();
        var envelope = transport["envelope"]!.AsObject();
        transport["envelope"] = new JsonObject(envelope.Reverse().Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())));
        var reordered = Encoding.UTF8.GetBytes(transport.ToJsonString());
        Assert.False(signedAgain.AsSpan().SequenceEqual(reordered));
        fixture.RequireVerified(reordered);
        Assert.Equal("AlreadyInstalled", fixture.Installer.Preview(reordered, fixture.Admitted).Verdict.ToString());
        Assert.Equal("AlreadyInstalled", fixture.Installer.Check(reordered, fixture.Admitted).Verdict.ToString());
        var auditBefore = fixture.Audit.Query(Fixture.Tenant).Count();
        var retry = await fixture.Installer.InstallAsync(reordered, fixture.Admitted);
        Assert.True(retry.Installed);
        Assert.Equal("Unchanged", retry.Action.ToString());
        Assert.False(retry.BrokeGlass);
        Assert.Empty(retry.RefusalCodes);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal("Unchanged", Assert.Single(fixture.Audit.Query(Fixture.Tenant).Skip(auditBefore)).Action.ToString());
    }

    [Fact]
    public async Task Occupied_equal_raw_commit_cannot_report_unaudited_success()
    {
        using var fixture = new Fixture();
        var bytes = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(bytes, fixture.Admitted)).Installed);
        var existing = fixture.Store.GetVersion(Fixture.Tenant, Fixture.Pack, "1.0.0")!;
        var before = fixture.Snapshot();
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Commit(new PackInstallTransaction(
            Fixture.Tenant, existing, fixture.Store.GetWatermark(Fixture.Tenant, Fixture.Pack)!, [])));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Omitted_and_explicit_null_rename_member_are_equal_after_real_verification()
    {
        using var fixture = new Fixture();
        var explicitNull = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(explicitNull, fixture.Admitted)).Installed);
        var node = JsonNode.Parse(explicitNull)!;
        Assert.True(node["envelope"]!["payload"]!["manifest"]!.AsObject().Remove("renamedFrom"));
        var omitted = Encoding.UTF8.GetBytes(node.ToJsonString());
        fixture.RequireVerified(omitted);
        var before = fixture.Snapshot();
        Assert.Equal("Unchanged", (await fixture.Installer.InstallAsync(omitted, fixture.Admitted)).Action.ToString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Exact_tenant_key_case_and_version_coordinate_isolation_never_guesses_equality()
    {
        using var fixture = new Fixture();
        var bytes = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(bytes, fixture.Admitted)).Installed);
        var anotherTenant = new TenantId("bbbbbbbb-0000-0000-0000-000000001098");
        PlatformPackTestPreload.Activate(fixture.Store, anotherTenant);
        var firstTenant = fixture.Snapshot();
        Assert.Equal("Installed", (await fixture.Installer.InstallAsync(bytes, fixture.Admitted with { Tenant = anotherTenant })).Action.ToString());
        Assert.Equal(firstTenant, fixture.Snapshot());
        foreach (var coordinate in new[] { "key-case", "version-build" })
        {
            var changed = await fixture.ResignAsync(bytes, subject => subject with { Manifest = coordinate == "key-case"
                ? subject.Manifest with { Key = "test.T1098-identity" }
                : subject.Manifest with { Version = "1.0.0+separate" } });
            fixture.RequireVerified(changed);
            var temporary = new InMemoryPackInstallStore();
            PlatformPackTestPreload.Activate(temporary, Fixture.Tenant);
            var accepted = await fixture.CreateInstaller(temporary).InstallAsync(changed, fixture.Admitted);
            Assert.True(accepted.Installed);
            var candidate = temporary.GetVersion(Fixture.Tenant, accepted.PackKey, accepted.Version)!;
            var result = fixture.Store.Commit(new PackInstallTransaction(Fixture.Tenant, candidate,
                temporary.GetWatermark(Fixture.Tenant, candidate.PackKey)!, []));
            Assert.Equal("Written", result.Disposition.ToString());
            Assert.Null(result.SelectedAudit);
        }
        Assert.Equal(3, fixture.Store.ListInstalled(Fixture.Tenant).Count(pack => pack.PackKey.StartsWith("test.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("display")]
    [InlineData("description")]
    [InlineData("tagline")]
    [InlineData("category")]
    [InlineData("icon")]
    [InlineData("scope")]
    [InlineData("requirements")]
    [InlineData("provider")]
    [InlineData("dependency")]
    [InlineData("content-reference")]
    [InlineData("content-version")]
    [InlineData("exposes")]
    [InlineData("interface")]
    [InlineData("exposed-facts")]
    [InlineData("rename-empty")]
    [InlineData("content-order")]
    [InlineData("dcp")]
    public async Task Any_complete_signed_subject_difference_refuses_same_version(string change)
    {
        using var fixture = new Fixture();
        var platform = new PackPlatformCompatibility("1.0.0",
            [new PackProjectorCase(PackContentKind.FormDefinition, ["packs.test.identity"])]);
        var installer = fixture.CreateInstaller(fixture.Store, platform);
        var target = await fixture.ResignAsync(await fixture.ArtifactAsync(keys: ["acme.target", "acme.other-target"]),
            subject => subject with { Manifest = subject.Manifest with { Key = "test.t1098-target" } });
        Assert.True((await installer.InstallAsync(target, fixture.Admitted)).Installed);
        var first = await fixture.ResignAsync(await fixture.ArtifactAsync(keys: ["acme.alpha", "acme.beta"]),
            subject => subject with { Manifest = subject.Manifest with
            {
                Dependencies = [new PackDependencyRef("test.t1098-target", "1.0.0")],
                ContentReferences = [new PackContentReferenceEdge("acme.alpha", PackContentKind.FormDefinition,
                    "test.t1098-target", "acme.target", PackContentKind.FormDefinition, PackFeatureEdgeRelation.References)],
                Exposes = ["acme.alpha"], InterfaceVersion = 1,
                ExposedDefinitions = [new PackExposedDefinition("acme.alpha", "1.0.0", "sha256:" + new string('a', 64))],
            } });
        fixture.RequireVerified(first);
        Assert.True((await installer.InstallAsync(first, fixture.Admitted)).Installed);
        var before = fixture.Snapshot();
        var changed = await fixture.ResignAsync(first, subject => subject with
        {
            Manifest = change switch
            {
                "name" => subject.Manifest with { Name = "Different name" },
                "display" => subject.Manifest with { DisplayName = "Different card" },
                "description" => subject.Manifest with { Description = "Different description" },
                "tagline" => subject.Manifest with { Tagline = "Different tagline" },
                "category" => subject.Manifest with { Category = "Different category" },
                "icon" => subject.Manifest with { IconRef = subject.Manifest.Contents[0].ContentAddress },
                "scope" => subject.Manifest with { ScopeTier = PackScopeTier.Vertical },
                "requirements" => subject.Manifest with { CapabilityRequirements = ["packs.test.identity"] },
                "provider" => subject.Manifest with { ProviderSlot = "test-provider" },
                "dependency" => subject.Manifest with { Dependencies = [new PackDependencyRef("test.t1098-target", "0.9.0")] },
                "content-reference" => subject.Manifest with { ContentReferences = [subject.Manifest.ContentReferences![0] with { ToContentKey = "acme.other-target" }] },
                "content-version" => subject.Manifest with { Contents = [subject.Manifest.Contents[0] with { Version = "1.0.1" }, subject.Manifest.Contents[1]] },
                "exposes" => subject.Manifest with { Exposes = ["acme.alpha", "acme.beta"] },
                "interface" => subject.Manifest with { InterfaceVersion = 2 },
                "exposed-facts" => subject.Manifest with { ExposedDefinitions = [subject.Manifest.ExposedDefinitions![0] with { Digest = "sha256:" + new string('b', 64) }] },
                "rename-empty" => subject.Manifest with { RenamedFrom = [] },
                "content-order" => subject.Manifest with { Contents = subject.Manifest.Contents.Reverse().ToArray() },
                "dcp" => subject.Manifest with { Dcp = null },
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            },
        }, removeDcp: change == "dcp");
        fixture.RequireVerified(changed);
        var originalFile = fixture.Codec.TryDecode(first)!;
        var changedFile = fixture.Codec.TryDecode(changed)!;
        Assert.Equal(originalFile.Contents, changedFile.Contents);
        Assert.Equal(originalFile.Envelope!.Payload.Manifest.Contents.Select(item => item.ContentAddress.ToString()).Order(StringComparer.Ordinal),
            changedFile.Envelope!.Payload.Manifest.Contents.Select(item => item.ContentAddress.ToString()).Order(StringComparer.Ordinal));
        // A fresh real store admits the signed variant under the same eligible trust/platform facts.
        var unoccupied = new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate(unoccupied, Fixture.Tenant);
        var control = fixture.CreateInstaller(unoccupied, platform);
        Assert.True((await control.InstallAsync(target, fixture.Admitted)).Installed);
        var positive = await control.InstallAsync(changed, fixture.Admitted);
        Assert.True(positive.Installed, string.Join(",", positive.RefusalCodes));
        Assert.Equal(new[] { "pack.install.refused.same_version_conflict" }, installer.Preview(changed, fixture.Admitted).RefusalCodes);
        var outcome = await installer.InstallAsync(changed, fixture.Admitted);
        Assert.False(outcome.Installed);
        Assert.Equal(new[] { "pack.install.refused.same_version_conflict" }, outcome.RefusalCodes);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    public async Task Occupied_check_preserves_all_current_platform_refusals_and_identity_cofindings(string kind)
    {
        using var fixture = new Fixture();
        var bytes = await fixture.ResignAsync(await fixture.ArtifactAsync(), subject => subject with
        { Manifest = subject.Manifest with { CapabilityRequirements = ["packs.test.first", "packs.test.second"] } });
        fixture.RequireVerified(bytes);
        var available = new PackPlatformCompatibility("1.0.0",
            [new PackProjectorCase(PackContentKind.FormDefinition, ["packs.test.first", "packs.test.second"])]);
        var initial = fixture.CreateInstaller(fixture.Store, available);
        Assert.True((await initial.InstallAsync(bytes, fixture.Admitted)).Installed);
        var store = fixture.Store;
        if (kind == "unknown")
        {
            var legacy = store.GetVersion(Fixture.Tenant, Fixture.Pack, "1.0.0")! with { ArtifactIdentity = null };
            store = new InMemoryPackInstallStore();
            PlatformPackTestPreload.Activate(store, Fixture.Tenant);
            store.Commit(new PackInstallTransaction(Fixture.Tenant, legacy, fixture.Store.GetWatermark(Fixture.Tenant, Fixture.Pack)!, []));
        }
        if (kind == "conflict") bytes = await fixture.ResignAsync(bytes,
            subject => subject with { Manifest = subject.Manifest with { Name = "Changed signed name" } });
        fixture.RequireVerified(bytes);
        var current = fixture.CreateInstaller(store, new PackPlatformCompatibility("1.0.0", []));
        var before = JsonSerializer.Serialize(new { Installed = store.ListInstalled(Fixture.Tenant),
            Watermark = store.GetWatermark(Fixture.Tenant, Fixture.Pack), Overrides = store.GetOverrides(Fixture.Tenant, Fixture.Pack),
            Ownership = store.GetKeyOwnership(Fixture.Tenant) });
        var auditCount = fixture.Audit.Query(Fixture.Tenant).Count();
        var check = current.Check(bytes, fixture.Admitted);
        Assert.Equal("Refused", check.Verdict.ToString());
        Assert.Equal(new[] { "packs.test.first", "packs.test.second" }, check.UnmetPlatformRequirements.Select(item => item.Capability));
        var identityCode = kind == "conflict" ? "pack.install.refused.same_version_conflict" : "pack.install.refused.same_version_identity_unknown";
        Assert.Equal(kind == "equal" ? new[] { "pack.install.refused.missing_platform_capability" }
            : new[] { "pack.install.refused.missing_platform_capability", identityCode }, check.RefusalCodes);
        Assert.Equal(kind == "equal" ? 2 : 3, check.Refusals.Count);
        Assert.All(check.Refusals, refusal => { Assert.Equal("/", refusal.Pointer); Assert.Null(refusal.Target); });
        Assert.Equal(new[] { "pack.install.refused.missing_platform_capability" }, current.Preview(bytes, fixture.Admitted).RefusalCodes);
        Assert.Equal(auditCount, fixture.Audit.Query(Fixture.Tenant).Count());
        var outcome = await current.InstallAsync(bytes, fixture.Admitted);
        Assert.False(outcome.Installed);
        Assert.Equal(new[] { "pack.install.refused.missing_platform_capability" }, outcome.RefusalCodes);
        Assert.Equal(before, JsonSerializer.Serialize(new { Installed = store.ListInstalled(Fixture.Tenant),
            Watermark = store.GetWatermark(Fixture.Tenant, Fixture.Pack), Overrides = store.GetOverrides(Fixture.Tenant, Fixture.Pack),
            Ownership = store.GetKeyOwnership(Fixture.Tenant) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independently_trusted_different_signer_or_epoch_conflicts(bool epochOnly)
    {
        using var fixture = new Fixture();
        using var other = KeyPair.Generate();
        var first = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(first, fixture.Admitted)).Installed);
        var signer = epochOnly ? fixture.Keys : other;
        var epoch = epochOnly ? 2 : 1;
        var trust = new InMemoryPackTrustStore([
            new PackTrustRoot(TrustScope.OwnRoster, fixture.Keys.PrincipalId, 1, TrustRootStatus.Current),
            new PackTrustRoot(TrustScope.OwnRoster, signer.PrincipalId, epoch, TrustRootStatus.Current)]);
        var file = fixture.Codec.TryDecode(first)!;
        var envelope = await new Ed25519Signer(signer).SignAsync(file.Envelope!.Payload with { Epoch = epoch }, Fixture.Now, Guid.NewGuid());
        var changed = fixture.Codec.Encode(file with { Envelope = envelope });
        Assert.Equal("Verified", fixture.Verifier.Verify(changed, trust).Verdict.ToString());
        var before = fixture.Snapshot();
        var refused = await fixture.Installer.InstallAsync(changed, Fixture.Context(trust));
        Assert.False(refused.Installed);
        Assert.Equal(new[] { "pack.install.refused.same_version_conflict" }, refused.RefusalCodes);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("base64")]
    [InlineData("base64-space")]
    [InlineData("utf8")]
    [InlineData("bom")]
    [InlineData("version")]
    [InlineData("version-type")]
    [InlineData("domain")]
    [InlineData("duplicate")]
    [InlineData("duplicate-subject")]
    [InlineData("missing-epoch")]
    [InlineData("unknown-subject")]
    [InlineData("signer")]
    [InlineData("row-epoch")]
    [InlineData("row-key")]
    public async Task Incomplete_or_malformed_stored_evidence_refuses_without_backfill(string kind)
    {
        using var fixture = new Fixture();
        var bytes = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(bytes, fixture.Admitted)).Installed);
        var original = fixture.Store.GetVersion(Fixture.Tenant, Fixture.Pack, "1.0.0")!;
        var frame = Encoding.UTF8.GetString(Convert.FromBase64String(original.ArtifactIdentity!));
        var node = JsonNode.Parse(frame)!.AsObject();
        switch (kind)
        {
            case "version": node["version"] = 2; break;
            case "version-type": node["version"] = "1"; break;
            case "domain": node["domain"] = "other"; break;
            case "missing-epoch": node["subject"]!.AsObject().Remove("Epoch"); break;
            case "unknown-subject": node["subject"]!["Future"] = "unmodeled"; break;
            case "signer": node["signer"] = node["signer"]!.GetValue<string>() + "="; break;
            case "row-epoch": node["subject"]!["Epoch"] = 2; break;
            case "row-key": node["subject"]!["Manifest"]!["Key"] = "acme.other"; break;
        }
        var evidence = kind switch
        {
            "missing" => null,
            "empty" => "",
            "base64" => "%%%",
            "base64-space" => original.ArtifactIdentity + " ",
            "utf8" => Convert.ToBase64String(new byte[] { 0xff }),
            "bom" => Convert.ToBase64String(Encoding.UTF8.GetBytes("\uFEFF" + frame)),
            "duplicate" => Convert.ToBase64String(Encoding.UTF8.GetBytes(frame.Replace("{", "{\"version\":1,", StringComparison.Ordinal))),
            "duplicate-subject" => Convert.ToBase64String(Encoding.UTF8.GetBytes(frame.Replace("\"Epoch\":1", "\"Epoch\":1,\"Epoch\":1", StringComparison.Ordinal))),
            _ => Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString())),
        };
        var legacyStore = new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate(legacyStore, Fixture.Tenant);
        var legacy = original with { ArtifactIdentity = evidence };
        legacyStore.Commit(new PackInstallTransaction(Fixture.Tenant, legacy,
            fixture.Store.GetWatermark(Fixture.Tenant, Fixture.Pack)!, []));
        var installer = fixture.CreateInstaller(legacyStore);
        var before = JsonSerializer.Serialize(legacyStore.ListInstalled(Fixture.Tenant));
        var refused = await installer.InstallAsync(bytes, fixture.Admitted);
        Assert.False(refused.Installed);
        Assert.Equal(new[] { "pack.install.refused.same_version_identity_unknown" }, refused.RefusalCodes);
        Assert.Equal(before, JsonSerializer.Serialize(legacyStore.ListInstalled(Fixture.Tenant)));
        Assert.Equal(evidence, legacyStore.GetVersion(Fixture.Tenant, Fixture.Pack, "1.0.0")!.ArtifactIdentity);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Active")]
    [InlineData("Inactive")]
    [InlineData("Superseded")]
    public async Task Every_lifecycle_preserves_existing_rows_overrides_ownership_and_time(string lifecycle)
    {
        using var fixture = new Fixture();
        var bytes = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(bytes, fixture.Admitted)).Installed);
        if (lifecycle != "Draft") Assert.True((await fixture.Installer.ActivateAsync(fixture.Admitted, Fixture.Pack, "1.0.0")).Activated);
        if (lifecycle == "Inactive") Assert.True((await fixture.Installer.DeactivateAsync(fixture.Admitted, Fixture.Pack, "1.0.0")).Deactivated);
        if (lifecycle == "Superseded")
        {
            Assert.True((await fixture.Installer.InstallAsync(await fixture.ArtifactAsync("2.0.0"), fixture.Admitted)).Installed);
            Assert.True((await fixture.Installer.ActivateAsync(fixture.Admitted, Fixture.Pack, "2.0.0")).Activated);
        }
        fixture.Store.SaveOverride(Fixture.Tenant, Fixture.Pack, new PackTenantOverride("acme.alpha", JsonNode.Parse("""{"witness":"preserve"}""")!));
        fixture.Store.RecordKeyOwnership(Fixture.Tenant, "acme.alpha", Fixture.Pack);
        Assert.Equal(lifecycle, fixture.Store.GetVersion(Fixture.Tenant, Fixture.Pack, "1.0.0")!.Lifecycle.ToString());
        var before = fixture.Snapshot();
        var outcome = await fixture.Installer.InstallAsync(bytes, fixture.Admitted);
        Assert.Equal("Unchanged", outcome.Action.ToString());
        Assert.False(outcome.BrokeGlass);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Higher_version_chain_or_swap_rekeys_once_and_identical_retry_never_replays(bool swap)
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.Installer.InstallAsync(await fixture.ArtifactAsync(keys: ["acme.alpha", "acme.beta"]), fixture.Admitted)).Installed);
        fixture.Store.SaveOverride(Fixture.Tenant, Fixture.Pack, new PackTenantOverride("acme.alpha", new JsonObject { ["title"] = "overlay-A" }));
        fixture.Store.SaveOverride(Fixture.Tenant, Fixture.Pack, new PackTenantOverride("acme.beta", new JsonObject { ["title"] = "overlay-B" }));
        PackContentRename[] map = swap ? [new("acme.alpha", "acme.beta"), new("acme.beta", "acme.alpha")]
            : [new("acme.beta", "acme.alpha"), new("acme.gamma", "acme.beta")];
        var next = await fixture.ArtifactAsync("2.0.0", swap ? ["acme.alpha", "acme.beta"] : ["acme.beta", "acme.gamma"], map);
        Assert.True((await fixture.Installer.InstallAsync(next, fixture.Admitted)).Installed);
        var actual = fixture.Store.GetOverrides(Fixture.Tenant, Fixture.Pack).OrderBy(item => item.ContentKey, StringComparer.Ordinal).ToArray();
        Assert.Equal(swap ? new[] { "acme.alpha", "acme.beta" } : new[] { "acme.beta", "acme.gamma" }, actual.Select(item => item.ContentKey));
        Assert.Equal(swap ? new[] { "overlay-B", "overlay-A" } : new[] { "overlay-A", "overlay-B" }, actual.Select(item => item.OverlayPatch["title"]!.GetValue<string>()));
        var before = fixture.Snapshot();
        Assert.Equal("Unchanged", (await fixture.Installer.InstallAsync(next, fixture.Admitted)).Action.ToString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Identical_equality_never_bypasses_current_signature_trust_revocation_or_authorization()
    {
        using var fixture = new Fixture();
        var bytes = await fixture.ArtifactAsync();
        Assert.True((await fixture.Installer.InstallAsync(bytes, fixture.Admitted)).Installed);
        var before = fixture.Snapshot();
        var tampered = fixture.Codec.TryDecode(bytes)!;
        var invalid = fixture.Codec.Encode(tampered with { Envelope = tampered.Envelope! with
        { Payload = tampered.Envelope.Payload with { Manifest = tampered.Envelope.Payload.Manifest with { Name = "tampered" } } } });
        Assert.False((await fixture.Installer.InstallAsync(invalid, fixture.Admitted)).Installed);
        var untrusted = fixture.Admitted with { TrustStore = new InMemoryPackTrustStore([]) };
        Assert.False((await fixture.Installer.InstallAsync(bytes, untrusted)).Installed);
        var revoked = fixture.Admitted with { Revocation = new PackRevocationList([new PackRevokedKey(fixture.Keys.PrincipalId, 1)], Fixture.Now) };
        Assert.Equal(new[] { "pack.install.refused.revoked" }, (await fixture.Installer.InstallAsync(bytes, revoked)).RefusalCodes);
        var denied = new PackInstaller(fixture.Verifier, fixture.Store, new WorkflowRefusingPackContentAdmission(), fixture.Audit, TestAuthorization.Gate(false));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => denied.InstallAsync(bytes, fixture.Admitted));
        Assert.Equal(before, fixture.Snapshot());
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        internal static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000001098");
        internal const string Pack = "test.t1098-identity";
        internal KeyPair Keys { get; } = KeyPair.Generate();
        internal PackFileCodec Codec { get; } = new();
        internal PackVerifier Verifier { get; }
        internal InMemoryPackInstallStore Store { get; } = new();
        internal InMemoryPackInstallAudit Audit { get; } = new();
        internal InMemoryPackTrustStore Trust { get; }
        internal PackInstaller Installer { get; }
        internal PackInstallContext Admitted => Context(Trust);
        internal Fixture()
        {
            Trust = new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, Keys.PrincipalId, 1, TrustRootStatus.Current)]);
            Verifier = new PackVerifier(new Ed25519Verifier(), Codec);
            PlatformPackTestPreload.Activate(Store, Tenant);
            Installer = CreateInstaller(Store);
        }
        internal static PackInstallContext Context(IPackTrustStore trust) => new(Tenant, trust,
            new PackRevocationList([], Now), AdmittedInstant.FromRecordedAct(Now), TimeSpan.FromDays(30), Principal: "test-operator");
        internal PackInstaller CreateInstaller(InMemoryPackInstallStore store, IPackPlatformCompatibility? platform = null) => new(Verifier, store,
            new WorkflowRefusingPackContentAdmission(), Audit, TestAuthorization.AllowGate(), platform);
        internal string Snapshot() => JsonSerializer.Serialize(new
        {
            Versions = Store.ListInstalled(Tenant), Active = Store.GetActive(Tenant, Pack),
            Watermark = Store.GetWatermark(Tenant, Pack), Overrides = Store.GetOverrides(Tenant, Pack),
            Ownership = Store.GetKeyOwnership(Tenant), Admissions = ((IPackProjectionAdmissionStore)Store).ListIncompleteProjectionAdmissions(),
        });
        internal void RequireVerified(byte[] bytes) => Assert.Equal("Verified", Verifier.Verify(bytes, Trust).Verdict.ToString());
        internal async Task<byte[]> ArtifactAsync(string version = "1.0.0", string[]? keys = null, PackContentRename[]? map = null)
        {
            var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), Codec, new Clock());
            var result = await exporter.ExportAsync(new PackExportRequest(Pack, version, "Identity", "Complete identity",
                PackScopeTier.Horizontal, (keys ?? ["acme.alpha"]).Select(key => new PackContentSource(key,
                    PackContentKind.FormDefinition, "1.0.0", new JsonObject { ["title"] = "Seed" })).ToArray(),
                [], [], 1, RenamedFrom: map, Dcp: DomainComplianceProfile.General("test-author")), new Ed25519Signer(Keys));
            Assert.True(result.Succeeded, string.Join(",", result.Validation.Errors.Select(item => item.Code)));
            var bytes = Assert.IsType<byte[]>(result.FileBytes);
            RequireVerified(bytes);
            return bytes;
        }
        internal async Task<byte[]> ResignAsync(byte[] bytes, Func<PackSignatureSubject, PackSignatureSubject> change,
            DateTimeOffset? issuedAt = null, bool removeDcp = false)
        {
            var file = Codec.TryDecode(bytes)!;
            var signed = await new Ed25519Signer(Keys).SignAsync(change(file.Envelope!.Payload), issuedAt ?? Now, Guid.NewGuid());
            return Codec.Encode(file with { Envelope = signed, Dcp = removeDcp ? null : file.Dcp });
        }
        public void Dispose() => Keys.Dispose();
        private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }
}
