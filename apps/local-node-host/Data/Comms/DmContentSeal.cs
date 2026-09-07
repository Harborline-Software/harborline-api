using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The DM body SEAL (C4) — the AEAD envelope that turns a 1:1 DM message body from plaintext into ciphertext
/// readable ONLY by the two participants. A sealed body is opaque to a non-participant — including a TEAM MEMBER
/// on the same sync plane (the design's load-bearing privacy property §2.2) — because the per-conversation AEAD
/// key (<see cref="IDmConversationKeyProvider"/>) is derivable only by the two participants. This is the
/// guarantee that survives a routing bug, a malicious peer, or a future WAN (signal-bridge) relay that sees
/// ciphertext it cannot read.
/// </summary>
/// <remarks>
/// <para>
/// <b>AEAD construction (DR-2 nonce discipline).</b> ChaCha20-Poly1305 (the same AEAD the kernel-security
/// sealed-box uses) under the 32-byte per-conversation key, with a FRESH 12-byte random nonce per message
/// (<see cref="RandomNumberGenerator"/>). The wire/at-rest envelope is
/// <c>"enc:v1:" + base64url(nonce(12) ‖ ciphertext+tag(16))</c> — version-prefixed so a future v2 (e.g. a
/// per-message ratchet) can coexist. The fresh-random nonce per message under a long-lived per-conversation key
/// is safe at the local-office message volume (collision probability is negligible at 96-bit random nonces for
/// the message counts a DM thread reaches); the honest forward-secrecy limitation (one long-term per-pair key)
/// is documented in the ADR + flagged for the deferred ratchet hardening.
/// </para>
/// <para>
/// <b>The sign/encrypt order + AAD (DR-3 — authenticate the ciphertext CONTEXT).</b> The author SIGNS the
/// plaintext signable payload FIRST (sign-then-encrypt — authorship is proven over the decrypted content, so a
/// participant verifies who wrote what they read), THEN the body is sealed. The AEAD's ASSOCIATED DATA binds
/// the immutable message context — <c>{conversationId, tenantId, authorPartyId, messageId}</c> — into the
/// authentication tag. So a ciphertext cannot be lifted out of its thread and replayed into another (the
/// conversationId is in the AAD AND in the signed payload — defence in depth), nor re-attributed to a different
/// author, nor moved to a different message id, without the tag failing. The signature over the plaintext +
/// the AAD over the envelope context together defend KCI / surreptitious-forwarding / replay (sec-eng DR-3
/// anchor): a non-participant who lifts the sealed envelope cannot decrypt it (no key) AND cannot re-context it
/// (AAD-bound), and a participant who decrypts it re-verifies authorship over the plaintext.
/// </para>
/// <para>
/// <b>Tamper-evident, fail-closed.</b> <see cref="TryUnseal"/> returns false on ANY authentication failure
/// (wrong key, tampered ciphertext, mismatched AAD context) — never throws on tampering, never returns partial
/// plaintext. The merge gate treats an unsealable DM body as a dropped/opaque message (it never reaches the
/// readable plaintext store). Sealing is deterministic-shape, dependency-free (BCL ChaCha20-Poly1305) so every
/// node seals/unseals identically.
/// </para>
/// </remarks>
public static class DmContentSeal
{
    /// <summary>The versioned envelope prefix marking a sealed DM body (vs a plaintext team body).</summary>
    public const string EnvelopePrefix = "enc:v1:";

    /// <summary>The per-conversation AEAD key length (32 bytes — ChaCha20-Poly1305).</summary>
    public const int KeyLength = 32;

    private const int NonceLength = 12;   // ChaCha20-Poly1305 nonce
    private const int TagLength = 16;     // Poly1305 authentication tag
    private const byte AadSeparator = 0x00; // NUL joins the AAD context fields (no concat-ambiguity)

