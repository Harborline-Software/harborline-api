using System.Security.Cryptography;
using System.Text;

using Harborline.Api.Kernel.Security.Crypto;

namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// Default <see cref="IXWingSubkeyDerivation"/>. HKDF-Expand-SHA256 over the install root seed produces a
/// 32-byte X-Wing decapsulation-key <b>seed</b>; <see cref="IXWingKem.DerivePublicKey"/> expands that seed
/// (via <c>SHAKE256(seed, 96)</c> → ML-KEM-768 key-gen + X25519) into the matching 1216-byte X-Wing public
/// key. Stateless; one instance can be shared across the process.
/// </summary>
/// <remarks>
/// Mirrors the shape of <see cref="HkdfX25519SubkeyDerivation"/> (the recovery-X25519 counterpart) — same
/// HKDF-Expand pattern over the root seed, distinct info prefix — but its output is an X-Wing SEED, not an
/// X25519 raw scalar, so it is NOT clamped and is fed straight to the X-Wing KEM. The X-Wing key-pair
/// expansion is delegated to the injected <see cref="IXWingKem"/> so the production key-derivation path is
/// the one the X-Wing KAT vectors validate (no duplicated combiner here).
/// </remarks>
public sealed class HkdfXWingSubkeyDerivation : IXWingSubkeyDerivation
{
    /// <summary>HKDF info-string prefix. Version-stamped (PQC Phase 2 / increment 2c-iii) so a future v2
    /// derivation can coexist with deployed v1 installs. Distinct from the Ed25519, recovery-X25519,
    /// DM-X25519, and SQLCipher prefixes (domain separation across all root-derived keys).</summary>
    public const string InfoPrefix = "sunfish-xwing-team-v1:";

    /// <summary>Length of the X-Wing private-key seed in bytes (32 — the X-Wing decapsulation key).</summary>
    public const int SeedLength = 32;

    private readonly IXWingKem _xwing;

    /// <summary>Construct over the X-Wing KEM primitive (used to expand a derived seed → public key).</summary>
    public HkdfXWingSubkeyDerivation(IXWingKem xwing)
    {
        _xwing = xwing ?? throw new ArgumentNullException(nameof(xwing));
    }

    /// <inheritdoc />
    public byte[] DeriveXWingPrivateKeySeed(ReadOnlyMemory<byte> rootSeed, string teamId)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        if (rootSeed.Length == 0)
        {
            throw new ArgumentException("Root seed must be non-empty.", nameof(rootSeed));
        }

        var prefix = Encoding.UTF8.GetBytes(InfoPrefix);
        var teamBytes = Encoding.UTF8.GetBytes(teamId);
        var info = new byte[prefix.Length + teamBytes.Length];
        Buffer.BlockCopy(prefix, 0, info, 0, prefix.Length);
        Buffer.BlockCopy(teamBytes, 0, info, prefix.Length, teamBytes.Length);

        var output = new byte[SeedLength];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: rootSeed.Span,
            output: output,
            salt: ReadOnlySpan<byte>.Empty,
            info: info);
        // The 32 bytes ARE the X-Wing decapsulation-key seed — raw, NOT clamped (the X-Wing key-gen expands
        // them via SHAKE256 internally; do NOT pre-transform).
        return output;
    }

    /// <inheritdoc />
    public byte[] DeriveXWingPublicKey(ReadOnlyMemory<byte> rootSeed, string teamId)
    {
        var seed = DeriveXWingPrivateKeySeed(rootSeed, teamId);
        try
        {
            return _xwing.DerivePublicKey(seed);
        }
        finally
        {
            // Zero the intermediate seed so it does not linger on the GC heap (W#67 PR 5 council R-1
            // discipline, mirrored from HkdfX25519SubkeyDerivation).
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}
