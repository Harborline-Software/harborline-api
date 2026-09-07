using Microsoft.Extensions.Configuration;

using Harborline.Api.Kernel.Sync.Network;

namespace Harborline.Api.LocalNodeHost.Tests.NetworkPosture;

public sealed class NetworkTrustConfigurationTests
{
    [Fact]
    public void ShippedConfiguration_ExposesUnknownNetworkTrustAsDurableDefault()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        var options = configuration.GetSection("LocalNode").Get<LocalNodeOptions>();

        Assert.NotNull(options);
        Assert.Equal("Unknown", configuration["LocalNode:Sync:NetworkTrust"]);
        Assert.Equal(NetworkTrustLevel.Unknown, options.Sync.NetworkTrust);
    }
}
