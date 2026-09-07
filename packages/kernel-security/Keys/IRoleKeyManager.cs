using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;

namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// Per-role symmetric-key lifecycle manager. Implements paper §11.3:
/// <list type="number">
///   <item>admin generates a new per-role key (<see cref="GenerateRoleKey"/>);</item>
///   <item>wraps it for each qualifying member using X25519 sealed-box (<see cref="WrapRoleKey"/>);</item>
///   <item>publishes the wrapped bundles as administrative events in the log;</item>
///   <item>each member unwraps its bundle with its private key (<see cref="UnwrapRoleKey"/>);</item>
///   <item>the member caches the key in the OS keystore (<see cref="StoreRoleKeyAsync"/>).</item>
/// </list>
/// Rotation is achieved by re-running the issuance flow and omitting revoked members.
/// </summary>
public interface IRoleKeyManager
{
    /// <summary>Admin side: generate a new 32-byte symmetric key for a role.</summary>
    ReadOnlyMemory<byte> GenerateRoleKey();

    /// <summary>
    /// Admin side: wrap a role key for a specific member. Suite #1 (default) uses the admin's X25519 private key
    /// + the member's X25519 public key (the authenticated box). Suite #3 (the 2c-iii-b writer flip) is emitted
    /// when (a) the hybrid-write policy is ON, (b) a <paramref name="memberXWingPublicKey"/> is supplied (the
    /// recipient is X-Wing-capable), and (c) a <paramref name="signer"/> is supplied for the NIT-1 outer Ed25519
    /// signature — otherwise it boxes suite #1 (the safe degrade).
    /// </summary>
    /// <param name="roleKey">The 32-byte role key to wrap.</param>
    /// <param name="role">The role this key grants (bound into the suite-#3 signable).</param>
    /// <param name="memberPublicKey">The recipient's 32-byte X25519 public key (suite-#1 box recipient).</param>
    /// <param name="adminPrivateKey">The admin's X25519 private key (suite-#1 authenticated-box sender).</param>
    /// <param name="adminPublicKey">The admin's X25519 public key (suite-#1 sender binding).</param>
    /// <param name="memberXWingPublicKey">The recipient's 1216-byte X-Wing public key — the per-recipient
    /// capability gate (2c-iii-b). Non-empty + policy-on + signer-supplied ⇒ suite #3; otherwise ⇒ suite #1.
    /// OPTIONAL (default empty) so existing call sites stay source-compatible (suite #1).</param>
    /// <param name="signer">The admin's Ed25519 <see cref="IOperationSigner"/> for the suite-#3 NIT-1 outer
    /// signature (restoring the admin-sender authentication X-Wing's anonymous-sender KEM drops vs suite #1's
    /// authenticated box). REQUIRED to emit suite #3; OPTIONAL (default null) for suite #1.</param>
    RoleKeyBundle WrapRoleKey(
        ReadOnlyMemory<byte> roleKey,
        string role,
        byte[] memberPublicKey,
        ReadOnlyMemory<byte> adminPrivateKey,
        byte[] adminPublicKey,
        ReadOnlyMemory<byte> memberXWingPublicKey = default,
        IOperationSigner? signer = null);

