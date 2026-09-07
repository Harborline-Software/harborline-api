using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.LocalNodeHost.BackupRestore;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Tests.BackupRestore;

public sealed class NodeRehostCutoverTests : IAsyncLifetime
{
    private readonly KeyPair _adminA = KeyPair.Generate();
    private readonly KeyPair _adminB = KeyPair.Generate();
    private string _directory = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"harborline-rehost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_directory, "rehost.db")};Pooling=False"));
        var provider = services.BuildServiceProvider();
        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        _adminA.Dispose();
        _adminB.Dispose();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the isolated test store.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestoreAsync_FencePreventsOldInstanceActingAsHomeAfterCutover()
    {
        const string tenantId = "tenant-a";
        var epochStore = new HomeEfHomeEpochStore(_factory, new Ed25519Verifier());
        await epochStore.AdvanceAsync(SignedBump(
            tenantId,
            epoch: 1,
            previous: 0,
            homeDeviceId: "old-node",
            HomePromotionKind.Genesis));
        var service = new NodeRehostService(
            new FixedTrusteeKeyRecovery(),
            new NoOpRootSeedRestorer(),
            new FixedIdentityFactory(new NodeIdentity("replacement-node", new byte[32], new byte[32])),
            new FixedGrantProvider(),
            new FixedCanonicalSource(),
            new FixedPromotionAuthority(() => SignedBump(
                tenantId,
                epoch: 2,
                previous: 1,
                homeDeviceId: "replacement-node",
                HomePromotionKind.RecoveryFailover,
                _adminB)),
            epochStore);

        var result = await service.RestoreAsync(
            new NodeRehostRequest(tenantId, "old-node"),
            CancellationToken.None);

        Assert.Equal(
            new PendingHomeEpochAssertion(tenantId, 2, "replacement-node"),
            result.HomeAssertion);
        await using var oldInstanceWrite = await _factory.CreateDbContextAsync();
        var rejection = await Assert.ThrowsAsync<StaleHomeEpochException>(() =>
            HomeEpochFence.AssertNotStaleAsync(
                oldInstanceWrite,
                new PendingHomeEpochAssertion(tenantId, 1, "old-node"),
                CancellationToken.None));
        Assert.Equal(1, rejection.AssertedEpoch);
        Assert.Equal(2, rejection.CurrentEpoch);
    }

    private HomeEpochRecord SignedBump(
        string tenantId,
        long epoch,
        long previous,
        string homeDeviceId,
        HomePromotionKind kind,
        KeyPair? coApprover = null)
    {
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();
        var payload = HomeEpochSignaturePayload.For(
            tenantId,
            epoch,
            previous,
            homeDeviceId,
            kind);
        var signature = new Ed25519Signer(_adminA).SignAsync(payload, issuedAt, nonce)
            .AsTask().GetAwaiter().GetResult().Signature.ToBase64Url();

        return new HomeEpochRecord
        {
            TenantId = tenantId,
            EpochNumber = epoch,
            PreviousEpochNumber = previous,
            HomeDeviceId = homeDeviceId,
            PromotionKind = kind,
            IssuedAt = issuedAt,
            Nonce = nonce,
            IssuerId = _adminA.PrincipalId.ToBase64Url(),
            Signature = signature,
            CoApproverIssuerId = coApprover?.PrincipalId.ToBase64Url(),
            CoApproverSignature = coApprover is null
                ? null
                : new Ed25519Signer(coApprover).SignAsync(payload, issuedAt, nonce)
                    .AsTask().GetAwaiter().GetResult().Signature.ToBase64Url(),
        };
    }

    private sealed class FixedTrusteeKeyRecovery : ITrusteeKeyRecovery
    {
        public ValueTask<RecoveredNodeKeys> RecoverAsync(
            string replacementNodeId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(new RecoveredNodeKeys(new byte[32], ["trustee-a", "trustee-b"]));
    }

    private sealed class NoOpRootSeedRestorer : IRootSeedRestorer
    {
        public Task RestoreRootSeedAsync(ReadOnlyMemory<byte> recoveredSeed, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FixedIdentityFactory(NodeIdentity identity) : INodeIdentityFactory
    {
        public NodeIdentity CreateFresh() => identity;
    }

    private sealed class FixedGrantProvider : IRosterRehostGrantProvider
    {
        public ValueTask<RosterSignedRehostGrant> ObtainAsync(
            string tenantId,
            string replacedNodeId,
            NodeIdentity replacementIdentity,
            IReadOnlyList<string> attestingTrusteeNodeIds,
            CancellationToken ct = default) =>
            ValueTask.FromResult(new RosterSignedRehostGrant("roster-signed-grant"));
    }

    private sealed class FixedCanonicalSource : ICanonicalRehostSource
    {
        public ValueTask<IReadOnlyList<CanonicalReplicaDocument>> ReConvergeFromHoldersAsync(
            RosterSignedRehostGrant grant,
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<CanonicalReplicaDocument>>(
            [
                new CanonicalReplicaDocument(
                    "contacts",
                    "abc"u8.ToArray(),
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ]);
    }

    private sealed class FixedPromotionAuthority(Func<HomeEpochRecord> create)
        : IHomeEpochPromotionAuthority
    {
        public ValueTask<HomeEpochRecord> AuthorizeRecoveryFailoverAsync(
            string tenantId,
            string replacementNodeId,
            CancellationToken ct = default) => ValueTask.FromResult(create());
    }
}
