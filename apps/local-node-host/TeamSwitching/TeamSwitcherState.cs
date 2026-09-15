namespace Harborline.Api.LocalNodeHost;

/// <summary>Host-projected state for selecting an active team.</summary>
internal interface ITeamSwitcherState
{
    IReadOnlyList<TeamSwitcherOption> Teams { get; }

    string? ActiveTeamId { get; }

    int AggregateUnread { get; }

    event EventHandler? Changed;

    Task SelectTeamAsync(string teamId, CancellationToken cancellationToken);
}

/// <summary>A team choice exposed by the local-node host.</summary>
internal sealed record TeamSwitcherOption(
    string Id,
    string DisplayName,
    int UnreadCount,
    bool IsLegacyPrimary = false);
