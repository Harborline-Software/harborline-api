namespace Harborline.Api.Kernel.Security.KeyDistribution;

/// <summary>
/// The PQC Phase 2 / BL-01 increment <b>2c-iii-b kill-switch</b> (ADR 0004 Amendment 2 GATE condition 5):
/// the revertible policy that gates whether a WRITER may emit the hybrid suite #3
/// (<see cref="Harborline.Api.Kernel.Security.Crypto.KemSuite.XWingX25519MlKem768_v1"/>) for a capable recipient.
/// </summary>
/// <remarks>
/// <para>
/// <b>This IS the cutover kill-trigger.</b> The 2c-iii-b writer flip is the irreversible-feeling CP cutover that
/// makes production start boxing hybrid for X-Wing-capable recipients. To keep it <i>revertible</i>, every
/// suite-#3 emission is gated behind this policy: when <see cref="HybridWritesEnabled"/> is <c>false</c> the
/// writer falls back to suite #1 with <b>nothing stranded</b> — the read path (#1488) keeps accepting suite #3,
/// so already-emitted #3 boxes stay openable; only NEW writes revert to #1. Any unwrap regression in CI / canary
/// ⇒ flip the policy off ⇒ the writer is back to suite #1 with no data loss.
/// </para>
/// <para>
/// <b>Default = OFF (fail-closed to the safe legacy behavior).</b> The shipped production null-object
/// (<see cref="DisabledHybridKemWritePolicy"/>) returns <c>false</c>, so until a host EXPLICITLY composes a
/// policy that returns <c>true</c>, the writer behaves exactly as before this increment (suite #1 only). Turning
/// the cutover ON is a deliberate host-composition act (a config flag wired to a policy that returns <c>true</c>),
/// not a default.
/// </para>
/// <para>
/// <b>It does NOT decide per-recipient capability.</b> Capability (does THIS recipient have a published X-Wing
/// key?) is a separate, orthogonal gate: the writer emits suite #3 only when the policy is on AND the recipient
/// is confirmed X-Wing-capable (a non-null recipient X-Wing public key supplied by the roster-bound resolver).
/// The policy is the GLOBAL on/off; capability is the PER-RECIPIENT degrade. Both must hold to emit #3.
/// </para>
/// </remarks>
public interface IHybridKemWritePolicy
{
    /// <summary>
    /// Whether the writer may emit the hybrid suite #3 for an X-Wing-capable recipient. <c>false</c> ⇒ the writer
    /// emits suite #1 regardless of recipient capability (the kill-switch / pre-cutover state). <c>true</c> ⇒ the
    /// writer emits suite #3 for X-Wing-capable recipients (and still suite #1 for incapable ones — the safe
    /// degrade). Read at each wrap so flipping the policy takes effect on the next write with no restart.
    /// </summary>
    bool HybridWritesEnabled { get; }
}

/// <summary>
/// The shipped PRODUCTION default — the fail-closed null-object that DISABLES hybrid writes (the pre-cutover /
/// kill-switch-off state). A host that has not deliberately turned the 2c-iii-b cutover on resolves this, so the
/// writer boxes suite #1 only. This is the same no-mock / fail-closed-default discipline the rest of the
/// key-distribution seam uses: the safe state is the default, and enabling the hybrid is an explicit act.
/// </summary>
public sealed class DisabledHybridKemWritePolicy : IHybridKemWritePolicy
{
    /// <summary>The shared instance (the policy is stateless).</summary>
    public static readonly DisabledHybridKemWritePolicy Instance = new();

    /// <inheritdoc />
    public bool HybridWritesEnabled => false;
}

/// <summary>
/// A simple mutable/explicit policy for a host that wires the 2c-iii-b cutover to a config flag (and for tests
/// that prove the kill-switch). The host constructs it from its config (e.g. an env var / settings value) and
/// can flip <see cref="HybridWritesEnabled"/> to revert the writer to suite #1 without a restart — the cutover
/// kill-trigger.
/// </summary>
public sealed class ConfigurableHybridKemWritePolicy : IHybridKemWritePolicy
{
    /// <summary>Construct with the initial enabled state (default <c>false</c> — off, the safe pre-cutover state).</summary>
    public ConfigurableHybridKemWritePolicy(bool enabled = false) => HybridWritesEnabled = enabled;

    /// <inheritdoc />
    public bool HybridWritesEnabled { get; set; }
}
