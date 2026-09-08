using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.LocalNodeHost.BackupRestore;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Tests.BackupRestore;

public sealed class NodeRehostServiceTests
{
    [Fact]
    public async Task RestoreAsync_RecoversKeysAndMatchesCanonicalContentHash()
    {
        var calls = new List<string>();
        var restoredKeys = new RecordingRootSeedRestorer(calls);
        var epochs = new RecordingHomeEpochStore(calls);
        var service = new NodeRehostService(
            restoredKeys,
            new StubRosterRehostGrantProvider(calls),
            epochs);

        var result = await service.RestoreAsync(
            new NodeRehostRequest("tenant-a", "old-node", new ActorId("operator"), new("roster-signed-grant")),
            new NodeRehostSession(new NodeIdentity("replacement-node", new byte[32], new byte[32]),
                new StubTrusteeKeyRecovery(calls), new StubCanonicalRehostSource(calls),
                new StubHomeEpochPromotionAuthority(calls)),
            CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("contacts", document.DocumentId);
        Assert.Equal("abc"u8.ToArray(), document.Snapshot);
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            document.Sha256);
        Assert.Equal("replacement-node", result.Identity.NodeId);
        Assert.Equal(2, result.HomeEpochNumber);
        Assert.Equal(new byte[32], restoredKeys.RestoredSeed);
        Assert.Equal(
            ["redeem-grant", "recover-keys", "restore-root-seed", "re-converge", "authorize-epoch", "advance-epoch"],
            calls);
    }

    [Fact]
    public async Task RestoreAsync_RefusesPromotionThatDoesNotFenceToReplacementNode()
    {
        var calls = new List<string>();
        var epochs = new RecordingHomeEpochStore(calls);
        var service = new NodeRehostService(
            new RecordingRootSeedRestorer(calls),
            new StubRosterRehostGrantProvider(calls),
            epochs);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.RestoreAsync(
                new NodeRehostRequest("tenant-a", "old-node", new ActorId("operator"), new("roster-signed-grant")),
                new NodeRehostSession(new NodeIdentity("replacement-node", new byte[32], new byte[32]),
                    new StubTrusteeKeyRecovery(calls), new StubCanonicalRehostSource(calls),
                    new WrongHomeEpochPromotionAuthority()),
                CancellationToken.None));

