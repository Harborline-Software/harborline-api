using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.LocalNodeHost.Data.AssetRegistry;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

public sealed class NodeAssetRegistryCompositionTests
{
    [Fact(DisplayName = "partial asset-registry composition validates without the full-node record writer")]
    public void PartialComposition_DoesNotRequireFullNodeRecordWriter()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry();

        using var app = builder.Build();

        Assert.Null(app.Services.GetService<PackBoundRegistryRecordWriter>());
    }
}
