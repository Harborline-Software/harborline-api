namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// Yields the authenticated shared bytes for a verification context, or a fail-closed <c>null</c> when no
/// active, authenticated context exists (ADR 0152 §Item-(a)). Pluggable so each channel supplies its own
/// source over its own already-built substrate — Channel-1 reuses the ADR 0076 handshake or the ADR 0136
/// co-roster ECDH; Channel-2 (#2271) supplies a funded-contract source.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST fail closed: return <c>null</c> when the engagement is absent, expired, or
/// revoked, or when the key path is gated. They MUST NEVER return a guessable, default, or
/// identity-derivable secret — a value derivable from a public party id defeats the whole ceremony
/// (ADR 0152 F3; the shipped assembly is arch-fenced against any such source).
/// </para>
/// <para>
/// <b>ABSENCE returns null; a STRUCTURAL ANOMALY throws.</b> The two are deliberately different
/// signals and this contract permits both. <c>null</c> means "no authenticated secret is available
/// here" — an ordinary, expected outcome the caller handles by degrading to "verification
/// unavailable". A thrown exception means the inputs were not merely absent but WRONG in a way that
/// should never occur: a disposed source (<see cref="ObjectDisposedException"/>), or key material
/// that reached the agreement despite being degenerate
/// (<see cref="System.Security.Cryptography.CryptographicException"/> — see
/// <see cref="RosterEcdhSecretSource"/>, where a peer key rejected by X25519's contributory check
/// means signed roster data is carrying an invalid point). Swallowing the second class into
/// <c>null</c> would hide corrupt or attacker-supplied signed data behind the same quiet outcome as
/// a locked device, so implementations SHOULD let it surface. Callers that iterate several sources
/// must therefore be prepared for a throw, not just a null.
/// </para>
/// </remarks>
public interface ISharedVerificationSecretSource
{
    /// <summary>
    /// Returns the authenticated shared secret for <paramref name="ctx"/>, or <c>null</c> (fail-closed)
    /// when none is available. The caller owns and disposes the returned <see cref="VerificationSecret"/>.
    /// </summary>
    /// <remarks>
    /// Per the interface remarks, <c>Try</c> here means "null for expected ABSENCE", not "never
    /// throws": a disposed source or degenerate key material raises instead of returning <c>null</c>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The source has been disposed.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Key material that was surfaced as valid failed a cryptographic invariant (e.g. X25519's
    /// contributory check) — a structural anomaly, not an absence.
    /// </exception>
    VerificationSecret? TryGetSharedSecret(VerificationContext ctx);
}
