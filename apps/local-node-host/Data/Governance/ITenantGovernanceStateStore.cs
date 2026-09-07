using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Durable read/advance surface over the tenant's <see cref="TenantGovernanceState"/> (the instance
/// lifecycle <see cref="SetupPhase"/>). The node-resident dogfood home of the ADR 0144 AD.1 setup-phase
/// mechanics (slice B5).
/// </summary>
/// <remarks>
/// <para>
/// Three responsibilities, matching the AD.1 lifecycle: (1) <see cref="GetAsync"/> reads the tenant's
/// current state (what the Harborline App phase hook reads to fold Build). (2) <see cref="EnsureGenesisAsync"/> is
/// the genesis write — idempotent, writes <see cref="SetupPhase.Setup"/> exactly once per tenant (the
/// wizard calls it on genesis completion; the node's genesis service backstops a headless boot). (3)
/// <see cref="FinishSetupAsync"/> is the founder-declared setup → operating transition — the UNGUARDED safe
/// direction, idempotent.
/// </para>
/// <para>
/// <b>There is no un-lock method here.</b> Re-entering Build in operating phase is the guarded act; its
/// authorization is <see cref="INodeWorkshopUnlockAuthority"/> and its control is the Harborline App's guarded
/// re-entry (slice B4). This store only moves the phase in the safe direction (genesis → setup, then the
/// founder-declared setup → operating). Re-entry does NOT move the phase back to setup — it opens Build
/// within operating phase — so no "reopen" mutation belongs on this store.
/// </para>
/// </remarks>
public interface ITenantGovernanceStateStore
{
    /// <summary>
    /// The tenant's current governance state, or <see langword="null"/> when genesis has not yet
    /// established one for the tenant.
    /// </summary>
    Task<TenantGovernanceState?> GetAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// <b>Genesis write (idempotent).</b> Establishes the tenant in <see cref="SetupPhase.Setup"/> if no
    /// state exists yet, and returns it. If a state already exists (any phase), it is returned UNCHANGED —
    /// genesis never re-sets an instance that has already progressed to operating (that would silently
    /// re-open setup chrome). Callable safely by both the wizard's genesis-completion path and the node's
    /// boot-time genesis backstop.
    /// </summary>
    Task<TenantGovernanceState> EnsureGenesisAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// <b>Founder-declared setup → operating (the UNGUARDED safe direction).</b> Flips a setup-phase tenant
    /// to <see cref="SetupPhase.Operating"/> and returns the new state. Idempotent: an already-operating
    /// tenant is returned unchanged (the first declaration's <see cref="TenantGovernanceState.EnteredOperatingAt"/>
    /// is preserved). If no state exists yet (a tenant that reaches finish-setup before any genesis write),
    /// genesis is established first, then flipped — so the founder's declaration is never lost to a missing
    /// row.
    /// </summary>
    Task<TenantGovernanceState> FinishSetupAsync(string tenantId, CancellationToken ct = default);
}
