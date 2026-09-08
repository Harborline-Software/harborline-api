namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Caller's identity and current operation permissions. Used by
/// <see cref="JournalPostingService"/> for period-gating authorization.
/// </summary>
/// <remarks>
/// Local placeholder. A future shared <c>Harborline.Api.Foundation.Identity</c>
/// or session-context type will replace this. TODO: relocate when the
/// shared type lands.
/// </remarks>
public interface IUserContext
{
    /// <summary>The current user's id (opaque string).</summary>
    string UserId { get; }

    /// <summary><c>true</c> if the current user holds <paramref name="permission"/>.</summary>
    bool HasPermission(string permission);
}

/// <summary>
/// In-memory <see cref="IUserContext"/> for tests and dev-mode bootstrap.
/// </summary>
public sealed class StaticUserContext : IUserContext
{
    private readonly HashSet<string> _permissions;

    public StaticUserContext(string userId, IEnumerable<string>? permissions = null)
    {
        UserId = userId;
        _permissions = new HashSet<string>(permissions ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string UserId { get; }

    /// <inheritdoc />
    public bool HasPermission(string permission) => _permissions.Contains(permission);
}
