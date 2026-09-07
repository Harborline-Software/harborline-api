namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Configuration for the web-client LOGIN rate limiter / lockout (ADR 0099 S11 — the consumer of
/// the <c>Auth.LoginFailed</c> signal). Bound from <c>LocalNode:WebClient:Lockout</c>. Failed
/// attempts are tracked per (username, source address) over a sliding <see cref="Window"/>; once
/// <see cref="MaxFailures"/> failures accumulate inside the window the pair is locked out for
/// <see cref="BaseLockoutDuration"/>, doubling on each consecutive lockout up to
/// <see cref="MaxLockoutDuration"/> (exponential backoff). A successful login resets the pair.
/// </summary>
/// <remarks>
/// The limiter is IN-PROCESS state (this is a single local node — local-first, no external
/// services) and FAIL-CLOSED: any internal limiter error refuses the attempt rather than admitting
/// it. A locked-out attempt receives the SAME non-enumerating 401 body as a wrong password, so the
/// lockout state does not become an account-existence or lockout-state oracle in the response body
/// or status. This does not claim timing equalization: a lockout refusal returns before the
/// authority's Argon2id verification, so the accepted residual is timing-distinguishable. The
/// engage/release transitions are auditable server-side via <c>Auth.LoginLockout</c> /
/// <c>Auth.LoginLockoutReleased</c>. Only credential-mismatch refusals consume the failed-attempt
/// budget; malformed input, authority-gate refusals, and unavailable provisioning do not.
///
/// The source dimension has an accepted local residual: the default listener and the LAN-enabled
/// host's loopback leg bind to loopback, so every local process appears as the same source address.
/// On those paths the key therefore collapses to the credential-resolution identity (effectively
/// username-only), and any local process able to reach the listener can consume the operator's
/// budget and lock the operator out. The current node has no trustworthy pre-auth process identity;
/// deployments requiring that distinction must add one at the listener boundary.
/// </remarks>
public sealed class NodeWebLoginLockoutOptions
{
    /// <summary>
    /// Failed attempts inside <see cref="Window"/> that engage the lockout. Values below 1 are
    /// clamped to 1 by the limiter (a zero/negative threshold must harden, not disable).
    /// </summary>
    public int MaxFailures { get; set; } = 5;

    /// <summary>The sliding window failed attempts are counted over.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>First-lockout duration; doubles on each consecutive lockout (exponential backoff).</summary>
    public TimeSpan BaseLockoutDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound on the backed-off lockout duration.</summary>
    public TimeSpan MaxLockoutDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Cap on distinct (username, source) keys the limiter tracks, bounding memory against a
    /// key-churning attacker. When the table is full, the limiter prunes stale entries at most
    /// once per <see cref="PruneInterval"/> and evicts the least-recently-active entry that has no
    /// live lockout or pending reservation. A new key is refused only when every tracked entry is
    /// protected or otherwise non-evictable.
    /// </summary>
    public int MaxTrackedKeys { get; set; } = 10_000;

    /// <summary>
    /// Minimum interval between full-table stale-entry scans. A shorter interval improves recovery
    /// after expired reservations but costs more work under key churn; non-positive values harden to
    /// the limiter's safe fallback.
    /// </summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromSeconds(1);
}