    /// <summary>
    /// True if <paramref name="body"/> is a sealed DM envelope (carries the <see cref="EnvelopePrefix"/>). A
    /// plaintext team body never starts with the prefix — the arch-test fence + the read path use this to tell a
    /// sealed DM body from a plaintext team body.
    /// </summary>
    public static bool IsSealed(string? body) =>
        body is not null && body.StartsWith(EnvelopePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Seal <paramref name="plaintext"/> under the per-conversation <paramref name="key"/>, binding the message
    /// CONTEXT into the AEAD associated data (DR-3). Returns the versioned, base64url envelope
    /// (<c>"enc:v1:" + base64url(nonce ‖ ciphertext+tag)</c>).
    /// </summary>
    /// <param name="plaintext">The DM message body to seal.</param>
    /// <param name="key">The 32-byte per-conversation AEAD key (only the two participants can derive it).</param>
    /// <param name="conversationId">The DM conversation id — bound into the AAD (replay-into-another-thread defence).</param>
    /// <param name="tenantId">The team tenant — bound into the AAD (cross-team replay defence).</param>
    /// <param name="authorPartyId">The author party id — bound into the AAD (re-attribution defence).</param>
    /// <param name="messageId">The message id — bound into the AAD (cross-message lift defence).</param>
    public static string Seal(
        string plaintext,
        ReadOnlySpan<byte> key,
        string conversationId,
        string tenantId,
        string authorPartyId,
        string messageId)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (key.Length != KeyLength)
            throw new ArgumentException($"The per-conversation key must be {KeyLength} bytes (was {key.Length}).", nameof(key));

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var aad = BuildAad(conversationId, tenantId, authorPartyId, messageId);

        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];
        using (var aead = new ChaCha20Poly1305(key))
        {
            aead.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);
        }
        CryptographicOperations.ZeroMemory(plaintextBytes);

        // envelope = nonce(12) ‖ ciphertext ‖ tag(16) — base64url, version-prefixed.
        var envelope = new byte[NonceLength + ciphertext.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, envelope, 0, NonceLength);
        Buffer.BlockCopy(ciphertext, 0, envelope, NonceLength, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, envelope, NonceLength + ciphertext.Length, TagLength);

        return EnvelopePrefix + Base64Url.Encode(envelope);
    }

    /// <summary>
    /// Unseal a sealed DM body under the per-conversation <paramref name="key"/>, re-binding the SAME message
    /// CONTEXT as AAD. Returns <c>true</c> + the plaintext on success; <c>false</c> on ANY authentication failure
    /// (wrong key — a non-participant — tampered ciphertext, or a mismatched context) — never throws on tamper,
    /// never returns partial plaintext (fail-closed). A non-participant holding only the ciphertext + a wrong/no
    /// key gets <c>false</c>: it CANNOT read the body (DR-5, the leak property).
    /// </summary>
    public static bool TryUnseal(
        string? sealedBody,
        ReadOnlySpan<byte> key,
        string conversationId,
        string tenantId,
        string authorPartyId,
        string messageId,
        out string plaintext)
    {
        plaintext = string.Empty;
        if (!IsSealed(sealedBody)) return false;
        if (key.Length != KeyLength) return false;

        byte[] envelope;
        try
        {
            envelope = Base64Url.Decode(sealedBody!.AsSpan(EnvelopePrefix.Length));
        }
        catch (FormatException)
        {
            return false; // malformed envelope → not unsealable.
        }
        if (envelope.Length < NonceLength + TagLength) return false;

        var nonce = envelope.AsSpan(0, NonceLength);
        var cipherLen = envelope.Length - NonceLength - TagLength;
        var ciphertext = envelope.AsSpan(NonceLength, cipherLen);
        var tag = envelope.AsSpan(NonceLength + cipherLen, TagLength);
        var aad = BuildAad(conversationId, tenantId, authorPartyId, messageId);

        var plaintextBytes = new byte[cipherLen];
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            aead.Decrypt(nonce, ciphertext, tag, plaintextBytes, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            // Wrong key (a non-participant), tampered ciphertext, or mismatched AAD context — fail-closed.
            CryptographicOperations.ZeroMemory(plaintextBytes);
            return false;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            return false;
        }

        plaintext = Encoding.UTF8.GetString(plaintextBytes);
        CryptographicOperations.ZeroMemory(plaintextBytes);
        return true;
    }

    /// <summary>
    /// The AEAD associated data — the immutable message context bound into the authentication tag (DR-3). NUL
    /// joins the fields so distinct field boundaries hash distinctly (no concat-ambiguity), domain-prefixed so
    /// this AAD never collides with another use.
    /// </summary>
    private static byte[] BuildAad(string conversationId, string tenantId, string authorPartyId, string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        using var buffer = new MemoryStream();
        WriteUtf8(buffer, "sunfish-dm-seal:v1");
        buffer.WriteByte(AadSeparator);
        WriteUtf8(buffer, conversationId);
        buffer.WriteByte(AadSeparator);
        WriteUtf8(buffer, tenantId);
        buffer.WriteByte(AadSeparator);
        WriteUtf8(buffer, authorPartyId);
        buffer.WriteByte(AadSeparator);
        WriteUtf8(buffer, messageId);
        return buffer.ToArray();
    }

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>URL-safe base64 (no padding) — keeps the sealed body wire- + JSON-safe.</summary>
    private static class Base64Url
    {
        public static string Encode(ReadOnlySpan<byte> data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(ReadOnlySpan<char> value)
        {
            var s = new string(value).Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Invalid base64url length.");
            }
            return Convert.FromBase64String(s);
        }
    }
}
