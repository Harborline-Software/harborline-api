using System;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// Operator-configurable session TTL + entropy options (ADR 0099 S4/S6). Defaults are the
/// sec-eng-ratified substrate floor; both timeouts are operator-overridable but the substrate
/// invariant <c>idle ≤ absolute</c> is enforced (see <see cref="Validate"/>).
/// </summary>
/// <remarks>
/// These options govern the SERVER-SIDE gate at <c>OnValidatePrincipal</c>; the cookie's
/// <c>ExpireTimeSpan</c> is a redundant client hint, not the authority (ADR 0099 B/D1). The
/// 8h absolute matches a single financial workday; the 30min idle matches OWASP guidance for
/// sensitive/financial apps (15-30min; 30min is the ceiling, NOT 60 — 60 is acceptable only
/// as an explicit operator override, ADR 0099 O-2).
/// </remarks>
public sealed class SessionOptions
{
    /// <summary>
    /// Absolute session lifetime (ADR 0099 S6). Default 8h. The session dies at
    /// <c>IssuedUtc + AbsoluteLifetime</c> regardless of activity.
    /// </summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Sliding idle timeout (ADR 0099 S6). Default 30min. A session expires if it goes
    /// <see cref="IdleTimeout"/> without an authenticated request. MUST be ≤
    /// <see cref="AbsoluteLifetime"/>.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The number of random bytes in a generated opaque session id (ADR 0099 S4). Default 32
    /// (256-bit; matches/exceeds the orphaned mock's 24-32). The substrate floor is 16 bytes
    /// (128-bit); values below it are rejected (see <see cref="Validate"/>).
    /// </summary>
    public int SessionIdByteLength { get; set; } = 32;

    /// <summary>The substrate-floor minimum entropy for a session id, in bytes (≥128-bit; ADR 0099 S4).</summary>
    public const int MinimumSessionIdByteLength = 16;

    /// <summary>
    /// Validates the substrate invariants (ADR 0099 S4/S6): idle ≤ absolute, both positive,
    /// and the session-id entropy floor. Called at registration time by
    /// <c>AddHarborlineSessionEstablishment</c> so a misconfiguration fails fast rather than
    /// silently weakening the session security floor.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a timeout is non-positive, idle &gt; absolute, or the id byte length is
    /// below <see cref="MinimumSessionIdByteLength"/>.
    /// </exception>
    public void Validate()
    {
        if (AbsoluteLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AbsoluteLifetime), AbsoluteLifetime,
                "Session AbsoluteLifetime must be positive.");
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(IdleTimeout), IdleTimeout,
                "Session IdleTimeout must be positive.");
        }

        if (IdleTimeout > AbsoluteLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(IdleTimeout), IdleTimeout,
                $"Session IdleTimeout ({IdleTimeout}) must be <= AbsoluteLifetime "
                + $"({AbsoluteLifetime}) — ADR 0099 S6 substrate floor.");
        }

        if (SessionIdByteLength < MinimumSessionIdByteLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SessionIdByteLength), SessionIdByteLength,
                $"Session id entropy must be >= {MinimumSessionIdByteLength} bytes "
                + $"(>=128-bit) per ADR 0099 S4.");
        }
    }
}
