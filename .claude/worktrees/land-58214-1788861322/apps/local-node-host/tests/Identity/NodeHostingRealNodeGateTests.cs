using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.NodeHosting;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Health;
using HostUnderTest = Harborline.Api.NodeHosting.NodeHost;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>G-R1(b): proves the listener-auth gate on the real app-started node.</summary>
[Collection("Harborline process environment")]
public sealed class NodeHostingRealNodeGateTests : IAsyncLifetime
{
    private HostUnderTest? _nodeHost;
    private string? _dataDirectory;
    private string? _previousRootSeedHex;
    private string? _previousWebClientEnabled;
    private string? _previousClientDataDirectory;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"harborline-node-g-r1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        _previousWebClientEnabled = Environment.GetEnvironmentVariable("LocalNode__WebClient__Enabled");
        _previousClientDataDirectory = Environment.GetEnvironmentVariable("HARBORLINE_NODE_DATA_DIRECTORY");
        Environment.SetEnvironmentVariable(
            "LocalNode__RootSeedHex",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", "false");
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_DATA_DIRECTORY", _dataDirectory);

        _nodeHost = new HostUnderTest();
        await _nodeHost.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_nodeHost is not null)
            await _nodeHost.StopAsync(CancellationToken.None);
        if (_dataDirectory is not null && Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
        Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", _previousRootSeedHex);
        Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", _previousWebClientEnabled);
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_DATA_DIRECTORY", _previousClientDataDirectory);
    }

    [Fact(DisplayName = "G-R1(b): the real started node enforces listener caller auth")]
    public void RealStartedNode_EnforcesListenerCallerAuth()
    {
        var services = LocalNodeHostRuntime.CurrentServices;
        Assert.NotNull(services);
        var registry = services!.GetRequiredService<LocalNodeExecutableEndpointRegistry>();
        Assert.True(registry.Current.ListenerCallerAuthEnforced);
    }
}
