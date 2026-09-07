using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.LocalNodeHost.Tests.Installation;

public sealed class PeerTrustPolicyStartupTests
{
    [Fact]
    public async Task Startup_RefusesAnActiveTeamWithoutAPeerTrustPolicy()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Services.AddTestKernelClock();
        builder.Services.AddLogging();
        builder.Services.AddHarborlineKernelRuntime();
        builder.Services.AddHarborlineMultiTeam((services, _, _) =>
            services.AddHarborlineKernelSync());
        builder.Services.Configure<LocalNodeOptions>(_ => { });
        builder.Services.AddHostedService<LocalNodeWorker>();
        using var host = builder.Build();
        var teamId = new TeamId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        _ = await host.Services.GetRequiredService<ITeamContextFactory>()
            .GetOrCreateAsync(teamId, "Untrusted team", CancellationToken.None);
        await host.Services.GetRequiredService<IActiveTeamAccessor>()
            .SetActiveAsync(teamId, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Equal(
            "The active team's peer trust policy is missing; refusing allow-all sync startup.",
            exception.Message);
    }
}
