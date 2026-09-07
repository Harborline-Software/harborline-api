using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
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
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 4 (L624, L674 api half) — an administrator NARROWS an Access definition through the
/// ORDINARY overlay pipeline: an RFC-7396 tenant override row on the pack's content key, the same row the
/// S-10 re-attach carries across an upgrade. There is no second pipeline and no new store. The three
/// properties proved here are: the narrowing is live after the next projection pass; it is still live
/// after the pack is REPLACED by a newer version that still declares the definition (the S-10 re-attach);
/// and it is still live after a REPLAY of the pass from the persisted state. An overlay that would WIDEN
/// the reviewed seed — a field, a state, a role the publisher never offered — is refused at the write
/// door, and an overlay whose definition the newer version no longer declares is retired with the
/// definition (slice 2's removal), never left dangling over content nobody ships.
/// </summary>
public sealed class PackNarrowingSurvivesReplayTests
{
    private const string PackKey = "harborline.access-administration";
    private const string BindingKey = "access.binding.records-read";
    private const string ItemVersion = "1.0.0";

    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000408");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The privileged-review workflow the Access package ships, verbatim.</summary>
    private const string Workflow = """
        {"key":"access.privileged-grant-review","version":"1.0.0",
         "tenant":"00000000-0000-0000-0000-000000000000","initialState":"requested",
         "states":[{"id":"requested","kind":"Normal"},{"id":"approved","kind":"Terminal"},
                   {"id":"declined","kind":"Terminal"}],
         "triggers":[{"id":"approve","kind":"HumanAction","task":"approve"},
                     {"id":"decline","kind":"HumanAction","task":"decline"}],
         "transitions":[{"id":"approve","from":"requested","on":"approve","to":"approved"},
                        {"id":"decline","from":"requested","on":"decline","to":"declined"}],
         "actions":[]}
        """;

    private static JsonNode BindingItem(string operation, params string[] offeredRoles) =>
        JsonSerializer.SerializeToNode(new { operation, scope = "/", offeredRoles })!;

    // ── The narrowing itself, across an upgrade and a replay ────────────────────────────────────────

    [Fact(DisplayName = "208 s4: an administrator's narrowing of a pack binding is live, survives the upgrade that re-declares it, and survives replay")]
    public async Task A_Narrowed_Binding_Survives_Upgrade_And_Replay()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(
            new[] { RoleReference.Administrator },
            (await world.EffectiveRolesAsync()).Roles.ToArray());

