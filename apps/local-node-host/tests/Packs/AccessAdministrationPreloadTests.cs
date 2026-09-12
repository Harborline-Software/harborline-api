using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 1 acceptance: the Access administration package is preloaded from an EMPTY
/// installation through the ordinary export → install → activate → project path, creating exactly the TWO
/// definitions this host can admit (the grant form and the privileged-review workflow) with pack
/// provenance and NO grant row, and a second boot neither duplicates nor re-installs it.
/// <para>
/// Fix 1: the view and report registries here are the host's REAL
/// <see cref="HostViewKindDescriptorRegistry"/> / <see cref="HostReportKindDescriptorRegistry"/> — no
/// accept-all stub — so a green here is a green in the composed node.
/// </para>
/// </summary>
public sealed class AccessAdministrationPreloadTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000208");

    /// <summary>The canonical form field order the export document declares (fix 1, MAJOR 4).</summary>
    private static readonly string[] GrantFormFields =
    {
        "person", "role", "scope", "residency", "effectiveFrom", "effectiveTo", "reason",
    };

    private WebApplication _app = null!;
    private KeyPair _key = null!;
    private NodePrincipalSigner _signer = null!;
    private InMemoryPackInstallStore _store = null!;
    private PackInstaller _installer = null!;
    private InMemoryViewDefinitionRegistry _views = null!;
    private InMemoryReportDefinitionRegistry _reports = null!;
    private IFormDefinitionStore _forms = null!;
    private IWorkflowDefinitionStore _workflows = null!;
    private AccessAdministrationPreloadHostedService _preload = null!;
    private PlatformPackPreloadHostedService _platformPreload = null!;
    private InMemoryRoleVocabulary _roles = null!;
    private InMemoryPackInstallAudit _audit = null!;

    public Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Services.AddLogging();
        builder.Services.AddInMemoryAssetTypeSystem();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms(
            configureWriters: static (services, entityMutations, _) =>
                services.AddEntityStoreWorkflowDefinitionStore(entityMutations));
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        _app = builder.Build();

        // The node's own signing identity: the export is signed by it and the trust store recognises it as
        // the OwnRoster root — exactly the posture the composed host gives the preload.
        _key = KeyPair.Generate();
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        _signer = new NodePrincipalSigner(seed);

        _forms = _app.Services.GetRequiredService<IFormDefinitionStore>();
        _workflows = _app.Services.GetRequiredService<IWorkflowDefinitionStore>();
        var schemas = _app.Services.GetRequiredService<ISchemaRegistry>();
        _roles = new InMemoryRoleVocabulary(
        [
            AccessGrantAuthorizationSeed.MemberDefinition,
        ]);
        var roleGate = new RoleGateAdmission(_roles, _forms, _workflows);
        // The REAL host descriptor registries: the shipped definitions must be admissible by the
        // composed node, not by a stub. (They admit the two shipped items because neither is a view or
        // a report; a view over an unregistered entity type or an unregistered report kind still fails.)
        _views = new InMemoryViewDefinitionRegistry(new HostViewKindDescriptorRegistry(
            _app.Services.GetRequiredService<IEntityTypeRegistry>(), _forms, schemas));
        _reports = new InMemoryReportDefinitionRegistry(
            new HostReportKindDescriptorRegistry(new ReportCartridgeRegistry()));

        _store = new InMemoryPackInstallStore();
        var codec = new PackFileCodec();
        _audit = new InMemoryPackInstallAudit();
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            _store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), roleGateAdmission: roleGate),
            _audit,
            TestAuthorization.AllowGate());
        var projector = new PackSeedProjector(
            _store,
            _app.Services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            forms: _forms,
            schemas: schemas,
            workflows: _workflows,
            time: TimeProvider.System,
            reportDefinitions: _reports,
            viewDefinitions: _views,
            authorizedForms: TestAuthorization.FormLifecycle(_forms, TestAuthorization.AllowGate(), roleGate),
            authorizedWorkflows: TestAuthorization.WorkflowLifecycle(_workflows, TestAuthorization.AllowGate(), roleGate),
            roleVocabulary: _roles);
        ((IPackProjectionReconciler)_installer).AttachProjector(projector);

        _preload = new AccessAdministrationPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec,
                timeProvider: TimeProvider.System),
            _signer,
            _installer,
            _store,
            TrustingTheNodeKey(),
            PackRevocationList.Empty,
            new NoActiveTeam(),
            TimeProvider.System,
            NullLogger<AccessAdministrationPreloadHostedService>.Instance);

        _platformPreload = new PlatformPackPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec,
                timeProvider: TimeProvider.System),
            _signer,
            _installer,
            _store,
            TrustingTheNodeKey(),
            PackRevocationList.Empty,
            new NoActiveTeam(),
            TimeProvider.System,
            NullLogger<PlatformPackPreloadHostedService>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _signer.Dispose();
        _key.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Preload_from_an_empty_installation_activates_the_pack_and_projects_both_definitions()
    {
        await PreloadPlatformThenAccessAsync();

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey);
        Assert.NotNull(active);
        Assert.Equal(AccessAdministrationPreloadHostedService.PackVersion, active!.Version);
        Assert.Equal(PackLifecycleState.Active, active.Lifecycle);

        // Exactly the two definitions this host admits — the holdings view and the access-by-person
        // report ship at S6, with the entity type and the report cartridge that admit them.
        Assert.Equal(
            new[]
            {
                (PackContentKind.RoleDefinition, "access.form-submitter"),
                (PackContentKind.FormDefinition, "access.grant-a-role"),
                (PackContentKind.NavWorkspaceConfig, "access.navigation"),
                (PackContentKind.WorkflowDefinition, "access.privileged-grant-review"),
            },
            active.SeedItems.Select(item => (item.Kind, item.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());

        // Both content keys resolve through the ordinary catalogue reads — over the REAL registries, so
        // "projected, not refused" is a claim about the composed host.
        var form = await _forms.GetAsync(
            new DefinitionCoordinates(Tenant, "access.grant-a-role", "1.0.1"), CancellationToken.None);
        Assert.NotNull(form);
        var workflow = await _workflows.GetAsync(
            new DefinitionCoordinates(Tenant, "access.privileged-grant-review", "1.0.1"), CancellationToken.None);
        Assert.NotNull(workflow);
    }

    [Fact]
    public async Task Access_activation_without_the_platform_pack_is_refused_at_admission()
    {
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        Assert.Null(_store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey));
        var refusal = Assert.Single(_audit.Query(Tenant), entry =>
            entry.Action == PackInstallAuditAction.Refused
            && entry.PackKey == AccessAdministrationPreloadHostedService.PackKey);
        Assert.StartsWith(PackInstallCodes.ActivatePlatformPackRequired, refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_boot_activates_platform_then_access_and_a_second_boot_is_idempotent()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        var firstBoot = _store.ListInstalled(Tenant)
            .OrderBy(pack => pack.PackKey, StringComparer.Ordinal)
            .Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle, ItemCount: pack.SeedItems.Count))
            .ToArray();
        Assert.Equal(
            new[]
            {
                (AccessAdministrationPreloadHostedService.PackKey, AccessAdministrationPreloadHostedService.PackVersion,
                    PackLifecycleState.Active, 4),
                (PlatformPackPreloadHostedService.PackKey, PlatformPackPreloadHostedService.PackVersion,
                    PackLifecycleState.Active, 21),
            },
            firstBoot);
        Assert.Equal(
            new[] { PlatformPackPreloadHostedService.PackKey, AccessAdministrationPreloadHostedService.PackKey },
            _audit.Query(Tenant)
                .Where(entry => entry.Action == PackInstallAuditAction.Activated)
                .Select(entry => entry.PackKey)
                .ToArray());

        var administrator = await _roles.ResolveAsync(RoleReference.Administrator);
        var auditor = await _roles.ResolveAsync(RoleReference.Auditor);
        Assert.NotNull(administrator);
        Assert.NotNull(auditor);
        var platform = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        Assert.Equal(2, platform.SeedItems.Count(item => item.Kind == PackContentKind.RoleDefinition));
        Assert.Equal(2, platform.SeedItems.Count(item => item.Kind == PackContentKind.AuthorizationCapabilityBinding));

        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        Assert.Equal(firstBoot, _store.ListInstalled(Tenant)
            .OrderBy(pack => pack.PackKey, StringComparer.Ordinal)
            .Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle, ItemCount: pack.SeedItems.Count))
            .ToArray());
        Assert.Equal(2, _store.ListInstalled(Tenant).Count);
        Assert.Equal(2, _audit.Query(Tenant).Count(entry => entry.Action == PackInstallAuditAction.Activated));
    }

    [Fact]
    public async Task Platform_preload_carries_the_one_compiled_descriptor_for_every_record_type()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);

        var platform = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        var carried = platform.SeedItems
            .Where(item => item.Kind == PackContentKind.RecordType)
            .ToArray();
        var compiledByName = SystemRecordType.All.ToDictionary(type => type.Name, StringComparer.Ordinal);

        Assert.Equal(SystemRecordType.All.Count, carried.Length);
        Assert.Equal(SystemRecordType.All.Count, compiledByName.Count);
        foreach (var item in carried)
        {
            Assert.True(PackSealedSystemTypeAdmission.TryResolveCompiledDescriptor(item.Key, out var descriptor),
                $"Carried RecordType key '{item.Key}' does not resolve to a compiled catalogue descriptor.");
            Assert.Same(compiledByName[item.Key], descriptor);
            Assert.True(descriptor!.Sealed);
            Assert.Equal("platform", descriptor.Provenance.Kind);
        }

        var carriedKeys = carried.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var compiledKeys = SystemRecordType.All.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(compiledKeys.Count, carriedKeys.Count);
        Assert.Empty(carriedKeys.Except(compiledKeys));
        Assert.Empty(compiledKeys.Except(carriedKeys));
    }

    [Fact]
    public async Task A_signature_the_composed_trust_store_does_not_recognise_is_refused()
    {
        // The preload built over a trust store that roots trust in SOMEONE ELSE's key: the node's own
        // signature is then untrusted, and no package may be preloaded on its strength.
        var stranger = new NodePrincipalSigner(RandomSeed());
        try
        {
            await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
            var preload = Build(new InMemoryPackTrustStore(new[]
            {
                new PackTrustRoot(
                    TrustScope.OwnRoster,
                    stranger.Signer.IssuerId,
                    PackComposerRoutes.OwnRosterEpoch,
                    TrustRootStatus.Current),
            }));

            await preload.PreloadAsync(Tenant, CancellationToken.None);

            Assert.Null(_store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey));
            Assert.Single(_store.ListInstalled(Tenant), pack =>
                pack.PackKey == PlatformPackPreloadHostedService.PackKey);
        }
        finally
        {
            stranger.Dispose();
        }
    }

    [Fact]
    public async Task A_refused_definition_leaves_no_active_package_and_the_next_boot_retries()
    {
        // A host that refuses one shipped definition (here: every workflow) must not end up ACTIVE over a
        // partial projection.
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        var refusing = Build(TrustingTheNodeKey(), refuseWorkflows: true);
        await refusing.PreloadAsync(Tenant, CancellationToken.None);

        Assert.Null(_store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey));
        Assert.NotNull(PackNavigationRoutes.PackNavigationComposer.Compose(_store, Tenant).Pack);
        // ... but the refusal is durable and operator-visible: the version is installed and INACTIVE in
        // the ordinary installed-pack listing, not silently gone.
        var pending = Assert.Single(
            _store.ListInstalled(Tenant),
            pack => pack.PackKey == AccessAdministrationPreloadHostedService.PackKey);
        Assert.NotEqual(PackLifecycleState.Active, pending.Lifecycle);
        // The form that DID project is retracted with the reversal — nothing of the package stays live.
        var retracted = await _forms.GetAsync(
            new DefinitionCoordinates(Tenant, "access.grant-a-role", "1.0.1"), CancellationToken.None);
        Assert.Equal(FormDefinitionStatus.Withdrawn, retracted!.Status);

        // The next boot — the host now admitting what it refused — retries from that pending state and
        // finishes the activation without re-installing.
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey);
        Assert.NotNull(active);
        Assert.Equal(PackLifecycleState.Active, active!.Lifecycle);
        Assert.NotNull(await _workflows.GetAsync(
            new DefinitionCoordinates(Tenant, "access.privileged-grant-review", "1.0.1"), CancellationToken.None));
    }

    [Fact]
    public void The_grant_form_maps_field_for_field_onto_the_grant_writer()
    {
        var fields = ReadGrantFormFieldsMeta();
        Assert.Equal(GrantFormFields, fields.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .OrderBy(k => Array.IndexOf(GrantFormFields, k)).ToArray());

        // The two structured choices are exactly the vocabularies the writer's types accept.
        Assert.All(fields["reason"].Options!, code => Assert.Contains(code, GrantReasonCodes.All));
        Assert.All(
            fields["residency"].Options!,
            value => Assert.True(Enum.TryParse<GrantResidency>(
                value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out _)));

        // One submitted row, read BY THE FORM'S OWN FIELD NAMES — no mapping table, no renaming.
        var row = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["person"] = "person:ada",
            ["role"] = "administrator",
            ["scope"] = "/",
            ["residency"] = "online-only",
            ["effectiveFrom"] = "2026-09-07T00:00:00Z",
            ["effectiveTo"] = "2027-09-07T00:00:00Z",
            ["reason"] = "manual",
        };
        Assert.All(
            fields.Where(field => field.Value.Required),
            field => Assert.False(string.IsNullOrWhiteSpace(row[field.Key])));

        var granter = new ActorId("person:grace");
        var now = DateTimeOffset.Parse(row["effectiveFrom"]!, CultureInfo.InvariantCulture);
        var grant = new AccessGrant(
            GrantId.New(),
            Tenant,
            subject: new ActorId(row["person"]!),
            role: new RoleReference(RoleVocabularies.Platform, row["role"]!),
            scope: new ScopeExpression(row["scope"]!),
            residency: Enum.Parse<GrantResidency>(
                row["residency"]!.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true),
            validity: new GrantValidity(
                now, DateTimeOffset.Parse(row["effectiveTo"]!, CultureInfo.InvariantCulture)),
            granterKind: GranterKind.Person,
            grantedBy: granter,
            grantedAt: now,
            grant: new GrantProvenance(GrantSourceKind.Manual, new GrantReason(row["reason"]!), granter),
            lastReviewedAt: now);

        Assert.Equal(GrantResidency.OnlineOnly, grant.Residency);
        Assert.Equal("manual", grant.Grant.Reason.Code);
        Assert.True(grant.IsActiveAt(now));
    }

    [Fact]
    public async Task The_preloaded_package_carries_no_grant_row()
    {
        await PreloadPlatformThenAccessAsync();

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey)!;
        // A grant is what a tenant HAS: no content item may be one, by kind or by shape.
        foreach (var item in active.SeedItems)
        {
            Assert.DoesNotContain(item.CanonicalJson, "granterId", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(item.CanonicalJson, "grantedTo", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(item.CanonicalJson, "accessGrant", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_second_boot_neither_duplicates_nor_reinstalls_the_package()
    {
        await PreloadPlatformThenAccessAsync();
        var installedAfterFirstBoot = _store.ListInstalled(Tenant)
            .Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle))
            .ToArray();

        await PreloadPlatformThenAccessAsync();

        Assert.Equal(
            installedAfterFirstBoot,
            _store.ListInstalled(Tenant).Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle)).ToArray());
        Assert.Equal(2, _store.ListInstalled(Tenant).Count);
    }

    private async Task PreloadPlatformThenAccessAsync()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        await _preload.PreloadAsync(Tenant, CancellationToken.None);
    }

    /// <summary>The composed node's posture: the node's own key is the current own-roster root.</summary>
    private IPackTrustStore TrustingTheNodeKey() => new InMemoryPackTrustStore(new[]
    {
        new PackTrustRoot(
            TrustScope.OwnRoster,
            _signer.Signer.IssuerId,
            PackComposerRoutes.OwnRosterEpoch,
            TrustRootStatus.Current),
    });

    private static byte[] RandomSeed()
    {
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        return seed;
    }

    /// <summary>A second preload over the same store, optionally over a projector that refuses the
    /// shipped workflow — the "this host cannot admit one of the definitions" case.</summary>
    private AccessAdministrationPreloadHostedService Build(
        IPackTrustStore trustStore, bool refuseWorkflows = false)
    {
        var installer = _installer;
        if (refuseWorkflows)
        {
            var roleGate = new RoleGateAdmission(_roles, _forms, _workflows);
            var codec = new PackFileCodec();
            installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), codec),
                _store,
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), roleGateAdmission: roleGate),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate());
            ((IPackProjectionReconciler)installer).AttachProjector(new PackSeedProjector(
                _store,
                _app.Services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                forms: _forms,
                schemas: _app.Services.GetRequiredService<ISchemaRegistry>(),
                workflows: _workflows,
                time: TimeProvider.System,
                reportDefinitions: _reports,
                viewDefinitions: _views,
                authorizedForms: TestAuthorization.FormLifecycle(_forms, TestAuthorization.AllowGate(), roleGate),
                // The one difference: the workflow writer's role gate refuses, so the workflow is REFUSED
                // by projection while the form projects — a partial projection, which must not stand.
                authorizedWorkflows: TestAuthorization.WorkflowLifecycle(
                    _workflows, TestAuthorization.AllowGate(), new RefusingRoleGate()),
                roleVocabulary: _roles));
        }

        return new AccessAdministrationPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                new PackFileCodec(),
                timeProvider: TimeProvider.System),
            _signer,
            installer,
            _store,
            trustStore,
            PackRevocationList.Empty,
            new NoActiveTeam(),
            TimeProvider.System,
            NullLogger<AccessAdministrationPreloadHostedService>.Instance);
    }

    /// <summary>The committed export document's grant-form field metadata, read where it is shipped.</summary>
    private static IReadOnlyDictionary<string, FormFieldMeta> ReadGrantFormFieldsMeta()
    {
        using var stream = typeof(AccessAdministrationPreloadHostedService).Assembly
            .GetManifestResourceStream(
                "Harborline.Api.LocalNodeHost.Packs.access-administration-pack.export.json")!;
        using var document = JsonDocument.Parse(stream);
        var form = document.RootElement.GetProperty("contents").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "access.grant-a-role");
        return form.GetProperty("content").GetProperty("fieldsMeta").EnumerateObject().ToDictionary(
            field => field.Name,
            field => new FormFieldMeta(
                field.Value.GetProperty("type").GetString()!,
                field.Value.GetProperty("required").GetBoolean(),
                field.Value.GetProperty("options").ValueKind == JsonValueKind.Array
                    ? field.Value.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray()
                    : null),
            StringComparer.Ordinal);
    }

    private sealed record FormFieldMeta(string Type, bool Required, IReadOnlyList<string>? Options);

    /// <summary>A role gate that refuses every gated definition (the host-cannot-admit-it stand-in).</summary>
    private sealed class RefusingRoleGate : IRoleGateAdmission
    {
        public ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default)
            => throw new RoleGateAdmissionException(new RoleGateFinding(
                "role_gate.unknown_role", "workflow", definition.DefinitionId, definition.Version,
                "approve", "unknown", "role", RoleGatedDefinitionOwnerKind.VendorPackage, null, "tenant"));

        public ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RoleGateFinding>>(Array.Empty<RoleGateFinding>());
    }

    private sealed class NoActiveTeam : IActiveTeamAccessor
    {
        public TeamContext? Active => null;

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

#pragma warning disable CS0067
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067
    }
}