        Assert.Equal(0, epochs.AdvanceCount);
    }

    private sealed class StubTrusteeKeyRecovery(List<string> calls) : ITrusteeKeyRecovery
    {
        public ValueTask<RecoveredNodeKeys> RecoverAsync(
            string replacementNodeId,
            CancellationToken ct = default)
        {
            calls.Add("recover-keys");
            return ValueTask.FromResult(new RecoveredNodeKeys(new byte[32], ["trustee-a", "trustee-b"]));
        }
    }

    private sealed class RecordingRootSeedRestorer(List<string> calls) : IRootSeedRestorer
    {
        public byte[]? RestoredSeed { get; private set; }

        public Task RestoreRootSeedAsync(ReadOnlyMemory<byte> recoveredSeed, CancellationToken ct)
        {
            calls.Add("restore-root-seed");
            RestoredSeed = recoveredSeed.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedIdentityFactory(NodeIdentity identity) : INodeIdentityFactory
    {
        public NodeIdentity CreateFresh() => identity;
    }

    private sealed class StubRosterRehostGrantProvider(List<string> calls)
        : IRosterRehostGrantProvider
    {
        public ValueTask<AuthorizationDecision> RedeemAsync(RosterSignedRehostGrant? grant, string tenantId,
            string replacedNodeId, NodeIdentity replacement, IReadOnlyList<string> requiredActs, ActorId caller,
            CancellationToken ct = default)
        {
            calls.Add("redeem-grant");
            Assert.Equal(new[] { SignedRosterRehostGrantProvider.ReadCanonical, SignedRosterRehostGrantProvider.PromoteHome }, requiredActs);
            return TestAuthorization.AllowGate().DecideAsync(new AuthorizationWriteContext(caller,
                new TenantId(tenantId), TestAuthorization.At).Request(
                    AuthorizationOperation.Parse("members:admit"), "members", replacement.NodeId), ct);
        }

        public ValueTask<RosterSignedRehostGrant> ObtainAsync(
            string tenantId,
            string replacedNodeId,
            NodeIdentity replacementIdentity,
            IReadOnlyList<string> attestingTrusteeNodeIds,
            CancellationToken ct = default)
        {
            calls.Add("obtain-grant");
            Assert.Equal("tenant-a", tenantId);
            Assert.Equal("old-node", replacedNodeId);
            Assert.Equal("replacement-node", replacementIdentity.NodeId);
            Assert.Equal(["trustee-a", "trustee-b"], attestingTrusteeNodeIds);
            return ValueTask.FromResult(new RosterSignedRehostGrant("roster-signed-grant"));
        }
    }

    private sealed class StubCanonicalRehostSource(List<string> calls) : ICanonicalRehostSource
    {
        public ValueTask<IReadOnlyList<CanonicalReplicaDocument>> ReConvergeFromHoldersAsync(
            RosterSignedRehostGrant grant,
            CancellationToken ct = default)
        {
            calls.Add("re-converge");
            Assert.Equal("roster-signed-grant", grant.SerializedGrant);
            return ValueTask.FromResult<IReadOnlyList<CanonicalReplicaDocument>>(
            [
                new CanonicalReplicaDocument(
                    "contacts",
                    "abc"u8.ToArray(),
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ]);
        }
    }

    private sealed class StubHomeEpochPromotionAuthority(List<string> calls)
        : IHomeEpochPromotionAuthority
    {
        public ValueTask<HomeEpochRecord> AuthorizeRecoveryFailoverAsync(
            string tenantId,
            string replacementNodeId,
            CancellationToken ct = default)
        {
            calls.Add("authorize-epoch");
            return ValueTask.FromResult(new HomeEpochRecord
            {
                TenantId = tenantId,
                EpochNumber = 2,
                PreviousEpochNumber = 1,
                HomeDeviceId = replacementNodeId,
                PromotionKind = HomePromotionKind.RecoveryFailover,
                IssuedAt = DateTimeOffset.UnixEpoch,
                Nonce = Guid.Empty,
                IssuerId = "admin-a",
                Signature = "signature-a",
                CoApproverIssuerId = "admin-b",
                CoApproverSignature = "signature-b",
            });
        }
    }

    private sealed class WrongHomeEpochPromotionAuthority : IHomeEpochPromotionAuthority
    {
        public ValueTask<HomeEpochRecord> AuthorizeRecoveryFailoverAsync(
            string tenantId,
            string replacementNodeId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(new HomeEpochRecord
            {
                TenantId = tenantId,
                EpochNumber = 2,
                PreviousEpochNumber = 1,
                HomeDeviceId = "some-other-node",
                PromotionKind = HomePromotionKind.RecoveryFailover,
                IssuedAt = DateTimeOffset.UnixEpoch,
                Nonce = Guid.Empty,
                IssuerId = "admin-a",
                Signature = "signature-a",
                CoApproverIssuerId = "admin-b",
                CoApproverSignature = "signature-b",
            });
    }

    private sealed class RecordingHomeEpochStore(List<string> calls) : IHomeEpochStore
    {
        public int AdvanceCount { get; private set; }

        public Task<HomeEpochRecord?> GetCurrentEpochAsync(
            string tenantId,
            CancellationToken ct = default) => Task.FromResult<HomeEpochRecord?>(null);

        public Task AdvanceAsync(HomeEpochRecord proposed, CancellationToken ct = default)
        {
            calls.Add("advance-epoch");
            AdvanceCount++;
            return Task.CompletedTask;
        }
    }
}
