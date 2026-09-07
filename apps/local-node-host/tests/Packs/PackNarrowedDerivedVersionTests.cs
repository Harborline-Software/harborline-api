using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 4b (L624) — the completion of slice 4 for the two kinds that publish at an IMMUTABLE
/// <c>(tenant, id, version)</c> tuple. A pack form or workflow whose unnarrowed tuple has already
/// published cannot have that tuple rewritten (nothing in this codebase mutates content at a pinned
/// version, by design), so the tenant's narrowing is projected at a DERIVED version — the seed item
/// version plus a deterministic tag over the override — through the ordinary lifecycle, and the
/// unnarrowed tuple is RETRACTED in the same pass, only after the narrowed body was fully admitted. This
/// is the pack-side spelling of what the ordinary authoring route already does when a definition is
/// re-saved (mint the next patch version, publish the new body, leave the old tuple alone).
/// <para>
/// Every read here goes through <c>GetCurrentPublishedAsync</c> — the catalogue read the S6 surfaces use
/// — never by doing arithmetic on a version string.
/// </para>
/// </summary>
public sealed class PackNarrowedDerivedVersionTests : IAsyncLifetime
{
    private const string PackKey = "harborline.access-administration";
    private const string FormKey = "access.grant-a-role";
    private const string WorkflowKey = "access.privileged-grant-review";

    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000418");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The grant form the Access package ships, verbatim from its export document.</summary>
    private const string FormSeed = """
        {"overlay":{
           "fields":{
             "person":{"label":{"kind":"Literal","value":"Person"},"controlHint":"text","piiSensitivity":"Direct"},
             "reason":{"label":{"kind":"Literal","value":"Reason"},"controlHint":"select","piiSensitivity":"None"}},
           "sections":[{"id":"grant","title":{"kind":"Literal","value":"Grant a role"},
                        "fields":["person","reason"]}],
           "rules":[],
           "title":{"kind":"Literal","value":"Grant a role"},
           "description":{"kind":"Literal","value":"Grant a person a role, in a scope, for a period, with a reason."}},
         "fieldsMeta":{
           "person":{"type":"text","required":true,"options":null},
           "reason":{"type":"select","required":true,"options":["manual","invitation","workflow","ticket"]}}}
        """;

