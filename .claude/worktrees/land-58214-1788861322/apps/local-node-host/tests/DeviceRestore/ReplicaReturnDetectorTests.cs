using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.Kernel.Sync.Gossip;
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
}
