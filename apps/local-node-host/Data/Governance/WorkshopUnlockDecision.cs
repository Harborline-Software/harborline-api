namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Result of evaluating the <c>workshop:unlock</c> mode-entry permission — a sealed two-case discriminated
/// union (<see cref="Granted"/> / <see cref="Denied"/>), never a bare <c>bool</c>. This adopts the ADR 0077
/// <c>PermissionDecision</c> <b>accessible-denial doctrine</b> (reused per ADR 0144 Option C): a denial
/// always names <em>why</em> and <em>what to do</em>, so the Harborline App's guarded re-entry (slice B4) can render
/// a First-Aid-style message + remediation rather than a dead control.
/// </summary>
/// <remarks>
/// The reason/remediation are carried as localizable <b>i18n keys</b> (never English strings — the fleet
/// validation-errors-are-localizable-codes rule), resolved at the Harborline App boundary. The keys match the AD.1
/// copy: <c>chrome.guard.build.denied</c> → "Customizing your business is available to owners and
/// administrators. Ask whoever set up this instance if you need to make changes here."
/// </remarks>
public abstract record WorkshopUnlockDecision
{
    private WorkshopUnlockDecision() { }

    /// <summary>The principal holds <c>workshop:unlock</c> — Build re-entry MAY proceed (through the guarded
    /// control; the guard is the human-session confirm, this is the authorization half).</summary>
    public sealed record Granted : WorkshopUnlockDecision
    {
        /// <summary>The singleton grant (carries no reason payload — a grant needs none).</summary>
        public static readonly Granted Instance = new();
    }

    /// <summary>
    /// The principal does NOT hold <c>workshop:unlock</c> — Build re-entry is refused with an accessible
    /// reason + remediation (never a bare denial).
    /// </summary>
    /// <param name="ReasonCode">Stable machine code for the denial cause (wire-safe, non-localized).</param>
    /// <param name="ReasonKey">i18n key for the human-readable cause (resolved at the Harborline App boundary).</param>
    /// <param name="RemediationKey">i18n key for the suggested next action — never null/empty.</param>
    public sealed record Denied(
        string ReasonCode,
        string ReasonKey,
        string RemediationKey) : WorkshopUnlockDecision;

    /// <summary>The canonical "missing workshop:unlock" denial — the AD.1 accessible-denial copy keys.</summary>
    public static readonly Denied MissingUnlockPermission = new(
        ReasonCode: "workshop_unlock_required",
        ReasonKey: "chrome.guard.build.denied",
        RemediationKey: "chrome.guard.build.denied.remediation");
}