    /// <summary>The privileged-review workflow the Access package ships, verbatim.</summary>
    private const string WorkflowSeed = """
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

    /// <summary>The administrator drops the form's description — a key the reviewed seed declares.</summary>
    private const string FormNarrowing = """{"overlay":{"description":null}}""";

    /// <summary>The administrator drops the DECLINE path — three proper subsets of the seed's arrays.</summary>
    private const string WorkflowNarrowing = """
        {"states":[{"id":"requested","kind":"Normal"},{"id":"approved","kind":"Terminal"}],
         "triggers":[{"id":"approve","kind":"HumanAction","task":"approve"}],
         "transitions":[{"id":"approve","from":"requested","on":"approve","to":"approved"}]}
        """;

    private World _world = null!;

    public Task InitializeAsync()
    {
        _world = new World();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _world.DisposeAsync().AsTask();

    [Theory(DisplayName = "208 s4b: a narrowing of a PUBLISHED pack form projects at a derived version, retracts the unnarrowed tuple, replays as a no-op, follows the upgrade, and is undone by removing the override")]
    [InlineData("1.1.0")]
    [InlineData("1.0.1")]
    [InlineData("1.0.0")]
    public async Task A_Narrowed_Form_Publishes_At_A_Derived_Version_And_The_Unnarrowed_Tuple_Retracts(string upgradedItemVersion)
    {
        _world.Add(FormKey, PackContentKind.FormDefinition, FormSeed, "1.0.0");
        await _world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await _world.ProjectAsync()).Refusals);

        // The unnarrowed form is live at its own seed version, with the publisher's description.
        var seedForm = await _world.CurrentFormAsync();
        Assert.Equal("1.0.0", seedForm!.Version.ToString());
        Assert.NotNull(seedForm.Overlay.Description);

        // The administrator narrows it AFTER it published — the case slice 4 could not complete.
        Assert.True(_world.Narrow(FormKey, FormNarrowing).Recorded);
        var pass = await _world.ProjectAsync();
        Assert.Empty(pass.Refusals);

        // (1) The narrowed shape reads back immediately, at the derived version, through the ordinary
        //     catalogue read — and the derived version is exactly what the deterministic rule says.
        var narrowed = await _world.CurrentFormAsync();
        Assert.Null(narrowed!.Overlay.Description);
        var derived = PackNarrowedVersion.Derive("1.0.0", JsonNode.Parse(FormNarrowing)!);
        Assert.Equal(derived, narrowed.Version.ToString());

        // (2) The unnarrowed tuple retracted in the SAME pass: it is no longer published, and nothing
        //     that pins a version can still reach the publisher's wider body.
        Assert.Equal(1, pass.FormDefinitionsRetracted);
        Assert.Equal(
            FormDefinitionStatus.Withdrawn,
            (await _world.FormAtAsync("1.0.0")).Status);

        // (3) Replay is an exact no-op: the same derived tuple, already present, nothing published and
        //     nothing retracted a second time.
        var replay = await _world.ProjectAsync();
        Assert.Empty(replay.Refusals);
        Assert.Equal(0, replay.FormDefinitionsPublished);
        Assert.Equal(1, replay.FormDefinitionsAlreadyPresent);
        Assert.Equal(0, replay.FormDefinitionsRetracted);
        Assert.Equal(derived, (await _world.CurrentFormAsync())!.Version.ToString());

        // (4) Upgrade with a minor bump, a patch bump, or no item-version change. S-10 carries the
        //     override onto it; the previous derived tuple retracts when the new tuple differs.
        _world.Replace((FormKey, PackContentKind.FormDefinition, FormSeed, upgradedItemVersion));
        await _world.InstallAndActivateAsync("1.1.0");
        Assert.Empty((await _world.ProjectAsync()).Refusals);
        var upgraded = await _world.CurrentFormAsync();
        // Derived from the override the S-10 re-attach actually left on the new seed (the planner
        // re-expresses the patch, so predicting it from the authored text would be a lie), and from the
        // current item version. An unchanged seed and patch reuse the same tuple.
        Assert.Equal(
            PackNarrowedVersion.Derive(upgradedItemVersion, _world.Override(FormKey)),
            upgraded!.Version.ToString());
        if (upgradedItemVersion != "1.0.0")
            Assert.NotEqual(derived, upgraded.Version.ToString());
        Assert.Null(upgraded.Overlay.Description);
        // Equal seed + equal reattached patch may reuse the same immutable tuple.
        Assert.Equal(
            derived == upgraded.Version.ToString() ? FormDefinitionStatus.Published : FormDefinitionStatus.Withdrawn,
            (await _world.FormAtAsync(derived)).Status);
        var upgradeReplay = await _world.ProjectAsync();
        Assert.Empty(upgradeReplay.Refusals);
        Assert.Equal(0, upgradeReplay.FormDefinitionsPublished);
        Assert.Equal(1, upgradeReplay.FormDefinitionsAlreadyPresent);
        Assert.Equal(0, upgradeReplay.FormDefinitionsRetracted);
        // The unnarrowed tuple stays retracted across the upgrade; nothing republished the wider body.
        Assert.Equal(FormDefinitionStatus.Withdrawn, (await _world.FormAtAsync("1.0.0")).Status);

        // (5) The override is withdrawn (RFC-7396's identity patch changes nothing, so the projector
        //     treats it as no override at all): the UNNARROWED body returns at its own seed version and
        //     the derived tuple retires.
        var upgradedDerived = upgraded.Version.ToString();
        Assert.True(_world.Narrow(FormKey, "{}").Recorded);
        Assert.Empty((await _world.ProjectAsync()).Refusals);
        var restored = await _world.CurrentFormAsync();
        Assert.Equal(upgradedItemVersion, restored!.Version.ToString());
        Assert.NotNull(restored.Overlay.Description);
        Assert.Equal(FormDefinitionStatus.Withdrawn, (await _world.FormAtAsync(upgradedDerived)).Status);
        Assert.Equal(FormDefinitionStatus.Withdrawn, (await _world.FormAtAsync(derived)).Status);

        var restoredReplay = await _world.ProjectAsync();
        Assert.Empty(restoredReplay.Refusals);
        Assert.Equal(0, restoredReplay.FormDefinitionsPublished);
        Assert.Equal(1, restoredReplay.FormDefinitionsAlreadyPresent);
        Assert.Equal(0, restoredReplay.FormDefinitionsRetracted);
        Assert.Equal(upgradedItemVersion, (await _world.CurrentFormAsync())!.Version.ToString());
    }

    [Theory(DisplayName = "208 s4b: a narrowing of a PUBLISHED pack workflow projects at a derived version, retracts the unnarrowed tuple, replays as a no-op, follows the upgrade, and is undone by removing the override")]
    [InlineData("1.1.0")]
    [InlineData("1.0.1")]
    [InlineData("1.0.0")]
    public async Task A_Narrowed_Workflow_Publishes_At_A_Derived_Version_And_The_Unnarrowed_Tuple_Retracts(string upgradedItemVersion)
    {
        _world.Add(WorkflowKey, PackContentKind.WorkflowDefinition, WorkflowSeed, "1.0.0");
        await _world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await _world.ProjectAsync()).Refusals);

        var seedWorkflow = await _world.CurrentWorkflowAsync();
        Assert.Equal("1.0.0", seedWorkflow!.Version);
        Assert.Equal(3, StateCount(seedWorkflow));

        Assert.True(_world.Narrow(WorkflowKey, WorkflowNarrowing).Recorded);
        var pass = await _world.ProjectAsync();
        Assert.Empty(pass.Refusals);

        var narrowed = await _world.CurrentWorkflowAsync();
        Assert.Equal(2, StateCount(narrowed!));
        var derived = PackNarrowedVersion.Derive("1.0.0", JsonNode.Parse(WorkflowNarrowing)!);
        Assert.Equal(derived, narrowed!.Version);

        Assert.Equal(1, pass.WorkflowDefinitionsRetracted);
        Assert.Equal(
            WorkflowDefinitionStatus.Withdrawn,
            (await _world.WorkflowAtAsync("1.0.0")).Status);

        var replay = await _world.ProjectAsync();
        Assert.Empty(replay.Refusals);
        Assert.Equal(0, replay.WorkflowDefinitionsPublished);
        Assert.Equal(1, replay.WorkflowDefinitionsAlreadyPresent);
        Assert.Equal(0, replay.WorkflowDefinitionsRetracted);
        Assert.Equal(derived, (await _world.CurrentWorkflowAsync())!.Version);

        _world.Replace((WorkflowKey, PackContentKind.WorkflowDefinition, WorkflowSeed, upgradedItemVersion));
        await _world.InstallAndActivateAsync("1.1.0");
        Assert.Empty((await _world.ProjectAsync()).Refusals);
        var upgraded = await _world.CurrentWorkflowAsync();
        Assert.Equal(
            PackNarrowedVersion.Derive(upgradedItemVersion, _world.Override(WorkflowKey)),
            upgraded!.Version);
        if (upgradedItemVersion != "1.0.0")
            Assert.NotEqual(derived, upgraded.Version);
        Assert.Equal(2, StateCount(upgraded));
        // Equal seed + equal reattached patch may reuse the same immutable tuple.
        Assert.Equal(
            derived == upgraded.Version ? WorkflowDefinitionStatus.Published : WorkflowDefinitionStatus.Withdrawn,
            (await _world.WorkflowAtAsync(derived)).Status);
        var upgradeReplay = await _world.ProjectAsync();
        Assert.Empty(upgradeReplay.Refusals);
        Assert.Equal(0, upgradeReplay.WorkflowDefinitionsPublished);
        Assert.Equal(1, upgradeReplay.WorkflowDefinitionsAlreadyPresent);
        Assert.Equal(0, upgradeReplay.WorkflowDefinitionsRetracted);
        Assert.Equal(
            WorkflowDefinitionStatus.Withdrawn, (await _world.WorkflowAtAsync("1.0.0")).Status);

        var upgradedDerived = upgraded.Version;
        Assert.True(_world.Narrow(WorkflowKey, "{}").Recorded);
        Assert.Empty((await _world.ProjectAsync()).Refusals);
        var restored = await _world.CurrentWorkflowAsync();
        Assert.Equal(upgradedItemVersion, restored!.Version);
        Assert.Equal(3, StateCount(restored));
        Assert.Equal(
            WorkflowDefinitionStatus.Withdrawn, (await _world.WorkflowAtAsync(upgradedDerived)).Status);
        Assert.Equal(WorkflowDefinitionStatus.Withdrawn, (await _world.WorkflowAtAsync(derived)).Status);

        var restoredReplay = await _world.ProjectAsync();
        Assert.Empty(restoredReplay.Refusals);
        Assert.Equal(0, restoredReplay.WorkflowDefinitionsPublished);
        Assert.Equal(1, restoredReplay.WorkflowDefinitionsAlreadyPresent);
        Assert.Equal(0, restoredReplay.WorkflowDefinitionsRetracted);
        Assert.Equal(upgradedItemVersion, (await _world.CurrentWorkflowAsync())!.Version);
    }

    [Fact(DisplayName = "208 s4b: the derived version is a pure function of the seed version and the override")]
    public void The_Derived_Version_Is_Deterministic_And_Clear_Of_The_Authoring_Range()
    {
        var patch = JsonNode.Parse(FormNarrowing)!;

        // Same seed + same override ⇒ same version, on any node and in any process: that is what makes
        // replay an exact no-op rather than a second projection at a fresh tuple.
        Assert.Equal(
            PackNarrowedVersion.Derive("1.0.0", patch),
            PackNarrowedVersion.Derive("1.0.0", JsonNode.Parse(FormNarrowing)!));

        // A different override, and a different seed version, each derive elsewhere.
        Assert.NotEqual(
            PackNarrowedVersion.Derive("1.0.0", patch),
            PackNarrowedVersion.Derive("1.0.0", JsonNode.Parse("""{"overlay":{"title":null}}""")!));
        Assert.NotEqual(
            PackNarrowedVersion.Derive("1.0.0", patch),
            PackNarrowedVersion.Derive("1.0.1", patch));

        // Clear of anything a pack or the authoring route mints, and ordered ABOVE the seed it narrows —
        // which is what makes the catalogue's highest-published read resolve to the narrowed body.
        var derived = SemanticVersion.Parse(PackNarrowedVersion.Derive("1.4.7", patch));
        Assert.Equal(1, derived.Major);
        Assert.Equal(4, derived.Minor);
        Assert.InRange(derived.Patch, 8 * 1_000_000, (9 * 1_000_000) - 1);
        Assert.True(derived > SemanticVersion.Parse("1.4.7"));

        // A version that cannot carry a tag is refused loudly, never silently wrapped.
        Assert.Throws<FormatException>(() => PackNarrowedVersion.Derive("1.0", patch));
        Assert.Throws<FormatException>(() => PackNarrowedVersion.Derive("1.0.2147", patch));
    }

    private static int StateCount(WorkflowDefinitionRecord record) =>
        record.Authored.GetProperty("states").GetArrayLength();

    /// <summary>One tenant, one pack key, the real exporter, installer and projector, over the node's own
    /// form + workflow stores and their authorized lifecycles — so "published at the derived version"
    /// is a claim about the composed host's admission, not a stub's.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly KeyPair _keyPair = KeyPair.Generate();
        private readonly PackFileCodec _codec = new();
        private readonly InMemoryPackInstallStore _store = new();
        private readonly List<PackContentSource> _contents = [];
        private readonly WebApplication _app;
        private int _activationCount;

        public World()
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = "Development" });
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

            Forms = _app.Services.GetRequiredService<IFormDefinitionStore>();
            Workflows = _app.Services.GetRequiredService<IWorkflowDefinitionStore>();
            var schemas = _app.Services.GetRequiredService<ISchemaRegistry>();
            var roleGate = _app.Services.GetRequiredService<IRoleGateAdmission>();

            Installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), _codec),
                _store,
                new PackWorkflowAdmissionAdapter(
                    new WorkflowAdmissionValidator(), roleGateAdmission: roleGate),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate());
            Projector = new PackSeedProjector(
                _store,
                _app.Services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                forms: Forms,
                schemas: schemas,
                workflows: Workflows,
                time: TimeProvider.System,
                authorizedForms: TestAuthorization.FormLifecycle(
                    Forms, TestAuthorization.AllowGate(), roleGate),
                authorizedWorkflows: TestAuthorization.WorkflowLifecycle(
                    Workflows, TestAuthorization.AllowGate(), roleGate));
            Context = new PackInstallContext(
                Tenant,
                new InMemoryPackTrustStore(
                    [new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current)]),
                PackRevocationList.Empty,
                Now,
                TimeSpan.FromDays(30),
                Principal: "test-operator");
        }

        public IFormDefinitionStore Forms { get; }

        public IWorkflowDefinitionStore Workflows { get; }

        public PackInstaller Installer { get; }

        public PackSeedProjector Projector { get; }

        public PackInstallContext Context { get; }

        public void Add(string key, PackContentKind kind, string content, string itemVersion) =>
            _contents.Add(new PackContentSource(key, kind, itemVersion, JsonNode.Parse(content)!));

        public void Replace(params (string Key, PackContentKind Kind, string Content, string ItemVersion)[] contents)
        {
            _contents.Clear();
            foreach (var (key, kind, content, itemVersion) in contents)
            {
                Add(key, kind, content, itemVersion);
            }
        }

        /// <summary>The override row as the store holds it — what the projector derives from.</summary>
        public JsonNode Override(string contentKey) =>
            _store.GetOverrides(Tenant, PackKey).Single(o => o.ContentKey == contentKey).OverlayPatch;

        public PackNarrowingOutcome Narrow(string contentKey, string patch) =>
            Installer.Narrow(Context, PackKey, contentKey, JsonNode.Parse(patch)!,
                TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now));

        /// <summary>The catalogue read the S6 surfaces use: the highest PUBLISHED revision at an address.
        /// Nothing here computes a version string.</summary>
        public ValueTask<FormDefinition?> CurrentFormAsync() =>
            Forms.GetCurrentPublishedAsync(new DefinitionAddress(Tenant, FormKey));

        public ValueTask<WorkflowDefinitionRecord?> CurrentWorkflowAsync() =>
            Workflows.GetCurrentPublishedAsync(new DefinitionAddress(Tenant, WorkflowKey));

        public ValueTask<FormDefinition> FormAtAsync(string version) =>
            Forms.GetAsync(new DefinitionCoordinates(Tenant, FormKey, version));

        public ValueTask<WorkflowDefinitionRecord> WorkflowAtAsync(string version) =>
            Workflows.GetAsync(new DefinitionCoordinates(Tenant, WorkflowKey, version));

        // Each boot/reconciliation carries a fresh authority. Never clear the process-wide nonce
        // guard: another test may be asserting that a consumed authority cannot be replayed.
        public async Task<PackSeedProjectionSummary> ProjectAsync()
        {
            var pack = _store.GetActive(Tenant, PackKey)!;
            var at = Now.AddMinutes(_activationCount - 1);
            var scope = ScopeExpression.Parse($"/records/{PackKey}");
            var principal = new ActorId("test-operator");
            var decision = await TestAuthorization.AllowGate().DecideAsync(new AuthorizationGateRequest(
                new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
                principal, Tenant, new AuthorizationTarget("pack", PackKey, scope), at));
            var authority = new PackProjectionAuthority(
                decision, PackKey, pack.Version, Tenant, principal, at);
            return await Projector.ProjectActivePacksAsync(authority);
        }

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
                    Description: "Exercises a narrowing of pack content that has already published.",
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
            var activation = Installer.Activate(Tenant, PackKey, packVersion, Now.AddMinutes(_activationCount++), "test-operator");
            Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
        }

        public async ValueTask DisposeAsync()
        {
            _keyPair.Dispose();
            await _app.DisposeAsync();
        }
    }
}
