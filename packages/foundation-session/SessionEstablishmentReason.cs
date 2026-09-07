namespace Harborline.Api.Foundation.Session;

/// <summary>
/// Why a session is being established. Carried on <see cref="SessionEstablishmentRequest"/>
/// so the single <see cref="ISessionEstablisher"/> seam (ADR 0099 H7) can emit the
/// reason-specific <c>Auth.SessionEstablished.*</c> audit (see <see cref="AuditEventTypes"/>)
/// without the three entry points diverging into three session-create implementations.
/// </summary>
/// <remarks>
/// These are <i>reasons</i>, not <i>implementations</i> (ADR 0099 AB-2): password login,
/// verify-email completion, and magic-link consume all route through the SAME
/// <see cref="ISessionEstablisher.EstablishAsync"/>; only the audit-event label differs.
/// MFA is a step that runs BEFORE <c>EstablishAsync</c> (ADR 0099 O-S5), not a reason here.
/// </remarks>
public enum SessionEstablishmentReason
{
    /// <summary>
    /// A username/password login that passed credential verification
    /// (<c>IPasswordHasher.VerifyHashedPassword</c>, ADR 0097). Emits
    /// <see cref="AuditEventTypes.SessionEstablishedPasswordLogin"/>.
    /// </summary>
    PasswordLogin,

    /// <summary>
    /// Email-verification completion ("now logged in"). The single-use verify token
    /// was atomically consumed (ADR 0099 S9) and auto-login is POST-gated (O-1). Emits
    /// <see cref="AuditEventTypes.SessionEstablishedVerifyEmail"/>.
    /// </summary>
    VerifyEmailCompletion,

    /// <summary>
    /// Passwordless magic-link consume (WS-E; ADR 0099 H-WSE-2). The single-use link
    /// token was atomically consumed (S9). Emits
    /// <see cref="AuditEventTypes.SessionEstablishedMagicLink"/>.
    /// </summary>
    MagicLinkConsume,
}
