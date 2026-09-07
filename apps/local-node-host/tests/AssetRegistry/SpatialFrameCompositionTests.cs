using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// The [A10] composition hard-fail: the store must resolve to the PACKAGE-SIDE adapter, the
/// foundation port to the host durable implementation, and the authority to the host home-claim
/// implementation — a mis-ordered last-registration-wins composition must refuse host
/// construction, never silently resolve a wrong implementation.
/// </summary>
public sealed class SpatialFrameCompositionTests : IDisposable
{
    private readonly NodePrincipalSigner _signer;

    public SpatialFrameCompositionTests()
    {
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        _signer = new NodePrincipalSigner(seed);
    }

    public void Dispose() => _signer.Dispose();

    private ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite("Data Source=:memory:"));
        services.AddSingleton(_signer);
        services.AddSingleton<Harborline.Api.Foundation.Crypto.IOperationSigner>(_signer.Signer);
        services.AddSingleton<IRegistryAuditLog, InMemoryRegistryAuditLog>();
        // CP-4: the sealer's key substrate — the composition hard-fails without a registered
        // ITenantKeyProvider (a key-less composition would persist cleartext PII).
        var rootSeed = new byte[32];
        Random.Shared.NextBytes(rootSeed);
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider>(
            new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed));
        return services;
    }

    [Fact(DisplayName = "The happy-path composition passes the hard-fail and resolves all three seams correctly")]
    public void Composition_Correct_ResolvesAllThreeSeams()
    {
        var services = BaseServices();
        services.AddNodeSpatialFrameDescriptors();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<FoundationBackedSpatialFrameDescriptorStore>(
            provider.GetRequiredService<ISpatialFrameDescriptorStore>());
        Assert.IsType<NodeEfSpatialFrameDescriptorPort>(
            provider.GetRequiredService<ISpatialFrameDescriptorPort>());
        Assert.IsType<NodeHomeClaimFrameEpochAuthority>(
            provider.GetRequiredService<IFrameEpochAuthority>());
    }

    [Fact(DisplayName = "Hard-fail (i): a host type registered behind ISpatialFrameDescriptorStore refuses composition")]
    public void Composition_HostStoreBehindInterface_HardFails()
    {
        var services = BaseServices();
        services.AddNodeSpatialFrameDescriptors();
        // The [A13] trap: a builder replaces the STORE interface with a host type, evicting the
        // package adapter (and the internal tenant-guard call with it).
        services.Replace(ServiceDescriptor.Singleton<ISpatialFrameDescriptorStore, EvilHostStore>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.ValidateSpatialFrameComposition());
        Assert.Contains("spatial-frames.store", ex.Message);
    }

    [Fact(DisplayName = "Hard-fail (ii): a missing port registration refuses composition")]
    public void Composition_MissingPort_HardFails()
    {
        var services = BaseServices();
        services.AddDurableAssetRegistryDescriptorStore();
        services.Replace(ServiceDescriptor.Singleton<IFrameEpochAuthority, NodeHomeClaimFrameEpochAuthority>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.ValidateSpatialFrameComposition());
        Assert.Contains("spatial-frames.port", ex.Message);
    }

    [Fact(DisplayName = "Hard-fail (iii): the refusing default authority left in place refuses composition")]
    public void Composition_DefaultAuthorityNotSwapped_HardFails()
    {
        var services = BaseServices();
        services.AddSingleton<ISpatialFrameDescriptorPort, NodeEfSpatialFrameDescriptorPort>();
        services.AddDurableAssetRegistryDescriptorStore(); // leaves RefusingFrameEpochAuthority

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.ValidateSpatialFrameComposition());
        Assert.Contains("spatial-frames.authority", ex.Message);
    }

    [Fact(DisplayName = "CP-4 key posture: the dev InMemoryTenantKeyProvider registered LAST refuses composition")]
    public void Composition_DevKeyProviderLast_HardFails()
    {
        var services = BaseServices();
        services.AddNodeSpatialFrameDescriptors();
        // Last-registration-wins: a later dev-stub registration would silently seal the two
        // governed PII columns under a fixed development salt — the allowlist gate must refuse.
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider>(
            new Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider());

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.ValidateSpatialFrameComposition());
        Assert.Contains("spatial-frames.sealing", ex.Message);
        Assert.Contains("InMemoryTenantKeyProvider", ex.Message);
    }

    [Fact(DisplayName = "CP-4 key posture: an opaque factory ITenantKeyProvider registration refuses composition")]
    public void Composition_OpaqueFactoryKeyProvider_HardFails()
    {
        var services = BaseServices();
        services.AddNodeSpatialFrameDescriptors();
        // A factory registration is unintrospectable at composition time — refused fail-closed
        // even though it WOULD produce the real provider at runtime.
        var rootSeed = new byte[32];
        Random.Shared.NextBytes(rootSeed);
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider>(
            _ => new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.ValidateSpatialFrameComposition());
        Assert.Contains("spatial-frames.sealing", ex.Message);
        Assert.Contains("opaque factory", ex.Message);
    }

    private sealed class EvilHostStore : ISpatialFrameDescriptorStore
    {
        public Task<Harborline.Api.Blocks.Assets.Registry.Model.Spatial.SpatialFrameDescriptor> MintAsync(
            SpatialFrameMintRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Harborline.Api.Blocks.Assets.Registry.Model.Spatial.SpatialFrameDescriptor?> FindAsync(
            TenantId tenant, Harborline.Api.Blocks.Assets.Registry.Model.RegistryEntityId anchor,
            string frameCode, long frameEpoch,
            SpatialFrameReadContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Harborline.Api.Blocks.Assets.Registry.Model.Spatial.SpatialFrameDescriptor>> ListAsync(
            TenantId tenant, Harborline.Api.Blocks.Assets.Registry.Model.RegistryEntityId anchor,
            string frameCode,
            SpatialFrameReadContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Harborline.Api.Blocks.Assets.Registry.Model.Spatial.SpatialFrameQuarantineRecord>> ListQuarantinedAsync(
            TenantId tenant, Harborline.Api.Blocks.Assets.Registry.Model.RegistryEntityId anchor,
            string frameCode,
            SpatialFrameReadContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
