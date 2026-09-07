using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Kernel.Security.DependencyInjection;
using Harborline.Api.Kernel.Security.Keys;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal static class InstallRootSeedResolver
{
    internal static async Task<byte[]> ResolveAsync(
        IInstallIdentityProvider identityProvider,
        IInstallFootprintProvider footprintProvider,
        string keystoreDirectory,
        CancellationToken cancellationToken)
    {
        await using var services = new ServiceCollection()
            .AddSingleton(identityProvider)
            .AddSingleton(footprintProvider)
            .AddHarborlineRootSeedProvider(keystoreStorageDirectory: keystoreDirectory)
            .BuildServiceProvider();
        var seed = await services.GetRequiredService<IRootSeedProvider>()
            .GetRootSeedAsync(cancellationToken)
            .ConfigureAwait(false);
        return seed.ToArray();
    }
}
