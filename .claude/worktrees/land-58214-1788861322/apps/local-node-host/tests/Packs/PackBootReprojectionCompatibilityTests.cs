using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 160 — compatibility is re-checked at BOOT re-projection, not only at install. An ACTIVE pack
/// whose declared platform requirements the RUNNING build cannot satisfy (the platform moved since the
/// pack was admitted) is REFUSED by the projection pass: skipped whole with a structured pack-grain
/// refusal naming pack + reason + window — never projected, never a silent runtime degradation — and the
/// startup hosted service still completes (refuse the pack, not the process).
/// </summary>
public sealed class PackBootReprojectionCompatibilityTests : IDisposable
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000160");
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly KeyPair _keyPair = KeyPair.Generate();
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddLogging()
        .AddInMemoryAssetTypeSystem()
        .BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        _keyPair.Dispose();
    }

    private IEntityTypeRegistry Types => _services.GetRequiredService<IEntityTypeRegistry>();

    [Fact(DisplayName = "ticket 160: boot re-projection refuses an out-of-window ACTIVE pack, structurally")]
    public async Task Boot_Reprojection_Refuses_Out_Of_Window_Pack()
    {
        var store = CommitActivePackRequiring("forms.dynamic", minimumPlatformVersion: "9.0.0");

        // The running platform PROVIDES the capability but sits BELOW the declared version floor —
        // the upgrade-outside-the-window shape (the pack was admitted when the floor was met).
        var platform = new PackPlatformCompatibility(
            "1.0.0", [new PackProjectorCase(PackContentKind.FormDefinition, ["forms.dynamic"])]);
        var projector = new PackSeedProjector(
            store,
            Types,
            NullLogger<PackSeedProjector>.Instance,
            platform: platform, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(Tenant);

        var refusal = Assert.Single(summary.PlatformRefusals);
        Assert.Equal("test.boot-compat", refusal.PackKey);
        Assert.Equal("1.0.0", refusal.Version);
        Assert.Equal("1.0.0", refusal.PlatformVersion);
        var unmet = Assert.Single(refusal.Unmet);
        Assert.Equal("forms.dynamic", unmet.Capability);
        Assert.Equal("9.0.0", unmet.MinimumPlatformVersion);
        Assert.Equal(PackPlatformRequirementFailure.PlatformVersionFloor, unmet.Failure);

        // Refused means NOT projected: the pack's form item would otherwise register as deferred
        // (no forms store is composed here) — zero deferrals proves the pack was skipped whole.
        Assert.Equal(0, summary.FormDefinitionsDeferred);
    }

    [Fact(DisplayName = "ticket 160: an in-window ACTIVE pack still projects on the same pass")]
    public async Task Boot_Reprojection_Projects_In_Window_Pack()
    {
        var store = CommitActivePackRequiring("forms.dynamic", minimumPlatformVersion: "1.0.0");

        var platform = new PackPlatformCompatibility(
            "2.0.0", [new PackProjectorCase(PackContentKind.FormDefinition, ["forms.dynamic"])]);
        var projector = new PackSeedProjector(
            store,
            Types,
            NullLogger<PackSeedProjector>.Instance,
            platform: platform, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(Tenant);

        Assert.Empty(summary.PlatformRefusals);
        Assert.Equal(1, summary.FormDefinitionsDeferred); // reached the per-item dispatch (no forms store).
    }

    [Fact(DisplayName = "ticket 160: the refusal does not brick node boot — StartAsync completes")]
    public async Task Boot_Hosted_Service_Survives_The_Refusal()
    {
        var store = CommitActivePackRequiring("packs.pillar.future");
        var projector = new PackSeedProjector(
            store,
            Types,
            NullLogger<PackSeedProjector>.Instance,
            platform: new PackPlatformCompatibility("1.0.0", Array.Empty<PackProjectorCase>()), time: TimeProvider.System);
        var hosted = new PackSeedProjectionHostedService(
            new ThrowingProjectionReconciler(),
            new FixedActiveTeam(),
            NullLogger<PackSeedProjectionHostedService>.Instance);

        // Must not throw — refuse the pack, not the process.
        await hosted.StartAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "review: one corrupted seed row costs ONE item — the pass, its siblings, and other packs still project")]
    public async Task Malformed_Seed_Row_Does_Not_Abort_The_Pass()
    {
        var store = new InMemoryPackInstallStore();
        // The corrupted pack: one row of unparseable canonical JSON beside a healthy asset type.
        CommitActivePack(store, "test.corrupted",
            new PackSeedItem(
                "bad-row", PackContentKind.AssetTypeDefinition, "1.0.0", "{ this is not json",
                Harborline.Api.Foundation.Blobs.Cid.FromBytes("{ this is not json"u8.ToArray())),
            AssetSeed("corrupted.good", "Good Type In Corrupted Pack"));
        // A healthy sibling pack — pre-fix, the corrupted ROW aborted the whole tenant pass and
        // NOTHING projected, every pass (the requirement scan threw with no per-item guard).
        CommitActivePack(store, "test.healthy", AssetSeed("healthy.good", "Healthy Type"));

        var projector = new PackSeedProjector(
            store,
            Types,
            NullLogger<PackSeedProjector>.Instance,
            platform: new PackPlatformCompatibility(
                "1.0.0", [new PackProjectorCase(PackContentKind.AssetTypeDefinition, ["assets.registry"])]), time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(Tenant);

        Assert.Empty(summary.PlatformRefusals); // an unparseable row declares no requirements.
        Assert.Equal(2, summary.AssetTypesSeeded); // both healthy items — sibling AND other pack.
        Assert.Equal(1, summary.AssetTypesSkippedInvalid); // exactly one Invalid item, the pre-160 blast radius.
    }

    [Fact(DisplayName = "review: a platform-refused pack loses its contested-key ownership for the pass — the in-window sibling's copy projects")]
    public async Task Refused_Owner_Does_Not_Suppress_The_Compatible_Sibling()
    {
        const string sharedKey = "shared.equipment";
        var store = new InMemoryPackInstallStore();
        // pack.a: recorded OWNER of the shared key, but ACTIVE outside the platform window.
        CommitActivePack(store, "pack.a",
            [AssetSeed(sharedKey, "Equipment A")],
            capabilityRequirements: ["packs.pillar.future"]);
        // pack.b: in-window, ships the same key.
        CommitActivePack(store, "pack.b", AssetSeed(sharedKey, "Equipment B"));
        store.RecordKeyOwnership(Tenant, sharedKey, "pack.a");

        var projector = new PackSeedProjector(
            store,
            Types,
            NullLogger<PackSeedProjector>.Instance,
            platform: new PackPlatformCompatibility(
                "1.0.0", [new PackProjectorCase(PackContentKind.AssetTypeDefinition, ["assets.registry"])]), time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(Tenant);

        // pack.a is refused (unmet capability) — and with its ownership EXCLUDED from the pass,
        // pack.b's copy of the key projects instead of deferring to a pack that projects nothing.
        var refusal = Assert.Single(summary.PlatformRefusals);
        Assert.Equal("pack.a", refusal.PackKey);
        Assert.Equal(1, summary.AssetTypesSeeded);
        Assert.Equal(0, summary.AssetTypesOwnedByOtherPack);
        Assert.Equal(0, summary.AssetTypesContestedUnresolved);
    }

    /// <summary>A minimal VALID AssetTypeDefinition seed item.</summary>
    private static PackSeedItem AssetSeed(string key, string displayName)
    {
        var content = new JsonObject
        {
            ["id"] = key,
            ["displayName"] = displayName,
            ["traits"] = new JsonArray("Maintainable"),
        }.ToJsonString();
        return new PackSeedItem(
            key, PackContentKind.AssetTypeDefinition, "1.0.0", content,
            Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    private void CommitActivePack(
        InMemoryPackInstallStore store, string packKey, params PackSeedItem[] seeds)
        => CommitActivePack(store, packKey, seeds, capabilityRequirements: null);

    private void CommitActivePack(
        InMemoryPackInstallStore store,
        string packKey,
        IReadOnlyList<PackSeedItem> seeds,
        IReadOnlyList<string>? capabilityRequirements)
    {
        var pack = new InstalledPack(
            packKey,
            "1.0.0",
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            seeds,
            new Dictionary<string, int>(),
            Now,
            _keyPair.PrincipalId,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>(),
            CapabilityRequirements: capabilityRequirements);
        store.Commit(new PackInstallTransaction(
            Tenant,
            pack,
            new PackInstallWatermark(pack.PackKey, pack.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        store.Activate(Tenant, pack.PackKey, pack.Version);
    }

    /// <summary>Commits an ACTIVE installed pack whose single form seed declares the requirement —
    /// the persisted state a node boots over after the platform moved out of the pack's window.</summary>
    private InMemoryPackInstallStore CommitActivePackRequiring(
        string capability, string? minimumPlatformVersion = null)
    {
        var content = new JsonObject
        {
            ["title"] = "boot compat form",
            ["definitionEnvelope"] = new JsonObject
            {
                ["requires"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["capability"] = capability,
                        ["minimumPlatformVersion"] = minimumPlatformVersion,
                    },
                },
            },
        }.ToJsonString();

        var seed = new PackSeedItem(
            "boot-compat-form",
            PackContentKind.FormDefinition,
            "1.0.0",
            content,
            Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(content)));
        var pack = new InstalledPack(
            "test.boot-compat",
            "1.0.0",
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            [seed],
            new Dictionary<string, int>(),
            Now,
            _keyPair.PrincipalId,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());

        var store = new InMemoryPackInstallStore();
        store.Commit(new PackInstallTransaction(
            Tenant,
            pack,
            new PackInstallWatermark(pack.PackKey, pack.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        // Flip Active via the STORE (the pack was legitimately activated when the platform was still
        // inside the window; the installer's activate guard is not the seam under test here).
        store.Activate(Tenant, pack.PackKey, pack.Version);
        return store;
    }

    private sealed class FixedActiveTeam : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = new(
            new TeamId(Guid.Parse("7e570000-0000-0000-0000-000000000160")),
            "Boot Compat Team",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ThrowingProjectionReconciler : IPackProjectionReconciler
    {
        public void AttachProjector(IPackProjectionDispatcher projector) { }

        public void ReconcilePending(CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated refused startup projection.");
    }
}
