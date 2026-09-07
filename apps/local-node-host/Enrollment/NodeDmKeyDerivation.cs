using System.Security.Cryptography;
using System.Text;

using NSec.Cryptography;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// C5 — derives a node's TEAM-SCOPED DM-encryption X25519 keypair from its install ROOT secret + a team id, the
/// DM-confidentiality counterpart of the team TRANSPORT subkey (<c>TeamSubkeyDerivation</c>) and the recovery
/// X25519 subkey (<c>HkdfX25519SubkeyDerivation</c>). The PRIVATE half is NODE-SECRET (it derives from the install
/// root secret and never leaves the node); the PUBLIC half is published on the synced roster record + the
/// enrollment wire so a DM peer can run the X25519 ECDH against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the C5 confidentiality boundary (the fix for the C4 false-positive).</b> The retired C4 test
/// stand-in (<c>SeedDerivedParticipantDmKeyResolver</c>) derived every party's DM keypair from a compile-time
/// CONSTANT keyed only by PARTY ID — so anyone holding the binary could derive any party's DM private key by
/// claiming that party's id, and the "leak guard" was an identity self-check, not crypto (sec-eng deep-review of
/// PR #1325). This derivation keys the private half on the NODE's own root secret instead, so a non-participant —
/// even a team member who knows both party ids + the dm: hash — CANNOT derive a participant's DM private key (it
/// is not a function of the party id; it requires the participant's node root secret). The roster carries only the
/// PUBLIC half, which yields the per-conversation key ONLY when combined with a participant's own private key.
/// </para>
/// <para>
/// <b>Input key material.</b> The IKM is the install's root secret bytes — the SAME material
/// <c>TeamSubkeyDerivation</c> uses for the transport subkey (the Ed25519 root private key IS the 32-byte root
/// seed). Keying on the root secret (not the raw party id) makes the private key node-secret AND deterministically
/// reconstructible on every boot (no persistence needed — like the transport key). Domain-separated by the version
/// info prefix below so the DM keypair never collides with the team signing subkey
/// (<c>"sunfish-team-subkey-v1:"</c>), the recovery X25519 subkey (<c>"sunfish-x25519-team-v1:"</c>), or the
/// SQLCipher key — even though all derive from the same root secret.
/// </para>
/// <para>
/// <b>RFC 7748 clamping.</b> The 32 HKDF-output bytes are raw — NSec's <c>Key.Import(X25519, raw, RawPrivateKey)</c>
/// applies clamping internally during scalar multiplication, so callers MUST NOT pre-clamp (the same discipline
/// <c>HkdfX25519SubkeyDerivation</c> documents).
/// </para>
/// </remarks>
public static class NodeDmKeyDerivation
{
    private static readonly KeyAgreementAlgorithm Kem = KeyAgreementAlgorithm.X25519;

    /// <summary>HKDF info-string prefix — version-stamped + DM-domain-separated from every other root-derived key.
    /// A future v2 (e.g. a ratcheted DM keypair) can coexist with deployed v1.</summary>
    public const string InfoPrefix = "sunfish-dm-subkey-v1:";

    /// <summary>Length of an X25519 private (and public) key in bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>
    /// Derive the raw 32-byte X25519 DM PRIVATE key for <paramref name="teamId"/> from the install
    /// <paramref name="rootSecret"/> via HKDF-Expand-SHA256 with the DM info prefix. Node-secret: never leaves the
    /// node. Do NOT pre-clamp (NSec clamps on import).
    /// </summary>
    public static byte[] DeriveDmPrivateKey(ReadOnlySpan<byte> rootSecret, string teamId)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        if (rootSecret.Length == 0)
        {
            throw new ArgumentException("Root secret must be non-empty.", nameof(rootSecret));
        }

        var prefix = Encoding.UTF8.GetBytes(InfoPrefix);
        var teamBytes = Encoding.UTF8.GetBytes(teamId);
        var info = new byte[prefix.Length + teamBytes.Length];
        Buffer.BlockCopy(prefix, 0, info, 0, prefix.Length);
        Buffer.BlockCopy(teamBytes, 0, info, prefix.Length, teamBytes.Length);

        var output = new byte[KeyLength];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: rootSecret,
            output: output,
            salt: ReadOnlySpan<byte>.Empty,
            info: info);
        return output; // raw — NSec applies RFC 7748 clamping on Import; do NOT pre-clamp.
    }

    /// <summary>
    /// Derive the X25519 DM PUBLIC key (the published half) corresponding to <see cref="DeriveDmPrivateKey"/> for
    /// the same <paramref name="rootSecret"/> + <paramref name="teamId"/> — Curve25519 scalar multiplication of the
    /// (clamped) private key with the X25519 base point.
    /// </summary>
    public static byte[] DeriveDmPublicKey(ReadOnlySpan<byte> rootSecret, string teamId)
    {
        var raw = DeriveDmPrivateKey(rootSecret, teamId);
        try
        {
            using var key = Key.Import(
                Kem,
                raw,
                KeyBlobFormat.RawPrivateKey,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            // Zero the intermediate private-key buffer so it does not linger on the GC heap.
            CryptographicOperations.ZeroMemory(raw);
        }
    }
}
