using Harborline.Api.Kernel.Runtime.Notifications;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.TeamSwitching;

public sealed class LocalNodeTeamSwitcherCompositionTests
{
    [Fact]
    public void RegistersHostAdapterAsTeamSwitcherStateSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITeamContextFactory>());
        services.AddSingleton(Substitute.For<IActiveTeamAccessor>());
        services.AddSingleton(Substitute.For<INotificationAggregator>());

        services.AddLocalNodeTeamSwitcher();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<ITeamSwitcherState>();
        var second = provider.GetRequiredService<ITeamSwitcherState>();
        Assert.IsType<KernelTeamSwitcherState>(first);
        Assert.Same(first, second);
    }
}
