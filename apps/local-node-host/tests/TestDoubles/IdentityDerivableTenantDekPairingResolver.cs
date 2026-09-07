using System.Security.Cryptography;
using System.Text;

using NSec.Cryptography;

using Harborline.Api.LocalNodeHost.Data.KeyDistribution;

namespace Harborline.Api.LocalNodeHost.Tests.TestDoubles;

/// <summary>
/// TEST-ONLY, INSECURE-BY-DESIGN <see cref="ITenantDekPairingResolver"/> — derives a recipient wrap key from the
/// PARTY ID alone (a compile-time-constant, party-id-keyed HKDF). It exists ONLY to make the MD-1 arch-fence's
/// false-positive scenario CONCRETE and to prove the <c>DekPairingResolverArchFence</c> structurally excludes it
/// from the shipped assembly. It lives in the TEST assembly and is NEVER registered in production.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS MUST NEVER SHIP (the #1325 / bug-1312 lesson, applied to DEK pairing).</b> Because the recipient key
/// is a function of the PARTY ID (the root/node secret is deliberately not mixed in), anyone holding this assembly
/// could compute the wrap key for ANY party by claiming that party's id — and a non-participant who LIES about being
/// an admitted party would receive a DEK wrapped to a key it controls. The protection would be an identity self-check,
/// not crypto: the dev-gate, not the roster, would be load-bearing for confidentiality. A leak test GREEN against
/// THIS resolver is a FALSE POSITIVE — it proves a party-id-keyed construction round-trips, NOT that an unadmitted
/// party cannot obtain the DEK.
/// </para>
/// <para>
/// The REAL no-leak guarantee is delivered ONLY by <see cref="RosterBoundTenantDekPairingResolver"/> — the recipient
/// key comes from the SIGNED, genesis-rooted admission (<c>MemberRoster.DmPublicKeyOf</c>), so a forged id resolves a
/// DIFFERENT (or null) key. The MD-1 leak test runs against THAT resolver; this double exists only to be fenced out.
/// </para>
/// </remarks>
public sealed class IdentityDerivableTenantDekPairingResolver : ITenantDekPairingResolver
{
    private static readonly KeyAgreementAlgorithm Kem = KeyAgreementAlgorithm.X25519;

    // INSECURE-BY-DESIGN: the recipient key derives from the party id + a compile-time constant — NO node/root secret.
    // This is exactly the identity-derivable resolver the arch-fence forbids in the shipped assembly.
    private const string InfoPrefix = "sunfish-dek-pairing-testdouble:v1:";
    private static readonly byte[] TestDoubleIkm = "sunfish-dek-pairing-testdouble-ikm"u8.ToArray();

    /// <inheritdoc />
    /// <remarks>Returns a party-id-derivable public key for ANY non-empty party id (no roster check) — the insecure
    /// behaviour the fence exists to keep out of production.</remarks>
    public byte[]? ResolveRecipientWrapKey(string recipientPartyId)
    {
        if (string.IsNullOrWhiteSpace(recipientPartyId))
        {
            return null;
        }

        var priv = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: TestDoubleIkm,
            output: priv,
            salt: ReadOnlySpan<byte>.Empty,
            info: Encoding.UTF8.GetBytes(InfoPrefix + recipientPartyId));
        try
        {
            using var key = Key.Import(Kem, priv, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
            });
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
        }
    }
}
