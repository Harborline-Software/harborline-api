using Harborline.Api.Kernel.Runtime.Notifications;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;

namespace Harborline.Api.LocalNodeHost;

internal sealed class KernelTeamSwitcherState : ITeamSwitcherState
{
    private EventHandler? _changed;
    private readonly ITeamContextFactory _factory;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly INotificationAggregator _notifications;

    public KernelTeamSwitcherState(
        ITeamContextFactory factory,
        IActiveTeamAccessor activeTeam,
        INotificationAggregator notifications)
    {
        _factory = factory;
        _activeTeam = activeTeam;
        _notifications = notifications;
        _activeTeam.ActiveChanged += OnActiveTeamChanged;
        _notifications.NotificationReceived += OnNotificationReceived;
    }

    public IReadOnlyList<TeamSwitcherOption> Teams => _factory.Active
        .OrderBy(static team => team.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(team => new TeamSwitcherOption(
            team.TeamId.ToString(),
            team.DisplayName,
            _notifications.GetUnreadCount(team.TeamId)))
        .ToArray();

    public string? ActiveTeamId => _activeTeam.Active?.TeamId.ToString();

    public int AggregateUnread => _notifications.GetAggregateUnreadCount();

    public event EventHandler? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public Task SelectTeamAsync(string teamId, CancellationToken cancellationToken) =>
        _activeTeam.SetActiveAsync(TeamId.Parse(teamId), cancellationToken);

    private void OnActiveTeamChanged(object? sender, ActiveTeamChangedEventArgs e) =>
        _changed?.Invoke(this, EventArgs.Empty);

    private void OnNotificationReceived(object? sender, TeamNotification e) =>
        _changed?.Invoke(this, EventArgs.Empty);
}
