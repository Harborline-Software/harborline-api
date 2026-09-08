namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// Durable read/advance surface over the tenant's home-failover fencing epoch
/// (<see cref="HomeEpochRecord"/>) in the recoverable <c>local-node.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two responsibilities.</b> (1) <see cref="GetCurrentEpochAsync"/> reads the tenant's current home
/// epoch (the highest-numbered row) — the value a writer asserts and the fence checks. (2)
/// <see cref="AdvanceAsync"/> is the <b>bump-on-promotion</b> path: it verifies the roster signature(s),
/// enforces the G-5 multi-actor floor for the contested case, asserts strict monotonicity, and appends the
/// new epoch row. Advancing is a <b>CP human-task</b> (a rare, can-be-manual home-failover event per ADR
/// 0135 §D3) — this store is the durable mechanism that task drives, not an autonomous trigger.
/// </para>
/// <para>
/// <b>The fence (G-4) does NOT call this store.</b> The in-transaction fence
/// (<see cref="HomeEpochFenceEnlister"/> / the numbering-service guard) reads the
/// <see cref="HomeEpochRecord"/> row directly on the in-flight <see cref="LocalNodeDbContext"/> so the
/// epoch check rides the SAME transaction as the invariant-bearing effect (no TOCTOU). This store's own
/// reads use short-lived contexts and are for the promotion path + status surfaces.
/// </para>
/// </remarks>
public interface IHomeEpochStore
{
    /// <summary>
    /// Returns the tenant's current home epoch (the highest <see cref="HomeEpochRecord.EpochNumber"/>),
    /// or <see langword="null"/> when no home epoch has been established for the tenant yet (the genesis
    /// single-device state — no failover has occurred).
    /// </summary>
    Task<HomeEpochRecord?> GetCurrentEpochAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// <b>Bump-on-promotion.</b> Verifies <paramref name="proposed"/> and appends it as the tenant's new
    /// current home epoch. Fail-closed on every check:
    /// <list type="bullet">
    ///   <item>The proposer's Ed25519 <see cref="HomeEpochRecord.Signature"/> over the canonical
    ///     <see cref="HomeEpochSignaturePayload"/> must verify against <see cref="HomeEpochRecord.IssuerId"/>.</item>
    ///   <item>Strict monotonicity: <see cref="HomeEpochRecord.EpochNumber"/> == current + 1 and
    ///     <see cref="HomeEpochRecord.PreviousEpochNumber"/> == current (genesis = epoch 1, previous 0).</item>
    ///   <item>G-5 multi-actor floor: a <see cref="HomePromotionKind.RecoveryFailover"/> bump MUST carry a
    ///     DISTINCT co-approver whose signature over the SAME payload also verifies; a
    ///     <see cref="HomePromotionKind.PlannedHandoff"/> bump must NOT carry a co-approver
    ///     (or may, but is not required). The install-time epoch-1 record uses the additive
    ///     <see cref="HomePromotionKind.Genesis"/> kind (ADR 0101 Rev 3.2 precondition 2) — not a
    ///     transfer, single-admin floor, written through this same verified path.</item>
    /// </list>
    /// Throws <see cref="HomeEpochAdvanceRejectedException"/> on any failure; the row is NOT persisted.
    /// </summary>
    Task AdvanceAsync(HomeEpochRecord proposed, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a proposed <see cref="HomeEpochRecord"/> bump fails verification (bad signature, broken
/// monotonicity, or the G-5 multi-actor floor) — the promotion is rejected and nothing is persisted.
/// </summary>
public sealed class HomeEpochAdvanceRejectedException : Exception
{
    /// <summary>Constructs the rejection with a fail-closed reason.</summary>
    public HomeEpochAdvanceRejectedException(string message) : base(message) { }
}