        // The administrator narrows the pack's offer to NOBODY — the same reduction the projector's own
        // retraction arm performs, now authored by a tenant through the ordinary overlay row.
        var narrowing = world.Installer.Narrow(
            world.Context, PackKey, BindingKey, JsonNode.Parse("""{"offeredRoles":[]}""")!,
            TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now));
        Assert.True(narrowing.Recorded, narrowing.RefusalCode);

        // (1) Live after the next ordinary pass — no new pipeline ran.
        World.SimulateProcessRestart();
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(RoleBindingSet.Empty, await world.EffectiveRolesAsync());

        // (2) Still live after the pack is REPLACED by 1.1.0, which still declares the definition: the
        //     S-10 re-attach re-expressed the overlay over the new version's seed inside the install
        //     transaction.
        await world.InstallAndActivateAsync("1.1.0");
        Assert.Equal(BindingKey, Assert.Single(world.Overrides()).ContentKey);
        World.SimulateProcessRestart();
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(RoleBindingSet.Empty, await world.EffectiveRolesAsync());

        // (3) Still live after a REPLAY of the pass over the same persisted state — the overrides are
        //     read fresh every pass, so replay cannot restore the publisher's wider offer.
        World.SimulateProcessRestart();
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(RoleBindingSet.Empty, await world.EffectiveRolesAsync());
    }

    [Fact(DisplayName = "208 s4: an overlay whose definition the newer version no longer declares is retired with the definition, not left dangling")]
    public async Task An_Overlay_On_A_Removed_Definition_Is_Retired_With_It()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");
        await world.ProjectAsync();
        Assert.True(world.Installer.Narrow(
            world.Context, PackKey, BindingKey, JsonNode.Parse("""{"offeredRoles":[]}""")!,
            TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now)).Recorded);
        Assert.Single(world.Overrides());

        // 1.1.0 drops the definition entirely. The overlay has nothing to re-attach ONTO, so the S-10
        // planner orphans it rather than carrying a patch over content nobody ships.
        world.Replace(("access.binding.other", PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(Permission.OrgManageSettings, RoleReference.Administrator.ToString())));
        await world.InstallAndActivateAsync("1.1.0");

        Assert.DoesNotContain(world.Overrides(), o => o.ContentKey == BindingKey);
        // And the definition it narrowed is gone with the replacement (slice 2): its binding is empty,
        // so no live capability is left carrying either the seed's offer or a dangling overlay.
        World.SimulateProcessRestart();
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(RoleBindingSet.Empty, await world.EffectiveRolesAsync());
    }

    // ── Widening is refused at the ordinary door, per kind ──────────────────────────────────────────

    [Theory(DisplayName = "208 s4: an overlay that WIDENS the reviewed seed is refused with the overlay refusal, per narrowed kind")]
    // The grant form: a field the reviewed form never declared.
    [InlineData(PackContentKind.FormDefinition,
        """{"overlay":{"fields":{"person":{"controlHint":"text"}},"sections":["main"]}}""",
        """{"overlay":{"fields":{"salary":{"controlHint":"text"}}}}""",
        "/overlay/fields/salary")]
    // The privileged-review workflow: a state the reviewed workflow never declared (its own shipped
    // body, so the widening is refused over content the composed host really admits).
    [InlineData(PackContentKind.WorkflowDefinition,
        Workflow,
        """{"states":[{"id":"requested","kind":"Normal"},{"id":"approved","kind":"Terminal"},{"id":"declined","kind":"Terminal"},{"id":"self-approved","kind":"Terminal"}]}""",
        "/states")]
    // The pack binding: a role outside the publisher's offer.
    [InlineData(PackContentKind.AuthorizationCapabilityBinding,
        """{"operation":"records:read","scope":"/","offeredRoles":["sys.platform-roles/administrator"]}""",
        """{"offeredRoles":["sys.platform-roles/administrator","sys.platform-roles/auditor"]}""",
        "/offeredRoles")]
    public async Task A_Widening_Overlay_Is_Refused_Per_Kind(
        PackContentKind kind, string seedJson, string wideningJson, string expectedPath)
    {
        var world = new World();
        world.Add("access.widened", kind, JsonNode.Parse(seedJson)!);
        await world.InstallAndActivateAsync("1.0.0");

        var outcome = world.Installer.Narrow(
            world.Context, PackKey, "access.widened", JsonNode.Parse(wideningJson)!,
            TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now));

        Assert.False(outcome.Recorded);
        Assert.Equal(PackTenantNarrowing.WideningRefusedCode, outcome.RefusalCode);
        Assert.Equal(expectedPath, outcome.WideningPath);
        // Nothing was stored: a refused widening leaves the reviewed seed the only thing that projects.
        Assert.Empty(world.Overrides());
    }

    [Fact(DisplayName = "208 s4: the narrowing shapes RFC-7396 can express are admitted, and every other shape widens")]
    public void The_Narrowing_Rule_Admits_Exactly_The_Three_Narrowing_Shapes()
    {
        var seed = JsonNode.Parse(
            """{"fields":{"person":{"required":true},"salary":{}},"roles":["a","b"],"label":"Grant"}""")!;

        // Removal, nested removal, and a proper array subset — the three shapes.
        Assert.True(PackTenantNarrowing.IsNarrowing(seed, JsonNode.Parse("""{"label":null}""")!, out _));
        Assert.True(PackTenantNarrowing.IsNarrowing(
            seed, JsonNode.Parse("""{"fields":{"salary":null}}""")!, out _));
        Assert.True(PackTenantNarrowing.IsNarrowing(seed, JsonNode.Parse("""{"roles":["a"]}""")!, out _));

        // A new key, a scalar rewrite, a re-ordering that adds nothing but removes nothing, and a
        // wholesale document replacement all WIDEN — the seed's reviewed value is the ceiling.
        Assert.False(PackTenantNarrowing.IsNarrowing(seed, JsonNode.Parse("""{"bonus":1}""")!, out var added));
        Assert.Equal("/bonus", added);
        Assert.False(PackTenantNarrowing.IsNarrowing(seed, JsonNode.Parse("""{"label":"Anything"}""")!, out _));
        Assert.False(PackTenantNarrowing.IsNarrowing(seed, JsonNode.Parse("""{"roles":["b","a"]}""")!, out _));
        Assert.False(PackTenantNarrowing.IsNarrowing(seed, JsonNode.Parse("""["a"]""")!, out _));
    }

    /// <summary>One tenant, one pack key, the real installer, the real projector, and the real
    /// authorization definition writer + admission over ticket 204's store — the slice-3 world, plus the
    /// ability to publish a SECOND version of the pack so the S-10 re-attach really runs.</summary>
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
                // The node host's own admission, so a WorkflowDefinition really is admitted here.
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
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

        /// <summary>The content of the NEXT exported version — what an upgrade re-declares (or drops).</summary>
        public void Replace(params (string Key, PackContentKind Kind, JsonNode Content)[] contents)
        {
            _contents.Clear();
            foreach (var (key, kind, content) in contents)
            {
                Add(key, kind, content);
            }
        }

        public IReadOnlyList<PackTenantOverride> Overrides() => _store.GetOverrides(Tenant, PackKey);

        /// <summary>The single definition's EFFECTIVE role binding — what a narrowing changes.</summary>
        public async Task<RoleBindingSet> EffectiveRolesAsync()
        {
            var definitions = await Definitions.ListAsync(Tenant);
            return definitions.Count == 0 ? RoleBindingSet.Empty : definitions[0].EffectiveRoles;
        }

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
                    Description: "Exercises an administrator's narrowing of pack-declared Access content.",
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
