using System.Security.Cryptography;
using System.Text;

using NSec.Cryptography;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// TEST-ONLY <see cref="IParticipantDmKeyResolver"/> — a deterministic, party-id-keyed X25519 keypair source used
/// ONLY to exercise the DM seal/unseal CONSTRUCTION (ECDH+HKDF, AAD, sign-then-encrypt, tamper fail-closed) in the
/// Tier-1 test harness. It lives in the TEST assembly and is NEVER registered in the production composition (the
/// arch-fence <c>ProductionComposition_RegistersFailClosed_NoDmKeyProvider</c> proves prod registers
/// <see cref="NoDmConversationKeyProvider"/> instead). It is NOT a confidentiality primitive.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS IS NOT A CONFIDENTIALITY PRIMITIVE — and must never ship.</b> This resolver derives EVERY party's
/// DM keypair (private AND public) from a single compile-time-constant IKM keyed ONLY by the party id. The node
/// root seed is deliberately NOT mixed in (it cannot be — two test nodes must compute the SAME peer keypair from a
/// party id alone, with no wire distribution). The consequence, surfaced by the security-engineering deep-review of
/// PR #1325: <b>anyone holding this assembly can derive ANY party's DM PRIVATE key from a party id + the constant.</b>
/// A non-participant who simply <i>claims a participant's party id</i> derives the identical <c>K_dm</c> and
/// decrypts the body. The <c>DerivedDmConversationKeyProvider</c> "leak guard" (the <c>me ∈ {A,B}</c> self-check)
/// is an IDENTITY/routing check, not a cryptographic barrier — it is bypassed by lying about who you are.
/// </para>
/// <para>
/// So a leak test run against THIS resolver proves a property of the CONSTRUCTION (seal/unseal/AAD/tamper round-trip
/// correctly; a non-participant who honestly reports a non-participant id gets <c>null</c>), <b>NOT</b> a
/// confidentiality GUARANTEE (a non-participant CANNOT obtain the key even when lying about their id). That real
/// no-leak guarantee is delivered ONLY by the C5 roster-bound resolver — each node generates its DM private key
/// from its OWN root seed and never exposes a party-id-derivable private key — and the C5 leak test (a forged party
/// id yields a DIFFERENT, useless key) lands with that resolver.
/// </para>
/// <para>
/// <b>What carries forward to C5.</b> The KEY CONSTRUCTION (<see cref="DerivedDmConversationKeyProvider"/>) is
/// sound and unchanged across the swap: the X25519 ECDH + HKDF, the AAD context binding, the sign-then-encrypt
/// order, the contributory-check. Only the resolver — WHERE the peer public key comes from and how the private key
/// is bound — changes. This test double exists so that sound construction can be regression-tested now; it is
/// structurally isolated in the test assembly so it can never become the production provider.
/// </para>
/// </remarks>
public sealed class SeedDerivedParticipantDmKeyResolver : IParticipantDmKeyResolver
{
    private static readonly KeyAgreementAlgorithm Kem = KeyAgreementAlgorithm.X25519;

    // TEST-DOUBLE derivation (INSECURE BY DESIGN — must never ship; see the class remarks): BOTH the active
    // member's private key and ANY party's public key derive from the SAME compile-time-constant, party-id-keyed
    // HKDF. This is the self-consistency the harness needs (the public key node-B computes for party-A off A's
    // party id MUST match the private key node-A holds off the SAME party id, else the ECDH disagrees), and it is
    // EXACTLY why this is not a confidentiality primitive: the private key is recoverable by anyone with this
    // assembly + a party id (the root seed is deliberately NOT mixed in). The C5 ROSTER-BOUND resolver replaces
    // this: each node generates its OWN DM keypair from its OWN root seed + publishes the PUBLIC key on the
    // enrollment wire, so a private key is node-secret and a forged party id yields a useless key.
    // DerivedDmConversationKeyProvider's ECDH is unchanged across the swap.
    private const string PartyKeypairInfoPrefix = "sunfish-dm-subkey-testdouble:v1:";
    private static readonly byte[] TestDoubleIkm = "sunfish-dm-testdouble-ikm"u8.ToArray();

    private readonly byte[] _activeDmPrivateKey;

    /// <summary>The active (local) member's party id.</summary>
    public string ActiveMemberPartyId { get; }

    /// <inheritdoc />
    public ReadOnlySpan<byte> ActiveMemberDmPrivateKey => _activeDmPrivateKey;

    /// <summary>
    /// Construct the test-double resolver for the active member. (The active member's DM private key derives from the
    /// SAME party-id-keyed HKDF a peer uses to compute its public key — so the ECDH is self-consistent across test
    /// nodes without wire distribution. The <paramref name="rootSeed"/> is accepted for signature-compatibility with
    /// the C5 roster-bound resolver — where the private key folds in the root seed — but is NOT mixed in here,
    /// because mixing it would break the cross-node public/private agreement the harness needs. This is the exact
    /// reason this double is insecure and must never ship.)
    /// </summary>
    /// <param name="rootSeed">The node's 32-byte root seed (signature-compatible with C5; NOT used here — validated non-empty only).</param>
    /// <param name="activeMemberPartyId">The active member's party id.</param>
    public SeedDerivedParticipantDmKeyResolver(ReadOnlySpan<byte> rootSeed, string activeMemberPartyId)
    {
        if (rootSeed.Length == 0) throw new ArgumentException("Root seed must be non-empty.", nameof(rootSeed));
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMemberPartyId);
        ActiveMemberPartyId = activeMemberPartyId;

        // The active member's DM private key — the SAME deterministic party-id-keyed derivation a peer uses to
        // compute this member's PUBLIC key (so ECDH(myPriv, peerPub) == ECDH(peerPriv, myPub) across nodes).
        _activeDmPrivateKey = DerivePartyPrivateKey(activeMemberPartyId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// TEST DOUBLE: a party's DM public key derives deterministically from its party id (compile-time-constant HKDF),
    /// so every test node computes the SAME keypair for the same party with no wire distribution — letting the
    /// derived-ECDH construction round-trip now. C5 replaces this with the roster-bound public key each node
    /// published at enrollment. (Because the matching PRIVATE key is equally derivable from the party id, this is a
    /// construction harness, not a secrecy boundary.)
    /// </remarks>
    public byte[]? TryResolveDmPublicKey(string partyId)
    {
        if (string.IsNullOrWhiteSpace(partyId)) return null;
        var priv = DerivePartyPrivateKey(partyId);
        try
        {
            return PublicKeyFromPrivate(priv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
        }
    }

    /// <summary>The party-id-keyed deterministic X25519 private key (the test-double keypair source — insecure).</summary>
    private static byte[] DerivePartyPrivateKey(string partyId) =>
        DeriveX25519PrivateKey(TestDoubleIkm, PartyKeypairInfoPrefix + partyId);

    private static byte[] DeriveX25519PrivateKey(ReadOnlySpan<byte> ikm, string info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: ikm,
            output: output,
            salt: ReadOnlySpan<byte>.Empty,
            info: Encoding.UTF8.GetBytes(info));
        return output; // NSec applies RFC 7748 clamping on Import — do NOT pre-clamp.
    }

    private static byte[] PublicKeyFromPrivate(ReadOnlySpan<byte> privateKey)
    {
        using var key = Key.Import(Kem, privateKey, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }
}
