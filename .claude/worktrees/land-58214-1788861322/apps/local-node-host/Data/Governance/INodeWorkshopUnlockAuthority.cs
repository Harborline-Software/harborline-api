using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Evaluates whether the current node principal may re-enter Build in operating phase — the authorization
/// half of the guarded re-entry (ADR 0144 AD.1). Returns a <see cref="WorkshopUnlockDecision"/> (an
/// accessible grant/denial), never a bare bool, so the Harborline App (slice B4) can render a First-Aid denial.
/// </summary>
/// <remarks>
/// This is the <em>authorization</em> gate only. The guarded control's <em>ceremony</em> (the cover/arm
/// two-step, the human-session confirm) is the Harborline App's <c>GuardedControl</c> (slices B4/B6). The node
/// answers "does this principal hold <c>workshop:unlock</c>?"; the Harborline App arms the control.
/// </remarks>
public interface INodeWorkshopUnlockAuthority
{
    /// <summary>
    /// Resolves the caller's <c>workshop:unlock</c> entitlement at this point of use, through
    /// <c>AuthorizationGate.DecideAsync</c>, returning an accessible <see cref="WorkshopUnlockDecision"/>.
    /// </summary>
    /// <param name="authority">The server-derived principal / tenant / act instant for this call — the caller
    /// supplies them at the point of use; nothing is read from an ambient context (ticket 205, ledger L592).</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<WorkshopUnlockDecision> AuthorizeAsync(AuthorizationWriteContext authority, CancellationToken ct = default);
}
