using Bunit;
using Harborline.Api.Foundation.Services;
using Harborline.Api.UIAdapters.Blazor.Components.Forms.Inputs;
using Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;
using Harborline.Api.UICore.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.TeamSwitching;

public sealed class HarborlineTeamSwitcherTests
{
    [Fact]
    public void RendersExistingDropDownListBoundToState()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton(Substitute.For<IHarborlineCssProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineIconProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineThemeService>());
        var state = new StubTeamSwitcherState(
            [
                new TeamSwitcherOption("blue", "Blue Harbor", 3),
                new TeamSwitcherOption("amber", "Amber Fleet", 0),
            ],
            activeTeamId: "amber");

        var rendered = context.Render<HarborlineTeamSwitcher>(
            parameters => parameters.Add(component => component.State, state));

        var dropDown = rendered.FindComponent<HarborlineDropDownList<TeamSwitcherOption, string>>();
        Assert.Same(state.Teams, dropDown.Instance.Data);
        Assert.Equal("amber", dropDown.Instance.Value);
        Assert.Equal(nameof(TeamSwitcherOption.DisplayName), dropDown.Instance.TextField);
        Assert.Equal(nameof(TeamSwitcherOption.Id), dropDown.Instance.ValueField);
    }

    [Fact]
    public void SelectingTeamDelegatesToStateThenRaisesCallback()
    {
        using var context = CreateContext();
        var state = new StubTeamSwitcherState(
            [
                new TeamSwitcherOption("blue", "Blue Harbor", 3),
                new TeamSwitcherOption("amber", "Amber Fleet", 0),
            ],
            activeTeamId: "amber");
        string? callbackTeamId = null;

        var rendered = context.Render<HarborlineTeamSwitcher>(parameters => parameters
            .Add(component => component.State, state)
            .Add(component => component.OnTeamChanged, teamId => callbackTeamId = teamId));

        rendered.Find("[role=listbox]").Click();
        rendered.FindAll("li[role=option]")
            .Single(option => option.TextContent.Contains("Blue Harbor", StringComparison.Ordinal))
            .Click();

        Assert.Equal("blue", state.SelectedTeamId);
        Assert.Equal("blue", callbackTeamId);
    }

    [Fact]
    public void ExternalStateChangeRefreshesDropDownSelection()
    {
        using var context = CreateContext();
        var state = new StubTeamSwitcherState(
            [
                new TeamSwitcherOption("blue", "Blue Harbor", 3),
                new TeamSwitcherOption("amber", "Amber Fleet", 0),
            ],
            activeTeamId: "amber");
        var rendered = context.Render<HarborlineTeamSwitcher>(
            parameters => parameters.Add(component => component.State, state));

        state.SetActiveExternally("blue");

        Assert.Equal(
            "blue",
            rendered.FindComponent<HarborlineDropDownList<TeamSwitcherOption, string>>().Instance.Value);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(Substitute.For<IHarborlineCssProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineIconProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineThemeService>());
        return context;
    }

    private sealed class StubTeamSwitcherState : ITeamSwitcherState
    {
        private string? _activeTeamId;

        public StubTeamSwitcherState(
            IReadOnlyList<TeamSwitcherOption> teams,
            string? activeTeamId)
        {
            Teams = teams;
            _activeTeamId = activeTeamId;
        }

        public IReadOnlyList<TeamSwitcherOption> Teams { get; }

        public string? ActiveTeamId => _activeTeamId;

        public int AggregateUnread => Teams.Sum(static team => team.UnreadCount);

        public string? SelectedTeamId { get; private set; }

        public event EventHandler? Changed;

        public Task SelectTeamAsync(string teamId, CancellationToken cancellationToken)
        {
            SelectedTeamId = teamId;
            return Task.CompletedTask;
        }

        public void SetActiveExternally(string teamId)
        {
            _activeTeamId = teamId;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
