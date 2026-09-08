namespace Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;

/// <summary>
/// Host-projected state consumed by <see cref="HarborlineTeamSwitcher"/>.
/// </summary>
public interface ITeamSwitcherState
{
    /// <summary>Teams available to the current user.</summary>
    IReadOnlyList<TeamSwitcherOption> Teams { get; }

    /// <summary>The selected team's opaque host identifier, or <see langword="null"/>.</summary>
    string? ActiveTeamId { get; }

    /// <summary>Total unread notifications across all teams.</summary>
    int AggregateUnread { get; }

    /// <summary>Raised when the host projection changes and consumers should re-render.</summary>
    event EventHandler? Changed;

    /// <summary>Switches the host's active team to the selected opaque identifier.</summary>
    /// <param name="teamId">Identifier from <see cref="Teams"/>.</param>
    /// <param name="cancellationToken">Token that cancels the host operation.</param>
    Task SelectTeamAsync(string teamId, CancellationToken cancellationToken);
}

/// <summary>A host-neutral team choice presented by <see cref="HarborlineTeamSwitcher"/>.</summary>
/// <param name="Id">Opaque identifier returned to the composed state when selected.</param>
/// <param name="DisplayName">Human-readable team name.</param>
/// <param name="UnreadCount">Unread notification count for the team.</param>
/// <param name="IsLegacyPrimary">Whether the team is the migrated install's primary team.</param>
public sealed record TeamSwitcherOption(
    string Id,
    string DisplayName,
    int UnreadCount,
    bool IsLegacyPrimary = false);
