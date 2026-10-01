using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Route-layer PBAC enforcement for the <c>/packs/*</c> surface (council A-1): <c>packages:author</c> gates
/// export/verify; <c>packages:operate</c> gates preview/install/activate/deactivate/installed. The rail is a
/// reflection — these tests prove the SERVER refuses fail-closed (403 + a stable code) when the permission
/// is absent, so a crafted call cannot bypass the split, AND that the author/operate boundary is real (an
/// author-only principal is denied the operate verbs).
/// <para>
/// Ticket 205 slice 3: each act now resolves at its POINT OF USE through
/// <see cref="AuthorizationGate"/> with the pack it addresses as its record target, so a grant scoped
/// to a DIFFERENT pack refuses while the right scope passes. The routes that address the install rather
/// than one pack carry no record target and are admitted only because their operation is declared
/// install-wide.
/// </para>
/// </summary>
public sealed class PackRoutePermissionTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000fe"));
    private static readonly TenantId TenantA = ActiveTeamTenantContext.ProjectTenantId(TeamA);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private readonly MutableGrants _grants = new();
    private AuthorizationGate _authz = null!;
    private InMemoryPackInstallStore _store = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // Ticket 294 slice 3b: the desktop actor (no compile-time operator id).
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // T-380: the node's refusal audit, composed as the shared host composes it, so a pack-route denial is
        // rendered and recorded rather than answered with a route-local shape.
        _authz = new AuthorizationGate(_grants, new EmptyRecordStandingResolver(), _grants);
        builder.Services.AddSingleton(_authz);
        builder.Services.AddSingleton(new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton<IOperationSigner>(sp => sp.GetRequiredService<NodePrincipalSigner>().Signer);
        builder.Services.AddTestInMemoryKernelAudit();
        builder.Services.AddAuthorizationRefusalAudit();
        _app = builder.Build();

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        var store = _store = new InMemoryPackInstallStore();
        var admission = new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator());
        var installer = new PackInstaller(
            verifier,
            store,
            admission,
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            new Harborline.Api.Foundation.Packs.Install.Compatibility.PackPlatformCompatibility(
                "2.0.0",
                PackSeedProjector.RegisteredCases));
        var registryServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var projector = new PackSeedProjector(
            store, registryServices.GetRequiredService<IEntityTypeRegistry>(), NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(
            _app, exporter, verifier, trustStore, signer, activeTeam, _authz, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, installer, store, trustStore, PackRevocationList.Empty, activeTeam,
            _authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: projector);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

        // Permission tests exercise the post-install routes' authorization decisions. Seed one unrelated
        // Draft pack through the installer route so the surface is present before each test narrows grants.
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor, Permission.PackagesOperate);
        using var exported = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody("surface.seed"));
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var bytes = await exported.Content.ReadAsByteArrayAsync();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync(PackInstallRoutes.InstallRoute, content)).StatusCode);
        _grants.Grant();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ── AUTHOR gate: export + verify ─────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "export is denied fail-closed without packages:author")]
    public async Task Export_denied_without_author()
    {
        _grants.Grant(/* nothing */);
        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody());
        await AssertDeniedAsync(resp, Permission.PackagesAuthor);
    }

    [Fact(DisplayName = "export is permitted with packages:author")]
    public async Task Export_allowed_with_author()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // a signed pack file is returned
    }

    [Fact(DisplayName = "verify is denied fail-closed without packages:author")]
    public async Task Verify_denied_without_author()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesOperate);   // has operate, but verify needs AUTHOR
        var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resp = await _client.PostAsync(PackComposerRoutes.VerifyRoute, content);
        await AssertDeniedAsync(resp, Permission.PackagesAuthor);
    }

    // ── OPERATE gate: preview + install + activate + deactivate + installed ──────────────────────────────

    [Theory(DisplayName = "operate routes are denied fail-closed without packages:operate")]
    [InlineData("preview")]
    [InlineData("install")]
    public async Task Operate_post_routes_denied_without_operate(string which)
    {
        // An AUTHOR-only principal must NOT be able to operate — proves the author/operate split is real.
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        var route = which == "preview" ? PackInstallRoutes.PreviewRoute : PackInstallRoutes.InstallRoute;
        var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resp = await _client.PostAsync(route, content);
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    [Fact(DisplayName = "activate is denied fail-closed without packages:operate")]
    public async Task Activate_denied_without_operate()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        var resp = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new { packKey = "k", version = "1.0.0" });
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    [Fact(DisplayName = "deactivate is denied fail-closed without packages:operate")]
    public async Task Deactivate_denied_without_operate()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        var resp = await _client.PostAsJsonAsync(PackInstallRoutes.DeactivateRoute,
            new { packKey = "k", version = "1.0.0" });
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    [Fact(DisplayName = "installed list is denied fail-closed without packages:operate")]
    public async Task Installed_denied_without_operate()
    {
        _grants.Grant(/* nothing */);
        var resp = await _client.GetAsync(PackInstallRoutes.ListInstalledRoute);
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    [Fact(DisplayName = "install of a valid pack is permitted with packages:operate")]
    public async Task Install_allowed_with_operate()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor, Permission.PackagesOperate);
        var export = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody());
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var packBytes = await export.Content.ReadAsByteArrayAsync();

        var content = new ByteArrayContent(packBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var install = await _client.PostAsync(PackInstallRoutes.InstallRoute, content);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);   // NOT a 403
    }

    // ── the record target: the pack the route addresses (ticket 205 slice 3) ────────────────────────────

    [Fact(DisplayName = "205: activate refuses a grant scoped to a DIFFERENT pack")]
    public async Task Activate_refuses_a_grant_outside_the_packs_record_scope()
    {
        _grants.GrantForPack("other.pack", Permission.PackagesOperate);
        var resp = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = "acme.pack", version = "1.0.0" });
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    [Fact(DisplayName = "205: activate passes the gate on a grant scoped to the pack it addresses")]
    public async Task Activate_passes_the_gate_on_a_grant_in_the_packs_record_scope()
    {
        _grants.GrantForPack("acme.pack", Permission.PackagesOperate);
        var resp = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = "acme.pack", version = "1.0.0" });
        // Past the gate: the installer then refuses an un-installed pack. NOT a 403.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact(DisplayName = "205: deactivate refuses a grant scoped to a DIFFERENT pack")]
    public async Task Deactivate_refuses_a_grant_outside_the_packs_record_scope()
    {
        _grants.GrantForPack("other.pack", Permission.PackagesOperate);
        var resp = await _client.PostAsJsonAsync(
            PackInstallRoutes.DeactivateRoute, new { packKey = "acme.pack", version = "1.0.0" });
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    [Fact(DisplayName = "205: export refuses a grant scoped to a DIFFERENT pack, and passes on the right one")]
    public async Task Export_follows_the_packs_record_scope()
    {
        _grants.GrantForPack("other.pack", Permission.PackagesAuthor);
        await AssertDeniedAsync(
            await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody()),
            Permission.PackagesAuthor);

        _grants.GrantForPack("acme.pack", Permission.PackagesAuthor);
        var allowed = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact(DisplayName = "205: install resolves against the pack INSIDE the artifact, not the caller's word")]
    public async Task Install_refuses_a_grant_outside_the_artifacts_pack_scope()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor, Permission.PackagesOperate);
        var export = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody());
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var packBytes = await export.Content.ReadAsByteArrayAsync();

        // A grant on a different pack must not install acme.pack, even though the caller names no pack
        // anywhere on the wire — the key comes out of the signed artifact.
        _grants.GrantForPack("other.pack", Permission.PackagesOperate);
        var content = new ByteArrayContent(packBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await AssertDeniedAsync(
            await _client.PostAsync(PackInstallRoutes.InstallRoute, content), Permission.PackagesOperate);

        _grants.GrantForPack("acme.pack", Permission.PackagesOperate);
        var scoped = new ByteArrayContent(packBytes);
        scoped.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Assert.Equal(
            HttpStatusCode.OK, (await _client.PostAsync(PackInstallRoutes.InstallRoute, scoped)).StatusCode);
    }

    [Theory(DisplayName =
        "205: the install-wide routes carry no record target — a pack-scoped grant does not reach them")]
    [InlineData("installed")]
    [InlineData("preview")]
    [InlineData("verify")]
    public async Task Install_wide_routes_refuse_a_pack_scoped_grant(string which)
    {
        _grants.GrantForPack("acme.pack", Permission.PackagesOperate, Permission.PackagesAuthor);
        var (response, permission) = which switch
        {
            "installed" => (await _client.GetAsync(PackInstallRoutes.ListInstalledRoute), Permission.PackagesOperate),
            "preview" => (await _client.PostAsync(PackInstallRoutes.PreviewRoute, Artifact()), Permission.PackagesOperate),
            _ => (await _client.PostAsync(PackComposerRoutes.VerifyRoute, Artifact()), Permission.PackagesAuthor),
        };
        await AssertDeniedAsync(response, permission);
    }

    [Theory(DisplayName = "205: a pack key that cannot be a record id is refused fail-closed, not thrown")]
    [InlineData("acme/../../root")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task A_pack_key_that_cannot_be_a_record_id_is_refused(string packKey)
    {
        _grants.GrantAtInstallRoot(Permission.PackagesOperate);
        var resp = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey, version = "1.0.0" });
        await AssertDeniedAsync(resp, Permission.PackagesOperate);
    }

    // ── T-380: a pack-route refusal is the node's audited refusal, and the refused act writes nothing ──────

    [Fact(DisplayName = "380: an install refused for packages:operate is audited and installs nothing; granting operate then installs")]
    public async Task Install_refusal_is_audited_and_leaves_the_store_unchanged()
    {
        var packBytes = await ExportAcmeAsync();

        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        await AssertDeniedAsync(
            await _client.PostAsync(PackInstallRoutes.InstallRoute, Octets(packBytes)), Permission.PackagesOperate);
        Assert.Null(_store.GetVersion(TenantA, "acme.pack", "1.0.0"));

        _grants.GrantAtInstallRoot(Permission.PackagesOperate);
        using var retried = await _client.PostAsync(PackInstallRoutes.InstallRoute, Octets(packBytes));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.NotNull(_store.GetVersion(TenantA, "acme.pack", "1.0.0"));
        Assert.Single(await RefusalRowsAsync());
    }

    [Fact(DisplayName = "380: an activate refused for packages:operate is audited and activates nothing; an operate retry passes the gate")]
    public async Task Activate_refusal_is_audited_and_leaves_the_pack_inactive()
    {
        var packBytes = await ExportAcmeAsync();
        _grants.GrantAtInstallRoot(Permission.PackagesOperate);
        Assert.Equal(
            HttpStatusCode.OK, (await _client.PostAsync(PackInstallRoutes.InstallRoute, Octets(packBytes))).StatusCode);
        Assert.Null(_store.GetActive(TenantA, "acme.pack"));

        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        var activate = new { packKey = "acme.pack", version = "1.0.0" };
        await AssertDeniedAsync(
            await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, activate), Permission.PackagesOperate);
        Assert.Null(_store.GetActive(TenantA, "acme.pack"));

        // Past the gate on retry: this fixture has no platform pack, so the installer refuses on its own terms.
        _grants.GrantAtInstallRoot(Permission.PackagesOperate);
        using var retried = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, activate);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retried.StatusCode);
        Assert.Contains("pack.install.activate.platform_pack_required", await retried.Content.ReadAsStringAsync());
        Assert.Single(await RefusalRowsAsync());
    }

    [Fact(DisplayName = "380: a decided pack-route refusal records the gate's four-stage trace, not a pre-decision row")]
    public async Task A_decided_refusal_records_the_four_stage_trace()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesOperate);
        await AssertDeniedAsync(await _client.PostAsync(PackComposerRoutes.VerifyRoute, Artifact()), Permission.PackagesAuthor);

        var row = Assert.Single(await RefusalRowsAsync());
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.Equal(4, row.AuthoritySnapshot!.Trace!.Count);
    }

    [Fact(DisplayName = "380: a pack key that cannot be a record id is recorded as a pre-decision refusal")]
    public async Task An_unaddressable_pack_key_is_a_recorded_pre_decision_refusal()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesOperate);
        await AssertDeniedAsync(
            await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new { packKey = "..", version = "1.0.0" }),
            Permission.PackagesOperate);

        var row = Assert.Single(await RefusalRowsAsync());
        Assert.Equal(true, row.Payload.Payload.Body["preDecision"]);
    }

    private async Task<byte[]> ExportAcmeAsync()
    {
        _grants.GrantAtInstallRoot(Permission.PackagesAuthor);
        using var export = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, FormPackBody());
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        return await export.Content.ReadAsByteArrayAsync();
    }

    private static ByteArrayContent Octets(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private async Task<List<AuditRecord>> RefusalRowsAsync()
    {
        var rows = new List<AuditRecord>();
        await foreach (var record in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(TenantA)))
        {
            if (record.EventType.Equals(AuthorizationRefusalAudit.AuthorizationRefusedEventType))
                rows.Add(record);
        }
        return rows;
    }

    private static ByteArrayContent Artifact()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The node's one refusal (T-380, RW-4): a 403 carrying the shared code, the missing permission and the
    /// id of the <c>AuthorizationRefused</c> row this very denial wrote.
    /// </summary>
    private async Task AssertDeniedAsync(HttpResponseMessage resp, string expectedPermission)
    {
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("authorization.permission_required", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(expectedPermission, doc.RootElement.GetProperty("permission").GetString());
        var auditId = doc.RootElement.GetProperty("auditId").GetGuid();
        var row = Assert.Single(await RefusalRowsAsync(), record => record.AuditId == auditId);
        Assert.Equal(expectedPermission, row.Payload.Payload.Body["permission"]);
        Assert.Equal(auditId.ToString("D"), resp.Headers.GetValues("X-Harborline-Audit-Id").Single());
    }

    private static object FormPackBody(string key = "acme.pack") => new
    {
        key,
        version = "1.0.0",
        name = "Acme Pack",
        description = "form pack",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "intake",
                kind = "FormDefinition",
                version = "1.0.0",
                content = new { title = "Intake", assignee = "role:approver" },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "forms.dynamic" },
    };

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    /// <summary>
    /// The grant source behind the ONE <see cref="AuthorizationGate"/> the routes close over: a flippable
    /// set of (operation, scope) grants. Refusal is decided by the gate's own scope containment, never by
    /// this double — a grant at <c>/records/acme.pack</c> does not cover an act at
    /// <c>/records/other.pack</c>, and neither covers an install-wide act at <c>/</c>.
    /// </summary>
    private sealed class MutableGrants : IAuthorizationClosureSnapshotReader, IAuthorizationDefinitionAtomReader
    {
        private readonly List<PermissionAtom> _granted = [];
        private readonly List<PermissionAtom> _issued = [];

        /// <summary>Grants the named operations across the whole install (the pre-205 breadth).</summary>
        public void GrantAtInstallRoot(params string[] operations) => Grant(operations
            .Select(operation => new PermissionAtom(
                AuthorizationOperation.Parse(operation), ScopeExpression.Parse("/")))
            .ToArray());

        /// <summary>Grants the named operations ONLY inside one pack's record scope.</summary>
        public void GrantForPack(string packKey, params string[] operations) => Grant(operations
            .Select(operation => new PermissionAtom(
                AuthorizationOperation.Parse(operation), ScopeExpression.Parse($"/records/{packKey}")))
            .ToArray());

        public void Grant(params PermissionAtom[] atoms)
        {
            _granted.Clear();
            _granted.AddRange(atoms);
        }

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _issued.Clear();
            var derivations = new List<AuthorizationAtomDerivation>();
            foreach (var atom in _granted.Where(atom => atom.Operation.Equals(request.Act.Operation)))
            {
                _issued.Add(atom);
                derivations.Add(new AuthorizationAtomDerivation(
                    atom, RoleReference.Administrator, "test-grant", 1, "test-definition",
                    atom.Scope, request.At.AddMinutes(-1), null));
            }

            return ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations));
        }

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = tenantId;
            _ = role;
            return ValueTask.FromResult<IReadOnlyList<PermissionAtom>>(_issued.ToArray());
        }
    }

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
