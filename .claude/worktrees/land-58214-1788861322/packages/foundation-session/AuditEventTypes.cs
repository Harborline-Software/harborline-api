namespace Harborline.Api.Foundation.Session;

/// <summary>
/// Canonical <c>Auth.*</c> audit-event-type strings emitted on the session lifecycle
/// (ADR 0099 S10). Defined here as the single source of truth for the substrate; the Bridge
/// integration PRs (PR-3 login/logout, PR-4 verify/magic-link) surface these on the existing
/// <c>AuditEventType</c> registry (ADR 0099 C6) rather than re-typing the literals — login
/// failure feeds the rate-limit/lockout signal (S11), and the session-established events
/// carry the establishment reason in their suffix so the three entry points are auditable
/// distinctly through the one <see cref="ISessionEstablisher"/> seam.
/// </summary>
/// <remarks>
/// These are stable wire/audit identifiers; do not rename without an audit-schema migration.
/// The dotted hierarchy (<c>Auth.SessionEstablished.*</c>) mirrors the reason-specific labels
/// the establisher emits per <see cref="SessionEstablishmentReason"/>.
/// </remarks>
public static class AuditEventTypes
{
    /// <summary>A session was established via a password login (<see cref="SessionEstablishmentReason.PasswordLogin"/>).</summary>
    public const string SessionEstablishedPasswordLogin = "Auth.SessionEstablished.PasswordLogin";

    /// <summary>A session was established on verify-email completion (<see cref="SessionEstablishmentReason.VerifyEmailCompletion"/>).</summary>
    public const string SessionEstablishedVerifyEmail = "Auth.SessionEstablished.VerifyEmail";

    /// <summary>A session was established via a magic-link consume (<see cref="SessionEstablishmentReason.MagicLinkConsume"/>).</summary>
    public const string SessionEstablishedMagicLink = "Auth.SessionEstablished.MagicLink";

    /// <summary>A login attempt failed credential verification. Feeds the S11 rate-limit/lockout signal.</summary>
    public const string LoginFailed = "Auth.LoginFailed";

    /// <summary>
    /// A login lockout ENGAGED: repeated <see cref="LoginFailed"/> events for one
    /// (username, source) crossed the configured threshold inside the sliding window, and further
    /// attempts are temporarily refused (S11 rate-limit/lockout). The refusal surface stays
    /// non-enumerating — this event is server-side audit only, never a distinct client response.
    /// </summary>
    public const string LoginLockout = "Auth.LoginLockout";

    /// <summary>
    /// A login lockout RELEASED: the temporary lockout for a (username, source) expired and
    /// attempts are admitted to credential verification again (S11).
    /// </summary>
    public const string LoginLockoutReleased = "Auth.LoginLockoutReleased";

    /// <summary>A user signed out; the server-side <see cref="SessionRecord"/> was removed (S7).</summary>
    public const string SignedOut = "Auth.SignedOut";

    /// <summary>A session was revoked server-side (admin force-logout / "log out all sessions"; S7).</summary>
    public const string SessionRevoked = "Auth.SessionRevoked";

    /// <summary>
    /// A request presented a session whose bound tenant did not match the subdomain-resolved
    /// tenant, or the subdomain was unresolved — rejected fail-closed (ADR 0099 S8 trigger).
    /// </summary>
    public const string SessionTenantMismatch = "Auth.SessionTenantMismatch";

    /// <summary>
    /// Maps an establishment <paramref name="reason"/> to its
    /// <c>Auth.SessionEstablished.*</c> audit-event-type string. Used by the establisher to
    /// emit the reason-specific audit through the single seam.
    /// </summary>
    /// <param name="reason">The establishment reason.</param>
    /// <returns>The canonical audit-event-type string for that reason.</returns>
    public static string SessionEstablishedFor(SessionEstablishmentReason reason) => reason switch
    {
        SessionEstablishmentReason.PasswordLogin => SessionEstablishedPasswordLogin,
        SessionEstablishmentReason.VerifyEmailCompletion => SessionEstablishedVerifyEmail,
        SessionEstablishmentReason.MagicLinkConsume => SessionEstablishedMagicLink,
        _ => SessionEstablishedPasswordLogin,
    };
}
