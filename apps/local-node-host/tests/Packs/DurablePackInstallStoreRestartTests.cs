using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// F5 (migration-update-architecture D5.2 / D5.3) — the durable <see cref="DurablePackInstallStore"/> MUST make an
/// installed + activated pack, its S-8 watermark, its tenant overrides, and its per-key ownership choices SURVIVE a
/// node restart, so boot re-projects the still-installed packs through the existing <c>PackSeedProjector</c> path
/// with NO from-empty re-seed and the downgrade/floor refusal keeps its teeth. A "restart" here is a fresh store
/// object graph over a fresh context factory pointed at the SAME on-disk encrypted file (Pooling=False, so the
/// prior block's disposal already released every handle) — faithfully modelling a process recycle.
/// </summary>
public sealed class DurablePackInstallStoreRestartTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-A");
    private static readonly DateTimeOffset InstalledAt = DateTimeOffset.UnixEpoch.AddYears(56).AddDays(188);
    private const string PackKey = "acme.accounting";

    [Fact(DisplayName = "F5: an installed+activated pack + its S-8 watermark + overrides + key-ownership SURVIVE a restart — the pack is still installed & Active, its seed content projects, and the watermark persists (no re-install, no reset)")]
    public async Task InstalledActivatedPack_SurvivesRestart()
    {
        await using var origin = await PacksTestStore.CreateAsync();

        // ── BEFORE restart: install v1.0.0 (Draft), record an override + a key-ownership choice, then activate. ──
        {
            var store = new DurablePackInstallStore(origin.Factory);

            var floors = new Dictionary<string, int>(StringComparer.Ordinal) { ["pii"] = 3, ["retention"] = 2 };
            var seed = Seed("type.invoice", "{\"kind\":\"asset-type\",\"label\":\"Invoice\"}");
            var pack = BuildPack("1.0.0", PackLifecycleState.Draft, floors, new[] { seed }, providerSlot: "accounting");
            var watermark = new PackInstallWatermark(PackKey, "1.0.0", floors);

            store.Commit(new PackInstallTransaction(Tenant, pack, watermark, Array.Empty<PackTenantOverride>()));
            store.SaveOverride(Tenant, PackKey, new PackTenantOverride("type.invoice", JsonNode.Parse("{\"label\":\"Bill\"}")!));
            store.RecordKeyOwnership(Tenant, "type.invoice", PackKey);
            store.Activate(Tenant, PackKey, "1.0.0");

            // Sanity BEFORE restart: the pack is installed and Active.
            var active = store.GetActive(Tenant, PackKey);
            Assert.NotNull(active);
            Assert.Equal(PackLifecycleState.Active, active!.Lifecycle);
        }

        // ── Simulate a process restart: rebuild the whole store object graph over the SAME on-disk encrypted file. ──
        await using var afterRestart = PacksTestStore.Reopen(origin);
        {
            var store = new DurablePackInstallStore(afterRestart.Factory);

            // ASSERTION 1 — STILL INSTALLED & ACTIVE (no from-empty re-seed). This is exactly what the seed projector
            // consumes at boot: ListInstalled(...).Where(Lifecycle == Active).
            var active = store.GetActive(Tenant, PackKey);
            Assert.NotNull(active);
            Assert.Equal("1.0.0", active!.Version);
            Assert.Equal(PackLifecycleState.Active, active.Lifecycle);
            Assert.Contains(store.ListInstalled(Tenant),
                p => p.PackKey == PackKey && p.Version == "1.0.0" && p.Lifecycle == PackLifecycleState.Active);

            // ASSERTION 2 — SEED CONTENT PROJECTS: the immutable seed layer round-tripped byte-faithfully (key,
            // canonical JSON, content address), plus provenance (signer/epoch/scope/floors/provider slot).
            var item = Assert.Single(active.SeedItems);
            Assert.Equal("type.invoice", item.Key);
            Assert.Equal(PackContentKind.AssetTypeDefinition, item.Kind);
            Assert.Equal("{\"kind\":\"asset-type\",\"label\":\"Invoice\"}", item.CanonicalJson);
            Assert.Equal(Cid.FromBytes(Encoding.UTF8.GetBytes("{\"kind\":\"asset-type\",\"label\":\"Invoice\"}")), item.ContentAddress);
            Assert.Equal(3, active.SafetyFloors["pii"]);
            Assert.Equal(2, active.SafetyFloors["retention"]);
            Assert.Equal("accounting", active.ProviderSlot);
            Assert.Equal(PackScopeTier.Horizontal, active.ScopeTier);
            Assert.Equal(Signer(), active.SignerKeyId);
            Assert.Equal(7L, active.Epoch);
            Assert.Equal(TrustScope.OwnRoster, active.VouchingScope);
            Assert.Equal(InstalledAt, active.InstalledAtUtc);

            // ASSERTION 3 — S-8 WATERMARK PERSISTED (this is what gives the downgrade/floor refusal its teeth across
            // the very updates it polices). Without the durable row, GetWatermark would be null after restart and the
            // engine could not refuse a downgrade.
            var wm = store.GetWatermark(Tenant, PackKey);
            Assert.NotNull(wm);
            Assert.Equal("1.0.0", wm!.Version);
            Assert.Equal(3, wm.Floors["pii"]);
            Assert.Equal(2, wm.Floors["retention"]);

            // ASSERTION 4 — TENANT OVERRIDE SURVIVED (the S-10 re-attach source for the next upgrade).
            var overrides = store.GetOverrides(Tenant, PackKey);
            var ov = Assert.Single(overrides);
            Assert.Equal("type.invoice", ov.ContentKey);
            Assert.Equal("Bill", ov.OverlayPatch["label"]!.GetValue<string>());

            // ASSERTION 5 — KEY-OWNERSHIP CHOICE SURVIVED (activation/projection honor the SAME chosen owner).
            var ownership = store.GetKeyOwnership(Tenant);
            Assert.Equal(PackKey, ownership["type.invoice"]);

            // ASSERTION 6 — MONOTONIC, no downgrade acceptance at the store layer: committing an UPGRADE (v2.0.0)
            // advances the persisted watermark; the watermark only ever moves UP and is the authority the engine
            // reads to refuse a later downgrade.
            var v2Floors = new Dictionary<string, int>(StringComparer.Ordinal) { ["pii"] = 4, ["retention"] = 2 };
            var v2 = BuildPack("2.0.0", PackLifecycleState.Draft, v2Floors,
                new[] { Seed("type.invoice", "{\"kind\":\"asset-type\",\"label\":\"Invoice v2\"}") }, providerSlot: "accounting");
            var v2Watermark = new PackInstallWatermark(PackKey, "2.0.0",
                new Dictionary<string, int>(StringComparer.Ordinal) { ["pii"] = 4, ["retention"] = 2 });
            store.Commit(new PackInstallTransaction(Tenant, v2, v2Watermark, overrides));
            store.Activate(Tenant, PackKey, "2.0.0");

            var wm2 = store.GetWatermark(Tenant, PackKey);
            Assert.Equal("2.0.0", wm2!.Version);
            Assert.Equal(4, wm2.Floors["pii"]);

            // The prior version's IMMUTABLE seed layer is RETAINED (S-2 — supersede is a pointer flip, not a delete),
            // so a rollback within the ADR 0011 window is loadable.
            var v1AfterUpgrade = store.GetVersion(Tenant, PackKey, "1.0.0");
            Assert.NotNull(v1AfterUpgrade);
            Assert.Equal(PackLifecycleState.Superseded, v1AfterUpgrade!.Lifecycle);
        }
    }

    [Fact(DisplayName = "F5 unit: a fresh store is empty — GetActive/GetVersion/GetWatermark are null and lists are empty")]
    public async Task FreshStore_IsEmpty()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(origin.Factory);

        Assert.Null(store.GetActive(Tenant, PackKey));
        Assert.Null(store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(store.GetWatermark(Tenant, PackKey));
        Assert.Empty(store.ListInstalled(Tenant));
        Assert.Empty(store.GetOverrides(Tenant, PackKey));
        Assert.Empty(store.GetKeyOwnership(Tenant));
    }

    [Fact(DisplayName = "F5 unit: Commit installs in Draft (GetActive null until Activate), then Activate makes it the resolving version")]
    public async Task Commit_IsDraft_UntilActivate()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(origin.Factory);

        var floors = new Dictionary<string, int>(StringComparer.Ordinal);
        var pack = BuildPack("1.0.0", PackLifecycleState.Draft, floors, new[] { Seed("k", "{}") });
        store.Commit(new PackInstallTransaction(Tenant, pack, new PackInstallWatermark(PackKey, "1.0.0", floors), Array.Empty<PackTenantOverride>()));

        // Installed but not active (Draft): GetVersion sees it, GetActive does not.
        Assert.NotNull(store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(store.GetActive(Tenant, PackKey));

        store.Activate(Tenant, PackKey, "1.0.0");
        Assert.Equal("1.0.0", store.GetActive(Tenant, PackKey)!.Version);
    }

    [Fact(DisplayName = "F5 unit: Activate supersedes the prior Active version (S-2 pointer flip) — the prior seed layer is retained, only one version is Active")]
    public async Task Activate_Supersedes_Prior()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(origin.Factory);
        var floors = new Dictionary<string, int>(StringComparer.Ordinal);

        store.Commit(new PackInstallTransaction(Tenant, BuildPack("1.0.0", PackLifecycleState.Draft, floors, new[] { Seed("k", "{}") }),
            new PackInstallWatermark(PackKey, "1.0.0", floors), Array.Empty<PackTenantOverride>()));
        store.Activate(Tenant, PackKey, "1.0.0");

        store.Commit(new PackInstallTransaction(Tenant, BuildPack("2.0.0", PackLifecycleState.Draft, floors, new[] { Seed("k", "{}") }),
            new PackInstallWatermark(PackKey, "2.0.0", floors), Array.Empty<PackTenantOverride>()));
        store.Activate(Tenant, PackKey, "2.0.0");

        Assert.Equal("2.0.0", store.GetActive(Tenant, PackKey)!.Version);
        Assert.Equal(PackLifecycleState.Superseded, store.GetVersion(Tenant, PackKey, "1.0.0")!.Lifecycle);
        Assert.Single(store.ListInstalled(Tenant), p => p.Lifecycle == PackLifecycleState.Active);
    }

    [Fact(DisplayName = "F5 unit: Deactivate persists an Inactive pointer across restart without deleting seeds, watermark, or overrides")]
    public async Task Deactivate_Persists_Inactive_State_AcrossRestart()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var floors = new Dictionary<string, int>(StringComparer.Ordinal) { ["retention"] = 2 };

        {
            var store = new DurablePackInstallStore(origin.Factory);
            store.Commit(new PackInstallTransaction(
                Tenant,
                BuildPack("1.0.0", PackLifecycleState.Draft, floors, new[] { Seed("notes.entry", "{}") }),
                new PackInstallWatermark(PackKey, "1.0.0", floors),
                Array.Empty<PackTenantOverride>()));
            store.SaveOverride(Tenant, PackKey,
                new PackTenantOverride("notes.entry", JsonNode.Parse("{\"label\":\"Tenant note\"}")!));
            store.Activate(Tenant, PackKey, "1.0.0");
            store.Deactivate(Tenant, PackKey, "1.0.0");

            Assert.Null(store.GetActive(Tenant, PackKey));
            Assert.Equal(PackLifecycleState.Inactive,
                store.GetVersion(Tenant, PackKey, "1.0.0")!.Lifecycle);
        }

        await using var afterRestart = PacksTestStore.Reopen(origin);
        {
            var store = new DurablePackInstallStore(afterRestart.Factory);
            Assert.Null(store.GetActive(Tenant, PackKey));
            var inactive = store.GetVersion(Tenant, PackKey, "1.0.0");
            Assert.NotNull(inactive);
            Assert.Equal(PackLifecycleState.Inactive, inactive!.Lifecycle);
            Assert.Single(inactive.SeedItems);
            Assert.Equal(2, store.GetWatermark(Tenant, PackKey)!.Floors["retention"]);
            Assert.Equal("Tenant note",
                Assert.Single(store.GetOverrides(Tenant, PackKey)).OverlayPatch["label"]!.GetValue<string>());

            store.Activate(Tenant, PackKey, "1.0.0");
            Assert.Equal(PackLifecycleState.Active, store.GetActive(Tenant, PackKey)!.Lifecycle);
        }
    }

    [Fact(DisplayName = "F5 unit: Commit REPLACES the pack key's overrides with the re-attached set atomically (S-10 total re-attach); SaveOverride upserts a single content key")]
    public async Task Commit_Replaces_Overrides_SaveOverride_Upserts()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(origin.Factory);
        var floors = new Dictionary<string, int>(StringComparer.Ordinal);

        store.Commit(new PackInstallTransaction(Tenant, BuildPack("1.0.0", PackLifecycleState.Draft, floors, new[] { Seed("a", "{}"), Seed("b", "{}") }),
            new PackInstallWatermark(PackKey, "1.0.0", floors), Array.Empty<PackTenantOverride>()));

        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("a", JsonNode.Parse("{\"x\":1}")!));
        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("a", JsonNode.Parse("{\"x\":2}")!)); // replace same key
        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("b", JsonNode.Parse("{\"y\":9}")!));

        var afterSaves = store.GetOverrides(Tenant, PackKey);
        Assert.Equal(2, afterSaves.Count);
        Assert.Equal(2, afterSaves.Single(o => o.ContentKey == "a").OverlayPatch["x"]!.GetValue<int>());

        // A Commit (upgrade) REPLACES the whole override set with the re-attached list — here just "a" survives.
        var reattached = new[] { new PackTenantOverride("a", JsonNode.Parse("{\"x\":2}")!) };
        store.Commit(new PackInstallTransaction(Tenant, BuildPack("2.0.0", PackLifecycleState.Draft, floors, new[] { Seed("a", "{}") }),
            new PackInstallWatermark(PackKey, "2.0.0", floors), reattached));

        var afterCommit = store.GetOverrides(Tenant, PackKey);
        Assert.Single(afterCommit);
        Assert.Equal("a", afterCommit[0].ContentKey);
    }

    [Fact(DisplayName = "F5 unit: RecordKeyOwnership upserts — a later choice on the same content key replaces the prior one")]
    public async Task RecordKeyOwnership_Upserts()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(origin.Factory);

        store.RecordKeyOwnership(Tenant, "shared.key", "pack.one");
        Assert.Equal("pack.one", store.GetKeyOwnership(Tenant)["shared.key"]);

        store.RecordKeyOwnership(Tenant, "shared.key", "pack.two");
        var ownership = store.GetKeyOwnership(Tenant);
        Assert.Single(ownership);
        Assert.Equal("pack.two", ownership["shared.key"]);
    }

    [Fact(DisplayName = "projection admission evidence and completion survive restart")]
    public async Task ProjectionAdmission_SurvivesRestartUntilMarkedProjected()
    {
        await using var origin = await PacksTestStore.CreateAsync();
        var admission = new PackProjectionAdmission(
            Guid.NewGuid(), PackKey, "1.0.0", Tenant, new ActorId("operator"), InstalledAt,
            ["grant-1", "definition-1"], Projected: false);
        var originStore = new DurablePackInstallStore(origin.Factory);
        var floors = new Dictionary<string, int>();
        originStore.Commit(new PackInstallTransaction(
            Tenant,
            BuildPack("1.0.0", PackLifecycleState.Draft, floors, [Seed("k", "{}")]),
            new PackInstallWatermark(PackKey, "1.0.0", floors),
            Array.Empty<PackTenantOverride>()));
        ((IPackProjectionAdmissionStore)originStore).ActivateAndRecordProjectionAdmission(
            Tenant, PackKey, "1.0.0", admission);

        await using var reopened = PacksTestStore.Reopen(origin);
        var restarted = new DurablePackInstallStore(reopened.Factory);
        var admissionStore = (IPackProjectionAdmissionStore)restarted;
        var pending = Assert.Single(admissionStore.ListIncompleteProjectionAdmissions());
        Assert.Equal(admission.AdmissionId, pending.AdmissionId);
        Assert.Equal(admission.PackId, pending.PackId);
        Assert.Equal(admission.PackVersion, pending.PackVersion);
        Assert.Equal(admission.Tenant, pending.Tenant);
        Assert.Equal(admission.Principal, pending.Principal);
        Assert.Equal(admission.Instant, pending.Instant);
        Assert.Equal(admission.DerivationIds, pending.DerivationIds);

        admissionStore.MarkProjectionCompleted(admission.AdmissionId);
        Assert.Empty(admissionStore.ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "in-memory activation and admission evidence are one atomic state publication")]
    public void InMemoryActivation_AdmissionFailureLeavesPackUntransitioned()
    {
        var store = new InMemoryPackInstallStore();
        var floors = new Dictionary<string, int>();
        store.Commit(new PackInstallTransaction(
            Tenant,
            BuildPack("1.0.0", PackLifecycleState.Draft, floors, [Seed("k", "{}")]),
            new PackInstallWatermark(PackKey, "1.0.0", floors),
            Array.Empty<PackTenantOverride>()));
        AssertAtomicAdmissionFailureLeavesInactive(store, (IPackProjectionAdmissionStore)store);
    }

    [Fact(DisplayName = "durable activation and admission evidence share one HomeEpoch fence transaction")]
    public async Task DurableActivation_AdmissionFailureLeavesPackUntransitioned()
    {
        await using var database = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(database.Factory);
        var floors = new Dictionary<string, int>();
        store.Commit(new PackInstallTransaction(
            Tenant,
            BuildPack("1.0.0", PackLifecycleState.Draft, floors, [Seed("k", "{}")]),
            new PackInstallWatermark(PackKey, "1.0.0", floors),
            Array.Empty<PackTenantOverride>()));
        AssertAtomicAdmissionFailureLeavesInactive(store, (IPackProjectionAdmissionStore)store);
    }

    [Fact(DisplayName = "installer activation surfaces an admission-store failure without falsely reporting not-installed and leaves the row untransitioned")]
    public void PackInstaller_ActivationAdmissionStoreFailure_IsNotMisclassifiedAndLeavesPackUntransitioned()
    {
        var inner = new InMemoryPackInstallStore();
        var floors = new Dictionary<string, int>();
        inner.Commit(new PackInstallTransaction(
            Tenant,
            BuildPack("1.0.0", PackLifecycleState.Draft, floors, [Seed("k", "{}")]),
            new PackInstallWatermark(PackKey, "1.0.0", floors),
            Array.Empty<PackTenantOverride>()));
        var store = new ThrowingAdmissionPackInstallStore(inner);
        var installer = new PackInstaller(
            Substitute.For<IPackVerifier>(), store, Substitute.For<IPackContentAdmission>(),
            Substitute.For<IPackInstallAudit>(), TestAuthorization.AllowGate());
        var context = new PackInstallContext(
            Tenant, Substitute.For<IPackTrustStore>(), Substitute.For<IPackRevocationList>(),
            InstalledAt, TimeSpan.FromHours(1), Principal: "operator");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            installer.Activate(context, PackKey, "1.0.0"));

        Assert.Equal(ThrowingAdmissionPackInstallStore.Failure, failure.Message);
        Assert.NotEqual(PackInstallCodes.ActivateNotInstalled, failure.Message);
        Assert.Null(inner.GetActive(Tenant, PackKey));
        Assert.Equal(PackLifecycleState.Draft, inner.GetVersion(Tenant, PackKey, "1.0.0")!.Lifecycle);
    }

    private static void AssertAtomicAdmissionFailureLeavesInactive(
        IPackInstallMutationStore store,
        IPackProjectionAdmissionStore admissionStore)
    {
        var admission = new PackProjectionAdmission(
            Guid.NewGuid(), PackKey, "1.0.0", Tenant, new ActorId("operator"), InstalledAt,
            ["grant-1", "definition-1"], Projected: false);
        admissionStore.ActivateAndRecordProjectionAdmission(Tenant, PackKey, "1.0.0", admission);
        store.Deactivate(Tenant, PackKey, "1.0.0");

        Assert.Throws<InvalidOperationException>(() =>
            admissionStore.ActivateAndRecordProjectionAdmission(Tenant, PackKey, "1.0.0", admission));

        Assert.Null(store.GetActive(Tenant, PackKey));
        Assert.Equal(PackLifecycleState.Inactive, store.GetVersion(Tenant, PackKey, "1.0.0")!.Lifecycle);
        Assert.Single(admissionStore.ListIncompleteProjectionAdmissions());
    }

    private sealed class ThrowingAdmissionPackInstallStore(InMemoryPackInstallStore inner)
        : IPackInstallMutationStore, IPackProjectionAdmissionStore
    {
        internal const string Failure = "simulated admission-store failure";

        public InstalledPack? GetActive(TenantId tenant, string packKey) => inner.GetActive(tenant, packKey);
        public InstalledPack? GetVersion(TenantId tenant, string packKey, string version) =>
            inner.GetVersion(tenant, packKey, version);
        public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant) => inner.ListInstalled(tenant);
        public PackInstallWatermark? GetWatermark(TenantId tenant, string packKey) => inner.GetWatermark(tenant, packKey);
        public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey) =>
            inner.GetOverrides(tenant, packKey);
        public void SaveOverride(TenantId tenant, string packKey, PackTenantOverride tenantOverride) =>
            inner.SaveOverride(tenant, packKey, tenantOverride);
        public void Commit(PackInstallTransaction transaction) => inner.Commit(transaction);
        public void Activate(TenantId tenant, string packKey, string version) => inner.Activate(tenant, packKey, version);
        public void Deactivate(TenantId tenant, string packKey, string version) => inner.Deactivate(tenant, packKey, version);
        public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) => inner.GetKeyOwnership(tenant);
        public void RecordKeyOwnership(TenantId tenant, string contentKey, string owningPackKey) =>
            inner.RecordKeyOwnership(tenant, contentKey, owningPackKey);

        void IPackProjectionAdmissionStore.ActivateAndRecordProjectionAdmission(
            TenantId tenant, string packKey, string version, PackProjectionAdmission admission) =>
            throw new InvalidOperationException(Failure);
        void IPackProjectionAdmissionStore.DeactivateAndRecordProjectionAdmission(
            TenantId tenant, string packKey, string version, PackProjectionAdmission admission) =>
            throw new InvalidOperationException(Failure);
        IReadOnlyList<PackProjectionAdmission> IPackProjectionAdmissionStore.ListIncompleteProjectionAdmissions() =>
            ((IPackProjectionAdmissionStore)inner).ListIncompleteProjectionAdmissions();
        void IPackProjectionAdmissionStore.MarkProjectionCompleted(Guid admissionId) =>
            ((IPackProjectionAdmissionStore)inner).MarkProjectionCompleted(admissionId);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────

    private static PackSeedItem Seed(string key, string canonicalJson) =>
        new(key, PackContentKind.AssetTypeDefinition, "1.0.0", canonicalJson, Cid.FromBytes(Encoding.UTF8.GetBytes(canonicalJson)));

    private static PrincipalId Signer()
    {
        var bytes = new byte[PrincipalId.LengthInBytes];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }
        return PrincipalId.FromBytes(bytes);
    }

    private static InstalledPack BuildPack(
        string version,
        PackLifecycleState lifecycle,
        IReadOnlyDictionary<string, int> floors,
        IReadOnlyList<PackSeedItem> seeds,
        string? providerSlot = null) =>
        new(
            PackKey, version, PackScopeTier.Horizontal, lifecycle, seeds, floors, InstalledAt,
            Signer(), Epoch: 7L, TrustScope.OwnRoster,
            Dependencies: new[] { new PackDependencyRef("acme.base", "1.0.0") },
            ProviderSlot: providerSlot);
}
