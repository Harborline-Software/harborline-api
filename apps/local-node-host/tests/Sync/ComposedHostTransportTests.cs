using Harborline.Api.Federation.Common;
using Harborline.Api.Foundation.Transport;
using Harborline.Api.Foundation.Transport.Mdns;
using Harborline.Api.LocalNodeHost;

using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

[Collection("Harborline process environment")]
public sealed class ComposedHostTransportTests
{
    // The root seed override keeps the composed boot off the platform keystore, which Linux and
    // macOS runners do not provide (PlatformNotSupportedException: libsecret is Wave 2).
    private const string RootSeedHex =
        "3463463463463463463463463463463463463463463463463463463463463463";

    [Fact]
    public async Task Known_network_with_mdns_enabled_composes_the_tier_one_mdns_transport()
    {
        var dataDirectory = CreateDataDirectory();
        var previousEnableMdns = Environment.GetEnvironmentVariable("LocalNode__Sync__EnableMdns");
        var previousNetworkTrust = Environment.GetEnvironmentVariable("LocalNode__Sync__NetworkTrust");
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            Environment.SetEnvironmentVariable("LocalNode__Sync__EnableMdns", "true");
            Environment.SetEnvironmentVariable("LocalNode__Sync__NetworkTrust", "Known");
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);

            await LocalNodeHostRuntime.StartAsync(
                "ticket-450-mdns-transport",
                dataDirectory,
                deadline.Token,
                finalServiceRegistration: services =>
                    services.AddSingleton<IPeerTransport, InertManagedRelayTransport>());

            var selector = Assert.IsType<DefaultTransportSelector>(
                LocalNodeHostRuntime.CurrentServices!.GetRequiredService<ITransportSelector>());

            Assert.IsType<MdnsPeerTransport>(selector.Tier1Transport);
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(deadline.Token);
            Environment.SetEnvironmentVariable("LocalNode__Sync__EnableMdns", previousEnableMdns);
            Environment.SetEnvironmentVariable("LocalNode__Sync__NetworkTrust", previousNetworkTrust);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            DeleteDataDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Default_composition_does_not_register_a_transport_selector()
    {
        var dataDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);
            await LocalNodeHostRuntime.StartAsync(
                "ticket-450-default-transport",
                dataDirectory,
                deadline.Token);

            Assert.Null(LocalNodeHostRuntime.CurrentServices!.GetService<ITransportSelector>());
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(deadline.Token);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            DeleteDataDirectory(dataDirectory);
        }
    }

    private static string CreateDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket-450-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDataDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class InertManagedRelayTransport : IPeerTransport
    {
        public TransportTier Tier => TransportTier.ManagedRelay;
        public bool IsAvailable => false;

        public Task<PeerEndpoint?> ResolvePeerAsync(PeerId peer, CancellationToken ct) =>
            Task.FromResult<PeerEndpoint?>(null);

        public Task<IDuplexStream> ConnectAsync(PeerId peer, CancellationToken ct) =>
            Task.FromException<IDuplexStream>(new InvalidOperationException("The inert test relay cannot connect."));
    }
}
