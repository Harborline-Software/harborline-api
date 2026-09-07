using Harborline.Api.Kernel.Runtime.Notifications;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.TeamSwitching;

public sealed class KernelTeamSwitcherStateTests
{
    [Fact]
    public void ProjectsSortedHostStateIntoUiOptions()
    {
        using var firstServices = new ServiceCollection().BuildServiceProvider();
        using var secondServices = new ServiceCollection().BuildServiceProvider();
        var alphaId = new TeamId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var zuluId = new TeamId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var zulu = new TeamContext(zuluId, "Zulu Fleet", firstServices, TimeProvider.System);
        var alpha = new TeamContext(alphaId, "Alpha Harbor", secondServices, TimeProvider.System);
        var factory = Substitute.For<ITeamContextFactory>();
        var activeTeam = Substitute.For<IActiveTeamAccessor>();
        var notifications = Substitute.For<INotificationAggregator>();
        factory.Active.Returns([zulu, alpha]);
        activeTeam.Active.Returns(zulu);
        notifications.GetUnreadCount(alphaId).Returns(4);
        notifications.GetUnreadCount(zuluId).Returns(7);
        notifications.GetAggregateUnreadCount().Returns(11);

        ITeamSwitcherState state = new KernelTeamSwitcherState(
            factory,
            activeTeam,
            notifications);

        Assert.Equal(
            [
                new TeamSwitcherOption("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Alpha Harbor", 4),
                new TeamSwitcherOption("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Zulu Fleet", 7),
            ],
            state.Teams);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", state.ActiveTeamId);
        Assert.Equal(11, state.AggregateUnread);
    }

    [Fact]
    public void ForwardsKernelEventsThroughHostNeutralChangeEvent()
    {
        var factory = Substitute.For<ITeamContextFactory>();
        var activeTeam = Substitute.For<IActiveTeamAccessor>();
        var notifications = Substitute.For<INotificationAggregator>();
        var state = new KernelTeamSwitcherState(factory, activeTeam, notifications);
        var changeCount = 0;
        state.Changed += (_, _) => changeCount++;

        activeTeam.ActiveChanged += Raise.Event<EventHandler<ActiveTeamChangedEventArgs>>(
            activeTeam,
            new ActiveTeamChangedEventArgs(null, null));
        notifications.NotificationReceived += Raise.Event<EventHandler<TeamNotification>>(
            notifications,
            new TeamNotification(
                new TeamId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                "known-notification",
                "Known title",
                "Known summary",
                DateTimeOffset.UnixEpoch,
                NotificationSeverity.Info));

        Assert.Equal(2, changeCount);
    }
}
