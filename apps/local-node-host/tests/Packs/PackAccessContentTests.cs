using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
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
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 3 (L675) — a pack carries ROLE DEFINITIONS and AUTHORIZATION CAPABILITY BINDINGS,
/// and never a GRANT. The binding lands as the PUBLISHER CEILING through the ordinary
/// <see cref="AuthorizationDefinitionWriter"/> and the ordinary
/// <see cref="AuthorizationDefinitionAdmission"/>, so a pack's ceiling is bounded by every rule that
/// admission holds — L628 included: a pack cannot offer the Auditor anything but the platform's own
/// <c>audit:read</c>, which it cannot publish at all. The prohibition on grants is closed twice: BY
/// KIND (<see cref="PackContentKind"/> has no grant member and the codec refuses an undefined kind) and
/// BY SHAPE (a grant smuggled inside another kind's JSON is refused just the same, with the same stable
/// code). A refused pack follows slice 2's invariant: it admits nothing and it removes nothing.
/// </summary>
public sealed class PackAccessContentTests
{
    private const string PackKey = "harborline.access-administration";
    private const string RoleKey = "access.role.grant-reviewer";
    private const string BindingKey = "access.binding.records-read";
    private const string ItemVersion = "1.0.0";
    private const string RoleName = "grant-reviewer";

    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000308");
    private static readonly TenantId OtherTenant = new("bbbbbbbb-0000-0000-0000-000000000308");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly RoleReference PackRole = new(RoleVocabularies.Domain, RoleName);

    private static JsonNode RoleItem(string displayName = "Grant reviewer") =>
        JsonSerializer.SerializeToNode(new { role = RoleName, displayName })!;

    private static JsonNode BindingItem(string operation, params string[] offeredRoles) =>
        JsonSerializer.SerializeToNode(new { operation, scope = "/", offeredRoles })!;

    [Fact(DisplayName = "208 s3: a pack shipping a role definition and a binding that NARROWS the seed's reviewed offer installs, and the binding is the publisher ceiling")]
    public async Task Role_And_Binding_Install_And_The_Binding_Is_The_Publisher_Ceiling()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            // {Administrator} is a strict subset of the seed's reviewed offer for records:read
            // ({Administrator, member, node-operator}) -- and it is in practice the ONLY role a pack may
            // offer for a PLATFORM operation, because the seed's own domain roles fail the admission's
            // ownership rule for a pack publisher and a pack's invented role fails the ceiling. A pack
            // role receives capability through the pack's OWN operation.
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        Assert.Empty(summary.Refusals);
        var role = await world.Vocabulary.ResolveAsync(PackRole);
        Assert.NotNull(role);
        Assert.Equal(RoleOwnerKind.Package, role!.Owner.Kind);
        Assert.Equal(PackKey, role.Owner.OwnerId);
        Assert.False(role.IsSealed);

        // The ceiling: the offered roles the definition carries, read back off ticket 204's store.
        var definition = Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Equal(TeamRolePermissions.RecordsRead, definition.Definition.Operation.Value);
        Assert.Equal(PackKey, definition.Definition.PublisherPackageId);
        Assert.Equal(
            new[] { RoleReference.Administrator },
            definition.Definition.OfferedRoles.Roles.OrderBy(r => r.ToString(), StringComparer.Ordinal));
        // Effective = the ceiling until a tenant narrows it; a tenant can never widen past it.
        Assert.Equal(definition.Definition.OfferedRoles, definition.EffectiveRoles);

