namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// ADR 0115 SC-4 amendment — <b>fail-closed boot guard</b> for the recoverability
/// contingency the security-engineering SPOT-CHECK flagged (verdict
/// <c>council-verdict-2026-06-14-sc4-impl-spot-check.md</c>, finding S1).
/// </summary>
/// <remarks>
/// <para>
/// SC-4 recovery (the passphrase-slot path) resolves the Store DEK used by
/// <c>local-node.db</c>. Ticket 043 extends that same custody root to per-team
/// <c>sunfish.db</c> stores through <see cref="LocalNodeKeyHierarchy"/> and rotates
/// existing seed-keyed stores on first activation.
/// </para>
/// <para>
/// The historical Boolean overload remains for compatibility tests that model an
/// unextended store. Production composition uses the hierarchy overload so the guard's
/// decision cannot drift away from the keys actually passed to the store activator.
/// </para>
/// <para>
/// This guard converts that documented assumption into a <b>runtime invariant</b>.
/// When SC-4 envelope recovery is active and the per-team KV store is not extended
/// behind the recoverable envelope, the host refuses multi-team startup because the
/// per-team KV stores would then hold value that a passphrase recovery cannot
/// restore.
/// </para>
/// <para>
/// The guard is a pure function over the resolved <see cref="LocalNodeOptions"/> so
/// it is fully unit-testable and runs at composition time (before any hosted
/// service starts), mirroring the SC-1 fail-closed registration posture.
/// </para>
/// </remarks>
public static class Sc4RecoverabilityGuard
{
    /// <summary>
    /// Validates SC-4 recoverability against the resolved key hierarchy rather than a caller-provided
    /// assertion about per-team store custody.
    /// </summary>
    /// <param name="options">Resolved host options.</param>
    /// <param name="hierarchy">Validated hierarchy used to compose the encrypted stores.</param>
    public static void Validate(
        LocalNodeOptions options,
        LocalNodeKeyHierarchy hierarchy)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        Validate(options, hierarchy.PerTeamKvStoreIsEnvelopeExtended);
    }

    /// <summary>
    /// Validates the SC-4 recoverability preconditions and throws
    /// <see cref="Sc4RecoverabilityViolationException"/> (fail-closed) when they are
    /// violated. A no-op when SC-4 envelope recovery is not active (no Store DEK
    /// injected) — the legacy seed-keyed path is unaffected.
    /// </summary>
    /// <param name="options">The resolved host options (Store DEK presence,
    /// MultiTeam enablement, peer-sync listening).</param>
    /// <param name="perTeamKvStoreIsEnvelopeExtended">Whether the per-team KV store
    /// is itself keyed behind the recoverable Store-DEK envelope (SC4-C2 option b).
    /// Historical callers can pass <c>false</c> to model a seed-keyed store; production
    /// obtains this value from <see cref="LocalNodeKeyHierarchy"/>.</param>
    /// <exception cref="Sc4RecoverabilityViolationException">SC-4 recovery is active,
    /// the KV store is not envelope-extended, and multi-team is effectively enabled
    /// — recoverability would be silently broken.</exception>
    public static void Validate(
        LocalNodeOptions options,
        bool perTeamKvStoreIsEnvelopeExtended = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        // SC-4 envelope recovery is active iff the shell injected a resolved Store
        // DEK. Absent it, the store is keyed by HKDF(rootSeed) (the legacy /
        // Bridge-spawned-tenant path); there is no passphrase reseed that could
        // orphan the KV store, so the guard does not apply.
        var sc4RecoveryActive = !string.IsNullOrWhiteSpace(options.StoreDekHex);
        if (!sc4RecoveryActive)
        {
            return;
        }

        // If the per-team KV store is itself envelope-extended (the deferred
        // multi-device path), a passphrase recovery restores it too — the orphaning
        // vector is closed, so multi-team is safe under SC-4.
        if (perTeamKvStoreIsEnvelopeExtended)
        {
            return;
        }

        // Multi-team is "effectively enabled" when the host will materialize
        // per-team KV/event stores that can accrue irreplaceable value. The shell
        // pins MultiTeam.Enabled=false for single-device v1; the host DEFAULT is
        // true (Wave 6.7). Under SC-4 recovery with a seed-keyed (non-envelope) KV
        // store, multi-team-on means a passphrase recovery silently orphans
        // per-team value — so refuse to start.
        var multiTeamEffectivelyEnabled = options.MultiTeam?.Enabled ?? true;
        if (multiTeamEffectivelyEnabled)
        {
            throw new Sc4RecoverabilityViolationException(
                "SC-4 envelope recovery is active (LocalNode:StoreDekHex is set) but multi-team is " +
                "ENABLED (LocalNode:MultiTeam:Enabled is true or unset — the host default is true since " +
                "Wave 6.7). The per-team KV/event store is keyed HKDF(rootSeed, teamId), so a passphrase " +
                "recovery (which mints a NEW seed) would leave it UNDECRYPTABLE and silently orphan any " +
                "per-team financial/CRDT value. Refusing to start (fail-closed) to avoid silently broken " +
                "recoverability. Resolutions: (a) for single-device v1, set LocalNode:MultiTeam:Enabled=false " +
                "(the Tauri sidecar boot contract already does this); or (b) extend the per-team KV store " +
                "behind the recoverable Store-DEK envelope (SC4-C2 option b) before enabling multi-team " +
                "under SC-4 recovery.");
        }
    }
}

/// <summary>
/// Thrown by <see cref="Sc4RecoverabilityGuard.Validate"/> when the SC-4
/// recoverability preconditions are violated — the host fails closed rather than
/// start a node whose passphrase recovery would silently orphan per-team financial
/// value (ADR 0115 SC-4 amendment, SPOT-CHECK finding S1).
/// </summary>
public sealed class Sc4RecoverabilityViolationException : InvalidOperationException
{
    /// <summary>Creates the exception with the fail-closed diagnostic message.</summary>
    public Sc4RecoverabilityViolationException(string message) : base(message)
    {
    }
}
