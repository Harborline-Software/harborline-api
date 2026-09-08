using System;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// <see cref="INodeWorkshopUnlockAuthority"/> over the <see cref="AuthorizationGate"/> — resolves the
/// <c>workshop:unlock</c> mode-entry capability at its point of use (ticket 205, ledger L592), from the
/// caller-supplied principal / tenant / act instant.
/// </summary>
/// <remarks>
/// <c>workshop:unlock</c> is declared install-wide
/// (<c>PermissionVocabulary.InstallWideOperations</c>; ledger L600/L671), so the request carries no record
/// target — the gate admits that shape only for a declared install-wide operation and refuses every other
/// operation by name. A principal whose holdings do not cover the act gets
/// <see cref="WorkshopUnlockDecision.MissingUnlockPermission"/> — the accessible denial with reason +
/// remediation keys, never a dead control.
/// </remarks>
public sealed class NodeWorkshopUnlockAuthority : INodeWorkshopUnlockAuthority
{
    private readonly AuthorizationGate _gate;

    /// <summary>Construct over the authorization gate.</summary>
    public NodeWorkshopUnlockAuthority(AuthorizationGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async ValueTask<WorkshopUnlockDecision> AuthorizeAsync(
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await _gate
            .DecideAsync(authority.InstallWide(WorkshopUnlock.Operation), ct)
            .ConfigureAwait(false);
        return decision.Verdict == AuthorizationVerdict.Allowed
            ? WorkshopUnlockDecision.Granted.Instance
            : WorkshopUnlockDecision.MissingUnlockPermission;
    }
}