    /// <summary>
    /// Member side: unwrap a received role-key bundle using this node's private key for the bundle's KEM
    /// suite. Throws on authentication failure — the caller must handle it.
    /// </summary>
    /// <remarks>
    /// <b>Suite acceptance (PQC Phase 2 / ADR 0004 Amendment 2).</b> A suite-#1 (X25519) bundle opens under
    /// <paramref name="memberPrivateKey"/>. A suite-#3 (X-Wing) bundle opens under
    /// <paramref name="memberXWingPrivateKeySeed"/> when the recipient is X-Wing-capable (it supplies the seed
    /// AND the implementation was composed with the X-Wing box) — the additive READ-before-write capability:
    /// production can OPEN suite #3 BEFORE any writer EMITS it. An X-Wing-incapable recipient (no seed) safely
    /// degrades — a suite-#3 bundle fails closed. Suite #2 and any unregistered suite are always denied. The
    /// wrap path still produces suite #1 (the writer flip is the separately-gated 2c-iii-b CP cutover).
    /// </remarks>
    /// <param name="bundle">The role-key bundle to open.</param>
    /// <param name="memberPrivateKey">This node's 32-byte X25519 private key — opens a suite-#1 bundle.</param>
    /// <param name="adminPublicKey">The issuing admin's X25519 public key (suite-#1 sender binding).</param>
    /// <param name="memberXWingPrivateKeySeed">This node's 32-byte node-secret X-Wing private-key seed
    /// (HKDF(node-root, teamId) over the X-Wing domain — see <c>IXWingSubkeyDerivation</c>) — opens a suite-#3
    /// bundle. OPTIONAL: omit (default) for an X-Wing-incapable recipient; a suite-#3 bundle then fails closed.</param>
    /// <param name="signatureVerifier">The Ed25519 <see cref="IOperationVerifier"/> the suite-#3 path uses to
    /// verify the NIT-1 outer signature (2c-iii-b). REQUIRED to open a suite-#3 bundle; a suite-#3 bundle fails
    /// closed without it (the signature cannot be checked). Unused for suite #1 (the box authenticates the
    /// sender). OPTIONAL (default null) so existing suite-#1 call sites stay source-compatible.</param>
    /// <param name="expectedSignerPublicKey">The admin's EXPECTED Ed25519 signing public key (32-byte) the
    /// suite-#3 bundle's <see cref="RoleKeyBundle.SignerPublicKey"/> must equal — the authority anchor (the
    /// caller resolves it from its own verified roster). A suite-#3 bundle whose stamped signer differs is
    /// rejected even if its signature is internally valid. REQUIRED to open a suite-#3 bundle. OPTIONAL (default
    /// null) for suite #1.</param>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The bundle was not produced for this recipient, was tampered with, was not issued by
    /// <paramref name="adminPublicKey"/> (suite #1) / <paramref name="expectedSignerPublicKey"/> (suite #3), or
    /// carries a suite this recipient cannot open.
    /// </exception>
    ReadOnlyMemory<byte> UnwrapRoleKey(
        RoleKeyBundle bundle,
        ReadOnlyMemory<byte> memberPrivateKey,
        byte[] adminPublicKey,
        ReadOnlyMemory<byte> memberXWingPrivateKeySeed = default,
        IOperationVerifier? signatureVerifier = null,
        byte[]? expectedSignerPublicKey = null);

    /// <summary>
    /// Store an unwrapped role key in the OS keystore under a namespaced key-name.
    /// Naming convention: <c>sunfish:role-key:{role}</c>.
    /// </summary>
    Task StoreRoleKeyAsync(string role, ReadOnlyMemory<byte> roleKey, CancellationToken ct);

    /// <summary>Retrieve a previously-stored role key, or <c>null</c> if absent.</summary>
    Task<ReadOnlyMemory<byte>?> GetRoleKeyAsync(string role, CancellationToken ct);
}

