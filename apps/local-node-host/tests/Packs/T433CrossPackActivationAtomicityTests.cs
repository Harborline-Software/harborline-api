using System.Text;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class T433CrossPackActivationAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_different_packs_cannot_publish_against_a_stale_provider_slot_guard(bool durable)
    {
        var tenant = new TenantId("t433-competing-providers");
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        await using var database = durable ? await PacksTestStore.CreateAsync() : null;
        IPackInstallMutationStore store = database is null ? new InMemoryPackInstallStore() : new DurablePackInstallStore(database.Factory);
        using var reader = new RendezvousReader(store);
        var types = new InMemoryEntityTypeRegistry(new InMemoryRegistryAuditLog());
        var projector = new PackSeedProjector(store, types, NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);
        PackInstaller CreateInstaller() => new(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()),
            reader, store, (IPackProjectionAdmissionStore)store, new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Authorization.TestAuthorization.AllowGate(), projector);
        var alphaInstaller = CreateInstaller();
        var betaInstaller = CreateInstaller();
        foreach (var key in new[] { "provider.alpha", "provider.beta" })
        {
            var json = $$"""{"id":"{{key}}.type","displayName":"Provider type","traits":["Maintainable"]}""";
            var seed = new PackSeedItem(key + ".type", PackContentKind.AssetTypeDefinition, "1.0.0", json,
                Cid.FromBytes(Encoding.UTF8.GetBytes(json)));
            var pack = new InstalledPack(key, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft,
                [seed], new Dictionary<string, int>(), now,
                PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster, [],
                ProviderSlot: "t433.providers");
            store.Commit(new(tenant, pack, new(key, "1.0.0", new Dictionary<string, int>()), []));
        }

        var outcomes = await Task.WhenAll(
            Task.Factory.StartNew(() => alphaInstaller.Activate(tenant, "provider.alpha", "1.0.0", now, "operator"),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            Task.Factory.StartNew(() => betaInstaller.Activate(tenant, "provider.beta", "1.0.0", now, "operator"),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .WaitAsync(TimeSpan.FromSeconds(15));

        var winner = Assert.Single(outcomes, outcome => outcome.Activated);
        var refused = Assert.Single(outcomes, outcome => !outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivateProviderSlotOccupied, refused.Error);
        Assert.Equal(winner.PackKey, Assert.Single(store.ListInstalled(tenant), pack => pack.Lifecycle == PackLifecycleState.Active).PackKey);
        Assert.Equal(winner.PackKey + ".type", Assert.Single(types.ListSeeds()).Id.Value);
        Assert.Equal(PackLifecycleState.Draft, store.GetVersion(tenant, refused.PackKey, "1.0.0")!.Lifecycle);
        Assert.Null(store.GetActive(tenant, refused.PackKey));
        Assert.Empty(((IPackProjectionAdmissionStore)store).ListIncompleteProjectionAdmissions());
        if (database is not null)
        {
            await using var persisted = database.CreateContext();
            var admission = Assert.Single(await persisted.ProjectionAdmissions.ToListAsync());
            Assert.Equal(winner.PackKey, admission.PackId);
            Assert.True(admission.Projected);
        }
    }

    // Pause after each caller has captured the preliminary collision snapshot. Both have already
    // observed the slot as empty; an authoritative read under the writer must reject the loser.
    private sealed class RendezvousReader(IPackInstallStore inner) : IPackInstallStore, IDisposable
    {
        private readonly Barrier rendezvous = new(2);
        private readonly ThreadLocal<int> reads = new(() => 0);
        public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant)
        {
            var snapshot = inner.ListInstalled(tenant);
            if (++reads.Value == 3 && !rendezvous.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Both provider activations must capture the initial composition.");
            return snapshot;
        }
        public InstalledPack? GetActive(TenantId tenant, string key) => inner.GetActive(tenant, key);
        public InstalledPack? GetVersion(TenantId tenant, string key, string version) => inner.GetVersion(tenant, key, version);
        public bool AnyInstalled() => inner.AnyInstalled();
        public PackInstallWatermark? GetWatermark(TenantId tenant, string key) => inner.GetWatermark(tenant, key);
        public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string key) => inner.GetOverrides(tenant, key);
        public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) => inner.GetKeyOwnership(tenant);
        public void Dispose() { rendezvous.Dispose(); reads.Dispose(); }
    }
}
