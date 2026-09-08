namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// Derives a human-verifiable short authentication string from an authenticated shared secret.
/// Implementations are pure: they perform no I/O, read no clock, and retain no key material.
/// </summary>
public interface ISafetyCodeDerivation
{
    /// <summary>
    /// Derives a decimal safety code containing <paramref name="digits"/> payload digits followed by
    /// one Damm check digit. The secret must contain at least 256 bits of authenticated shared entropy.
    /// Public handshake transcripts are not secrets and must never be supplied as
    /// <paramref name="sharedSecret"/>.
    /// </summary>
    /// <param name="sharedSecret">Authenticated shared secret bytes available identically to both parties.</param>
    /// <param name="engagementId">Engagement identifier encoded as UTF-8 and used as the HKDF salt.</param>
    /// <param name="label">Consumer label appended to the versioned HKDF info prefix.</param>
    /// <param name="digits">Number of security-bearing decimal digits, excluding the check digit.</param>
    string Derive(
        ReadOnlyMemory<byte> sharedSecret,
        string engagementId,
        string label,
        int digits = SasEncode.DefaultDigits);
}