/// <summary>
/// Wire-format role-key bundle — published as an administrative event in the log.
/// </summary>
/// <param name="Role">Role this key grants access to.</param>
/// <param name="MemberPublicKey">32-byte X25519 public key of the intended recipient.</param>
/// <param name="WrappedKey">Ciphertext: X25519-sealed-box ciphertext of the role key (48 bytes = 32-byte key + 16-byte tag).</param>
/// <param name="Nonce">24-byte nonce generated at wrap time.</param>
public sealed record RoleKeyBundle(
    string Role,
    byte[] MemberPublicKey,
    byte[] WrappedKey,
    byte[] Nonce)
{
    /// <summary>
    /// The sealed-box KEM suite that produced <see cref="WrappedKey"/> (ADR 0004 §1 KEM axis).
    /// Defaults to <see cref="KemSuites.LegacyDefault"/> (suite #1, the X25519 sealed box) so the
    /// 4-argument positional construction and every legacy untagged bundle resolve to the construction
    /// already in use. NOT part of the positional primary constructor — deliberately, to keep all
    /// existing call sites source-compatible. New code may set an explicit suite via a <c>with</c>
    /// expression once a second suite exists (Phase 2b).
    /// </summary>
    /// <remarks>
    /// <b>Back-compat seam.</b> A bundle deserialized from a pre-this-change administrative event carries
    /// no suite tag; the reader leaves this at <see cref="KemSuites.LegacyDefault"/>. A future
    /// hybrid-aware unwrap path MUST fail closed on an unregistered suite rather than silently treating
    /// an unknown tagged value as suite #1 — see <see cref="KemSuites.IsRegistered"/>.
    /// </remarks>
    public KemSuite Suite { get; init; } = KemSuites.LegacyDefault;

    /// <summary>
    /// The X-Wing KEM ciphertext (suite #3 only, ADR 0004 Amendment 2 GATE condition 2 / D1 — the role-key
    /// equivalent of the tenant-DEK envelope-signing fix). For a suite-#3
    /// (<see cref="KemSuite.XWingX25519MlKem768_v1"/>) bundle this carries the 1120-byte X-Wing ciphertext
    /// (<c>ct_M ‖ ct_X</c>) the recipient needs to recover the X-Wing shared secret. <c>null</c> for suites
    /// #1/#2 (whose X25519 box carries the sender-ephemeral public key in <see cref="MemberPublicKey"/>'s
    /// counterpart on the wire, not a separate KEM ciphertext). For suite #3 the KEM ciphertext travels here
    /// and the unwrap path binds it as the recipient input to X-Wing Decaps — a substituted/tampered KEM
    /// ciphertext yields a different X-Wing secret and fails the box's AEAD tag (fail-closed). It is ALSO bound
    /// into the suite-#3 outer signature (<see cref="Signature"/>) below.
    /// </summary>
    public byte[]? KemCiphertext { get; init; }

    /// <summary>
    /// The suite-#3 OUTER Ed25519 signer public key (base64url) — the admin/issuer whose signature
    /// (<see cref="Signature"/>) authenticates a suite-#3 (X-Wing) bundle (the NIT-1 decision; PQC Phase 2 /
    /// BL-01 increment 2c-iii-b; ADR 0004 Amendment 2). <b>Why suite #3 needs this and suites #1/#2 do not:</b>
    /// the suite-#1 X25519 box is an AUTHENTICATED box — it is sealed under the admin's X25519 private key and
    /// opened under the admin's X25519 PUBLIC key (passed to <c>UnwrapRoleKey</c>), so the box itself binds the
    /// sender. X-Wing (suite #3) is an ANONYMOUS-sender KEM (it encapsulates to the recipient with no sender key
    /// in the construction), so switching to it would DROP the admin-sender authentication suite #1 had. The
    /// outer Ed25519 signature restores it: a suite-#3 bundle is honored only if this key's signature over the
    /// canonical <see cref="RoleKeyBundleSignable"/> verifies AND the caller confirms this key is the expected
    /// admin (the same authority-anchor discipline the tenant-DEK envelope uses). <c>null</c> for suites #1/#2
    /// (the box authenticates the sender there). The 32-byte Ed25519 public key as base64url.
    /// </summary>
    public string? SignerPublicKey { get; init; }

    /// <summary>
    /// The suite-#3 OUTER Ed25519 signature (base64url) over the canonical <see cref="RoleKeyBundleSignable"/>
    /// — the NIT-1 binding (PQC Phase 2 / BL-01 increment 2c-iii-b). It covers the role, the KEM ciphertext, the
    /// recipient X-Wing public key, the AEAD ciphertext + nonce — so a re-pointed recipient key, a substituted
    /// KEM ciphertext, or a swapped role all fail closed at verify, and (with <see cref="SignerPublicKey"/>) the
    /// admin-sender is authenticated (restoring what the suite-#1 authenticated box gave). <c>null</c> for
    /// suites #1/#2.
    /// </summary>
    public string? Signature { get; init; }
}

/// <summary>
/// The canonical SIGNABLE payload the suite-#3 role-key OUTER Ed25519 signature covers (NIT-1; PQC Phase 2 /
/// BL-01 increment 2c-iii-b) — everything that authenticates a suite-#3 <see cref="RoleKeyBundle"/> EXCEPT the
/// signature itself. Serialized via the proven <see cref="Harborline.Api.Foundation.Crypto.IOperationSigner"/>
/// canonical-JSON path (byte-stable, cross-language), the SAME discipline the tenant-DEK wrap signable uses. The
/// byte fields are base64url so the canonical-JSON form is deterministic across platforms.
/// </summary>
/// <param name="Role">The role the bundle grants (bound so a bundle for role-A cannot be replayed as role-B).</param>
/// <param name="WrappedKeyB64Url">base64url of the AEAD ciphertext of the role key.</param>
/// <param name="NonceB64Url">base64url of the 24-byte AEAD nonce.</param>
/// <param name="KemCiphertextB64Url">base64url of the 1120-byte X-Wing KEM ciphertext (a substituted/tampered KEM
/// ciphertext breaks this signature — the role-key envelope-signing fix).</param>
/// <param name="RecipientXWingPublicKeyB64Url">base64url of the recipient's 1216-byte X-Wing public key the
/// bundle was sealed FOR (a bundle re-pointed at a different recipient key fails closed).</param>
public sealed record RoleKeyBundleSignable(
    string Role,
    string WrappedKeyB64Url,
    string NonceB64Url,
    string KemCiphertextB64Url,
    string RecipientXWingPublicKeyB64Url);
