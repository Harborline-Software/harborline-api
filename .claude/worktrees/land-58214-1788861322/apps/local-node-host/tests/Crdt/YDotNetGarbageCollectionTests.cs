using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Crdt.GarbageCollection;

namespace Harborline.Api.LocalNodeHost.Tests.Crdt;

public sealed class YDotNetGarbageCollectionTests
{
    [Fact]
    public async Task Deleted_Long_History_Is_Compacted_Without_A_Manual_Gc_Strategy()
    {
        Assert.Equal(
            [nameof(GcStrategy.None), nameof(GcStrategy.ShallowSnapshot)],
            Enum.GetNames<GcStrategy>());

        var engine = new YDotNetCrdtEngine();
        await using var document = engine.CreateDocument("gc/long-history");
        var history = document.GetMap("history");
        var payload = new string('x', 2_048);

        for (var index = 0; index < 256; index++)
        {
            history.Set($"entry-{index:D3}", payload);
        }

        var bytesBeforeDeletion = document.ToSnapshot().Length;

        for (var index = 0; index < 256; index++)
        {
            Assert.True(history.Remove($"entry-{index:D3}"));
        }

        var bytesAfterDeletion = document.ToSnapshot().Length;

        Assert.True(bytesBeforeDeletion > 500_000, $"Expected a payload-heavy history; found {bytesBeforeDeletion} bytes.");
        Assert.True(bytesAfterDeletion < 20_000, $"Expected yrs GC to discard deleted payloads; found {bytesAfterDeletion} bytes.");
        Assert.True(bytesAfterDeletion < bytesBeforeDeletion);
    }
}
