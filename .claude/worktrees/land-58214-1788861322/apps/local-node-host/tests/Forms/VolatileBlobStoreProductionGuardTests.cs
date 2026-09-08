using Harborline.Api.Foundation.Blobs;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// Proves the volatile blob store cannot be the resolved <see cref="IBlobStore"/> in Production,
/// independent of registration order.
///
/// The guard exists because ordering alone is not fail-closed: Card 3750 (gap G2.1) records the
/// bundled node running the default Edge role, falling through to the in-memory default, and
/// losing every blob on restart with no error. The second test below is the important one — a
/// guard that fires on the correct composition too would be a guard nobody keeps.
/// </summary>
public sealed class VolatileBlobStoreProductionGuardTests : IDisposable
{
    private readonly string? _originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

    private static ServiceCollection ServicesWith(Type implementation)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IBlobStore), implementation);
        return services;
    }

    [Fact]
    public async Task Volatile_store_is_refused_in_production()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        var guard = new VolatileBlobStoreProductionGuardAssertion(ServicesWith(typeof(NodeInMemoryBlobStore)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.StartAsync(CancellationToken.None));
        Assert.Contains("not permitted in Production", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Volatile_store_is_permitted_outside_production()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        var guard = new VolatileBlobStoreProductionGuardAssertion(ServicesWith(typeof(NodeInMemoryBlobStore)));

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Durable_store_registered_after_the_volatile_default_does_not_trip_the_guard()
    {
        // The real composition registers the volatile default via TryAddSingleton inside
        // AddNodeForms and a durable store around it. The LAST registration wins at resolution,
        // so the guard must inspect the effective descriptor, not merely detect the volatile
        // type's presence anywhere in the collection.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        var services = ServicesWith(typeof(NodeInMemoryBlobStore));
        services.AddSingleton<IBlobStore, DurableTestBlobStore>();

        await new VolatileBlobStoreProductionGuardAssertion(services).StartAsync(CancellationToken.None);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalEnvironment);

    private sealed class DurableTestBlobStore : IBlobStore
    {
        public ValueTask<Cid> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new NotSupportedException("registration-shape stand-in; never resolved by these tests");

        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(Cid cid, CancellationToken ct = default)
            => throw new NotSupportedException("registration-shape stand-in; never resolved by these tests");

        public ValueTask<bool> ExistsLocallyAsync(Cid cid, CancellationToken ct = default)
            => throw new NotSupportedException("registration-shape stand-in; never resolved by these tests");

        public ValueTask PinAsync(Cid cid, CancellationToken ct = default)
            => throw new NotSupportedException("registration-shape stand-in; never resolved by these tests");

        public ValueTask UnpinAsync(Cid cid, CancellationToken ct = default)
            => throw new NotSupportedException("registration-shape stand-in; never resolved by these tests");
    }
}
