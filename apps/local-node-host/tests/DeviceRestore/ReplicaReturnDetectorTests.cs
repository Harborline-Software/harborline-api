using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.DeviceRestore;

public sealed class ReplicaReturnDetectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"harborline-restore-{Guid.NewGuid():N}");

    [Fact]
    public async Task OlderReturningPosition_IsDetectedFromDurableHolderEvidence()
    {
        var originalStore = new FilePublishedPositionStore(_directory);
        await originalStore.AdvanceAsync("node-a", 42, CancellationToken.None);

        var afterRestart = new FilePublishedPositionStore(_directory);
        var detector = new ReplicaReturnDetector(afterRestart);

        var decision = await detector.EvaluateAsync(
            "node-a",
            returningPosition: 17,
            CancellationToken.None);

        Assert.Equal(ReplicaReturnDisposition.RestoreRequired, decision.Disposition);
        Assert.Equal(42UL, decision.LastPublishedPosition);
    }

    [Fact]
    public async Task DetectedRestore_RehostsFromHolderWithFreshIdentityAndNoOldClock()
    {
        var positions = new FilePublishedPositionStore(_directory);
        await positions.AdvanceAsync("old-node", 12, CancellationToken.None);
        var newIdentity = new NodeIdentity("new-node", new byte[32], new byte[32]);
        var holder = new StubRehostSource(new CanonicalReplicaState(
            RosterSignedGrant: "roster-signature",
            Documents:
            [
                new CanonicalReplicaDocument(
                    "contacts",
                    "abc"u8.ToArray(),
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ]));
        var coordinator = new ReplicaRestoreCoordinator(
            positions,
            holder,
            new FixedIdentityFactory(newIdentity));
        var oldClock = new VectorClock(new Dictionary<string, ulong> { ["old-node"] = 4 });

        var result = await coordinator.ReturnAsync(
            new ReturningReplicaState("old-node", 4, oldClock),
            CancellationToken.None);

        Assert.Equal(ReplicaReturnDisposition.RestoreRequired, result.Disposition);
        Assert.Same(newIdentity, result.Identity);
        Assert.Equal(0UL, result.VectorClock.Get("old-node"));
        Assert.Equal("roster-signature", result.RosterSignedGrant);
        Assert.Equal("abc"u8.ToArray(), Assert.Single(result.Documents).Snapshot);
        Assert.Equal("old-node", Assert.Single(holder.ReplacedNodeIds));
    }

    [Fact]
    public async Task TamperedCanonicalDocument_IsRefusedBeforeFreshIdentityIsMinted()
    {
        var positions = new FilePublishedPositionStore(_directory);
        await positions.AdvanceAsync("old-node", 12, CancellationToken.None);
        var holder = new StubRehostSource(new CanonicalReplicaState(
            RosterSignedGrant: "roster-signature",
            Documents:
            [
                new CanonicalReplicaDocument(
                    "contacts",
                    "tampered"u8.ToArray(),
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ]));
        var identities = new TrackingIdentityFactory();
        var coordinator = new ReplicaRestoreCoordinator(positions, holder, identities);

        var error = await Assert.ThrowsAsync<ContentHashMismatchException>(async () =>
            await coordinator.ReturnAsync(
                new ReturningReplicaState("old-node", 4, new VectorClock()),
                CancellationToken.None));

        Assert.Equal("contacts", error.DocumentId);
        Assert.Equal(0, identities.CreatedCount);
    }

    [Fact]
    public async Task RestoreAtTimeT_RejoinsFromHolderTombstoneWithoutRematerializingDeletedContent()
    {
        const string deletedContent = "deleted-after-time-t";
        var positions = new FilePublishedPositionStore(_directory);
        await positions.AdvanceAsync("old-node", 8, CancellationToken.None);
        var canonicalSnapshot = Convert.FromBase64String(
            "AQH8/Y/9DQABAQdyZWNvcmRzFAH8/Y/9DQEAFA==");
        var holder = new StubRehostSource(new CanonicalReplicaState(
            "roster-signature",
            [
                new CanonicalReplicaDocument(
                    "contacts",
                    canonicalSnapshot,
                    "4c1662d5ff83cb20ab1a700c1ba26899467bb53bb81c299eacbd198176c621b3"),
            ]));
        var coordinator = new ReplicaRestoreCoordinator(
            positions,
            holder,
            new TrackingIdentityFactory());
        await using var restoredAtTimeT = new YDotNetCrdtEngine().CreateDocument("contacts");
        restoredAtTimeT.GetText("records").Insert(0, deletedContent);

        var result = await coordinator.ReturnAsync(
            new ReturningReplicaState("old-node", 3, new VectorClock()),
            CancellationToken.None);
        var rehosted = result.OpenRehostedDocuments(new YDotNetCrdtEngine());
        await using var contacts = rehosted["contacts"];

        Assert.Equal(deletedContent, restoredAtTimeT.GetText("records").Value);
        Assert.Equal(string.Empty, contacts.GetText("records").Value);
    }

    [Fact]
    public async Task RehostedReplica_DoesNotReissuePreRestoreSequenceNumbers()
    {
        var positions = new FilePublishedPositionStore(_directory);
        await positions.AdvanceAsync("old-node", 12, CancellationToken.None);
        var holder = new StubRehostSource(new CanonicalReplicaState(
            "roster-signature",
            [
                new CanonicalReplicaDocument(
                    "contacts",
                    "abc"u8.ToArray(),
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ]));
        var coordinator = new ReplicaRestoreCoordinator(
            positions,
            holder,
            new FixedIdentityFactory(new NodeIdentity("new-node", new byte[32], new byte[32])));

        var result = await coordinator.ReturnAsync(
            new ReturningReplicaState("old-node", 4, new VectorClock()),
            CancellationToken.None);
        var allocator = new PublishedPositionSequenceAllocator(positions);
        var first = await allocator.ReserveNextAsync(result.Identity!.NodeId, CancellationToken.None);
        var afterRestart = new PublishedPositionSequenceAllocator(
            new FilePublishedPositionStore(_directory));
        var second = await afterRestart.ReserveNextAsync(result.Identity.NodeId, CancellationToken.None);

        Assert.Equal(13UL, first);
        Assert.Equal(14UL, second);
    }

    [Fact]
    public void VectorClockSet_RefusesToMoveANodeBackwards()
    {
        var clock = new VectorClock();
        clock.Set("node-a", 9);

        var error = Assert.Throws<InvalidOperationException>(() => clock.Set("node-a", 8));

        Assert.Contains("cannot move backwards", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9UL, clock.Get("node-a"));
    }

    [Fact]
    public async Task DurableComposition_UsesOneStoreForDetectionAndOutboundAllocation()
    {
        var services = new ServiceCollection();
        services.AddHarborlineDurablePublishedPositions(_directory);
        await using var provider = services.BuildServiceProvider();
        var positions = provider.GetRequiredService<IPublishedPositionStore>();
        var allocator = provider.GetRequiredService<IOutboundSequenceAllocator>();

        await positions.AdvanceAsync("node-a", 20, CancellationToken.None);

        Assert.Equal(21UL, await allocator.ReserveNextAsync("node-a", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedIdentityFactory(NodeIdentity identity) : INodeIdentityFactory
    {
        public NodeIdentity CreateFresh() => identity;
    }

    private sealed class TrackingIdentityFactory : INodeIdentityFactory
    {
        public int CreatedCount { get; private set; }

        public NodeIdentity CreateFresh()
        {
            CreatedCount++;
            return new NodeIdentity("new-node", new byte[32], new byte[32]);
        }
    }

    private sealed class StubRehostSource(CanonicalReplicaState state) : IReplicaRehostSource
    {
        public List<string> ReplacedNodeIds { get; } = new();

        public ValueTask<CanonicalReplicaState> ReConvergeFromHoldersAsync(
            string replacedNodeId,
            CancellationToken ct)
        {
            ReplacedNodeIds.Add(replacedNodeId);
            return ValueTask.FromResult(state);
        }
    }
}
