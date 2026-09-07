using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

public sealed class DeltaRouterEntitlementSnapshotSourceTests
{
    [Fact]
    public void SnapshotDocumentIds_returns_registered_document_identifiers_only()
    {
        var router = new DeltaRoutingRegistry();
        var route = new EmptyRoute();
        router.Register("contacts", route, route);
        router.Register("roster", route, route);
        router.Register("comms:team-a:conversation-7", route, route);
        var source = new DeltaRouterEntitlementSnapshotSource(router);

        var documentIds = source.SnapshotDocumentIds("team-a");

        Assert.Equal(
            ["comms:team-a:conversation-7", "contacts", "roster"],
            documentIds.OrderBy(value => value, StringComparer.Ordinal));
    }

    private sealed class EmptyRoute : IDeltaProducer, IDeltaSink
    {
        public ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
            string documentId,
            ReadOnlyMemory<byte> peerVectorClock,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(ReadOnlyMemory<byte>.Empty);

        public ValueTask ApplyInboundDeltaAsync(
            string documentId,
            ulong opSequence,
            ReadOnlyMemory<byte> delta,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
