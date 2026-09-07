namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// Derives per-team <b>X-Wing</b> (X25519 + ML-KEM-768) key-pair material from the install's root seed —
/// the PQC Phase 2 / BL-01 increment <b>2c-iii</b> counterpart to <see cref="IX25519SubkeyDerivation"/>
/// (the recovery X25519 subkey) and <c>NodeDmKeyDerivation</c> (the DM X25519 subkey). It gives a recipient
/// the X-Wing keypair it needs to <b>open a suite-#3</b> (<see cref="Harborline.Api.Kernel.Security.Crypto.KemSuite.XWingX25519MlKem768_v1"/>)
/// sealed box, alongside its existing X25519 DM keypair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-before-write (ADR 0004 Amendment 2 GATE condition 4a).</b> A recipient gaining this X-Wing
/// keypair is what lets the production <b>READ</b> path open a suite-#3 box BEFORE any writer emits one —
/// so a future writer flip (2c-iii-b) can never strand key material. This derivation ships in the
/// read-capability increment; no production write path uses it to box yet.
/// </para>
/// <para>
/// <b>Same custody model as every other root-derived key — NO new custody.</b> The X-Wing private key is a
/// 32-byte <i>seed</i> (the X-Wing decapsulation key per <c>draft-connolly-cfrg-xwing-kem</c>); this
/// derivation produces it via HKDF-Expand-SHA256 over the install root seed, so it is <b>derived, never
/// separately stored</b>. The OS-keystore-held root seed (<c>IRootSeedProvider</c> / ADR 0118 custody
/// ladder) stays the sole custody root: the X-Wing seed is deterministically reconstructible on every boot,
/// exactly like the team transport / DM / recovery X25519 subkeys, and never leaves the node.
/// </para>
/// <para>
/// <b>Domain separation.</b> The HKDF info prefix <c>"sunfish-xwing-team-v1:"</c> is distinct from the
/// Ed25519 (<c>"sunfish-team-subkey-v1:"</c>), recovery-X25519 (<c>"sunfish-x25519-team-v1:"</c>), DM-X25519
/// (<c>"sunfish-dm-subkey-v1:"</c>), and SQLCipher (<c>"sunfish:sqlcipher:v1:"</c>) prefixes, so the X-Wing
/// seed for a team never collides with that team's signing, recovery, DM, or at-rest key — even though all
/// derive from the same 32-byte root seed. The version stamp lets a future v2 X-Wing derivation coexist with
/// deployed v1 installs.
/// </para>
/// <para>
/// <b>Seed semantics.</b> The 32 HKDF-output bytes ARE the X-Wing decapsulation-key seed — they are NOT an
/// X25519 raw scalar and are NOT clamped here. The X-Wing construction expands the seed via
/// <c>SHAKE256(seed, 96)</c> into the ML-KEM key-gen seed <c>(d ‖ z)</c> and the X25519 scalar internally
/// (see <see cref="Harborline.Api.Kernel.Security.Crypto.IXWingKem"/>), so callers pass the raw 32 bytes straight
/// to X-Wing Decaps / <see cref="Harborline.Api.Kernel.Security.Crypto.IXWingKem.DerivePublicKey"/>.
/// </para>
/// </remarks>
public interface IXWingSubkeyDerivation
{
    /// <summary>
    /// Derives the 32-byte X-Wing private-key <b>seed</b> for <paramref name="teamId"/> from
    /// <paramref name="rootSeed"/> using HKDF-Expand-SHA256 with info prefix
    /// <c>"sunfish-xwing-team-v1:"</c>. The returned bytes are the X-Wing decapsulation key — pass them
    /// directly to <see cref="Harborline.Api.Kernel.Security.Crypto.IXWingKem.Decapsulate"/> /
    /// <see cref="Harborline.Api.Kernel.Security.Crypto.IXWingSealedBox.OpenXWing"/>. Node-secret: never leaves the
    /// node. Do NOT clamp or otherwise transform the bytes.
    /// </summary>
    byte[] DeriveXWingPrivateKeySeed(ReadOnlyMemory<byte> rootSeed, string teamId);

    /// <summary>
    /// Derives the 1216-byte X-Wing PUBLIC key (<c>pk_M ‖ pk_X</c>) corresponding to
    /// <see cref="DeriveXWingPrivateKeySeed"/> for the same <paramref name="rootSeed"/> +
    /// <paramref name="teamId"/> pair — the published half a sender encapsulates to. Equivalent to expanding
    /// the derived seed through the X-Wing key-gen (<see cref="Harborline.Api.Kernel.Security.Crypto.IXWingKem.DerivePublicKey"/>).
    /// </summary>
    byte[] DeriveXWingPublicKey(ReadOnlyMemory<byte> rootSeed, string teamId);
}
