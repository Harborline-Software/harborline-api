namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// The per-code lifecycle policy (ADR 0152 §Item-(d)). The code is EPHEMERAL — recomputed on demand,
/// never persisted — so lifecycle here is the time/rotation policy the fail-closed gate consults, not
/// stored state.
/// </summary>
/// <param name="ExpiresAt">
/// Absolute expiry; <c>null</c> means "no time-based expiry" (revocation still applies through the
/// secret source returning <c>null</c>). Once <paramref name="ExpiresAt"/> passes, the gate fails closed.
/// </param>
/// <param name="RotationEpoch">
/// Optional mid-engagement rotation window. When set, the consumer seam (ADR 0152 Card C) mixes it into
/// the HKDF <c>info</c> so a past window's code stops validating; the one-shot-call default leaves it
/// <c>null</c> (the engagement id already binds replay resistance via the HKDF salt).
/// </param>
public readonly record struct VerificationLifecycle(
    DateTimeOffset? ExpiresAt = null,
    string? RotationEpoch = null)
{
    /// <summary>True once <paramref name="now"/> has reached <see cref="ExpiresAt"/> (if any).</summary>
    public bool Expired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;
}