        // Re-projecting the same pack on the next boot is a no-op, not a second revision.
        World.SimulateProcessRestart();
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(1, Assert.Single(await world.Definitions.ListAsync(Tenant)).Definition.Revision);
    }

    [Fact(DisplayName = "208 s3: a pack that widens the Auditor is refused by the existing admission")]
    public async Task A_Pack_That_Widens_The_Auditor_Is_Refused()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(Permission.AuditRead, RoleReference.Auditor.ToString()));
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.AuthorizationCapabilityBinding, refusal.ContentKind);
        Assert.Equal(PackSeedProjector.AccessProjectionRefusedCode, refusal.Code);
        Assert.Empty(await world.Definitions.ListAsync(Tenant));
    }

    [Theory(DisplayName = "208 s3: a pack carrying a grant is refused with a stable code and leaves the store untouched")]
    // BY KIND: the item declares itself an access item and is a grant instance.
    [InlineData(PackContentKind.AuthorizationCapabilityBinding)]
    // BY SHAPE: the same grant smuggled inside another kind's JSON.
    [InlineData(PackContentKind.ViewDefinition)]
    public async Task A_Pack_Carrying_A_Grant_Is_Refused_And_Nothing_Lands(PackContentKind smuggledUnder)
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        world.Add("access.grant.alice", smuggledUnder, JsonSerializer.SerializeToNode(new
        {
            title = "Perfectly ordinary content",
            entries = new[]
            {
                new { subject = "os:alice#node", role = PackRole.ToString(), scope = "/" },
            },
        })!);
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal("access.grant.alice", refusal.ContentKey);
        Assert.Equal(PackAuthorizationContentAdmission.GrantInstanceRefusedCode, refusal.Code);
        // Admits nothing: the role and the binding the SAME pack also shipped never landed.
        Assert.Null(await world.Vocabulary.ResolveAsync(PackRole));
        Assert.Empty(await world.Definitions.ListAsync(Tenant));
        // Removes nothing: slice 2's replacement leg is skipped while any refusal stands.
        Assert.Empty(summary.RetractedByKind);
    }

    [Fact(DisplayName = "208 s3: retraction removes both new kinds")]
    public async Task Retraction_Removes_Both_Kinds()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");
        await world.ProjectAsync();
        Assert.NotNull(await world.Vocabulary.ResolveAsync(PackRole));

        var deactivation = world.Installer.Deactivate(Tenant, PackKey, "1.0.0", Now, "test-operator");
        Assert.True(deactivation.Deactivated, deactivation.Error);
        World.SimulateProcessRestart();
        var down = await world.ProjectAsync();

        Assert.Equal(1, down.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Equal(1, down.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
        Assert.Null(await world.Vocabulary.ResolveAsync(PackRole));
        // The definition revision is retained (this store is append-only); its BINDING is empty, so the
        // withdrawn pack's capability now offers nothing to anyone.
        Assert.Equal(
            RoleBindingSet.Empty,
            Assert.Single(await world.Definitions.ListAsync(Tenant)).EffectiveRoles);
    }

    [Fact(DisplayName = "208 s3 fix 1: a pack conferring grant:permissions on a role it invented is refused by the seed ceiling")]
    public async Task A_Pack_Cannot_Confer_A_Platform_Only_Operation_On_An_Invented_Role()
    {
        var world = new World();
        // The role definition projects BEFORE the binding, so the invented role really is installed by
        // the time the binding is admitted: nothing but the ceiling stands between this pack and the
        // authority to issue grants.
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(Permission.GrantPermissions, PackRole.ToString()));
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.AuthorizationCapabilityBinding, refusal.ContentKind);
        Assert.Equal(PackSeedProjector.AccessProjectionRefusedCode, refusal.Code);
        Assert.Empty(await world.Definitions.ListAsync(Tenant));

        // The stable code the admission refuses under, pinned where it is thrown.
        Assert.Equal(
            "authorization.definition.pack_exceeds_seed_ceiling",
            AuthorizationDefinitionAdmission.SeedCeilingExceededCode);
        Assert.True(AuthorizationDefinitionAdmission.ExceedsReviewedCeiling(
            Definition(Permission.GrantPermissions, PackRole)));
    }

    [Fact(DisplayName = "208 s3 fix 1: a pack is its own ceiling for an operation the platform seed does not define")]
    public void A_Pack_Is_Its_Own_Ceiling_For_Its_Own_Operation()
    {
        // The seed's reviewed table currently covers the whole platform vocabulary exactly (asserted by
        // AccessGrantAuthorizationSeed.InstallAsync), so a pack-owned operation is not yet reachable
        // through the projector. This pins the branch that will carry it: no reviewed offer means no
        // platform ceiling, and the pack publishes its own operation to its own role.
        Assert.Null(AccessGrantAuthorizationSeed.ReviewedOfferFor(
            AuthorizationOperation.Parse("tax:file-return")));
        Assert.False(AuthorizationDefinitionAdmission.ExceedsReviewedCeiling(
            Definition("tax:file-return", PackRole)));
    }

    [Fact(DisplayName = "208 s3 fix 1: a pack binding is declared by the installing tenant only, and retraction narrows exactly that tenant")]
    public async Task A_Pack_Binding_Is_Effective_In_The_Installing_Tenant_Only()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");
        await world.ProjectAsync();

        // The definition carries the declaring tenant of the install, so a tenant that never installed
        // the pack does not see it and does not inherit its ceiling.
        Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Empty(await world.Definitions.ListAsync(OtherTenant));

        var deactivation = world.Installer.Deactivate(Tenant, PackKey, "1.0.0", Now, "test-operator");
        Assert.True(deactivation.Deactivated, deactivation.Error);
        World.SimulateProcessRestart();
        await world.ProjectAsync();

        // Retraction narrows exactly the installing tenant; the other tenant is untouched because the
        // pack was never effective there in the first place.
        Assert.Equal(
            RoleBindingSet.Empty,
            Assert.Single(await world.Definitions.ListAsync(Tenant)).EffectiveRoles);
        Assert.Empty(await world.Definitions.ListAsync(OtherTenant));
    }

    private static AuthorizationCapabilityDefinition Definition(string operationValue, RoleReference role)
    {
        var operation = AuthorizationOperation.Parse(operationValue);
        return new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("f0f0f0f0-0000-0000-0000-000000000308")),
            PackKey, 1, operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/")), RoleBindingSet.Of(role));
    }

    [Fact(DisplayName = "208 s3: no content kind names a grant")]
    public void No_Content_Kind_Names_A_Grant()
    {
        // Generated from the enum itself, so a future member called "…Grant…" fails here rather than
        // shipping a door the shape fence was written because kinds alone could not close.
        Assert.DoesNotContain(
            Enum.GetNames<PackContentKind>(),
            name => name.Contains("grant", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One tenant, one pack key, the real installer, the real projector, and the real
    /// authorization definition writer + admission over ticket 204's store.</summary>
    private sealed class World
    {
        private readonly KeyPair _keyPair = KeyPair.Generate();
        private readonly PackFileCodec _codec = new();
        private readonly InMemoryPackInstallStore _store = new();
        private readonly List<PackContentSource> _contents = [];
        private readonly ServiceProvider _services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();

        public World()
        {
            Vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
            var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
            Definitions = configuration;
            var writer = new AuthorizationDefinitionWriter(
                configuration,
                configuration,
                new AuthorizationDefinitionAdmission(Vocabulary),
                new AuthorizationCapabilityBindingAdmission(),
                TestAuthorization.AllowGate(),
                grants);
            Installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), _codec),
                _store,
                new WorkflowRefusingPackContentAdmission(),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate());
            Projector = new PackSeedProjector(
                _store,
                _services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                time: TimeProvider.System,
                roleVocabulary: Vocabulary,
                authorizationDefinitions: writer);
            Context = new PackInstallContext(
                Tenant,
                new InMemoryPackTrustStore(
                    [new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current)]),
                PackRevocationList.Empty,
                Now,
                TimeSpan.FromDays(30),
                Principal: "test-operator");
        }

        public InMemoryRoleVocabulary Vocabulary { get; }

        public IAuthorizationDefinitionCatalogueReader Definitions { get; }

        public PackInstaller Installer { get; }

        public PackSeedProjector Projector { get; }

        public PackInstallContext Context { get; }

        public void Add(string key, PackContentKind kind, JsonNode content) =>
            _contents.Add(new PackContentSource(key, kind, ItemVersion, content));

        /// <summary>Clears <c>PackSeedProjector.ConsumedAuthorities</c> — the per-process replay guard,
        /// which a real node restart empties. Test-only; nothing production reaches this field.</summary>
        public static void SimulateProcessRestart()
        {
            var field = typeof(PackSeedProjector).GetField(
                "ConsumedAuthorities",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            ((System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>)field.GetValue(null)!).Clear();
        }

        public Task<PackSeedProjectionSummary> ProjectAsync() =>
            ((IPackSeedProjector)Projector).ProjectActivePacksAsync(Tenant);

        public async Task InstallAndActivateAsync(string packVersion)
        {
            var exporter = new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                _codec,
                timeProvider: TimeProvider.System);
            var export = await exporter.ExportAsync(
                new PackExportRequest(
                    Key: PackKey,
                    Version: packVersion,
                    Name: "Access administration",
                    Description: "Exercises pack role definitions and capability bindings.",
                    ScopeTier: PackScopeTier.Horizontal,
                    Contents: _contents,
                    Dependencies: Array.Empty<PackDependencyRef>(),
                    CapabilityRequirements: Array.Empty<string>(),
                    Epoch: 1,
                    Dcp: DomainComplianceProfile.General("access-administration-test-author")),
                new Ed25519Signer(_keyPair));
            Assert.True(export.Succeeded, string.Join(
                "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

            var install = Installer.Install(export.FileBytes!, Context);
            Assert.True(install.Installed, string.Join("; ", install.RefusalCodes));
            var activation = Installer.Activate(Tenant, PackKey, packVersion, Now, "test-operator");
            Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
        }
    }
}
