namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// Supplies the X25519 key material <see cref="RosterEcdhSecretSource"/> needs, keeping the ADR 0136
/// signed-key-binding (F2) at the seam so <c>kernel-security</c> stays free of a roster dependency.
/// </summary>
/// <remarks>
/// <para>
/// This is the ADR 0152 analog of ADR 0136 <c>IParticipantDmKeyResolver</c>. The active member's PRIVATE
/// key MUST be node-secret and NOT derivable from a public party id (it is derived from the install root
/// seed via <see cref="Keys.IX25519SubkeyDerivation"/>). The load-bearing SUBSTITUTION defence lives in
/// <see cref="TryResolvePurposeSignedX25519PublicKey"/>: a peer public key is surfaced ONLY when it is
/// bound into signed admission FOR THE VERIFICATION PURPOSE (ADR 0136
/// <c>RosterSigning.VerifyAdmission</c>, first-write-wins immutable). An unsigned / last-write-wins /
/// wrong-purpose key is the C5-DM / X-Wing key-substitution leak (bug-2882 / bug-1314) and MUST resolve
/// to <c>null</c>.
/// </para>
/// <para>
/// The production implementation lives in the roster package that already references
/// <c>kernel-security</c> (never the reverse), exactly as ADR 0136's C5 roster-bound resolver replaced
/// the C4 fail-closed default. A party-id-derived stand-in resolver is a TEST-ONLY construct — it is a
/// FALSE POSITIVE if it makes a round-trip green (ADR 0152 F3) and is arch-fenced out of production.
/// </para>
/// </remarks>
public interface IVerificationSubkeyResolver
{
    /// <summary>The active member's own party id.</summary>
    string ActiveMemberPartyId { get; }

    /// <summary>
    /// The active member's 32-byte raw X25519 private key (root-seed-derived, node-secret). Empty when
    /// the key path is gated (e.g. device locked) — the source then fails closed.
    /// </summary>
    ReadOnlyMemory<byte> ActiveMemberX25519PrivateKey { get; }

    /// <summary>
    /// Resolves <paramref name="counterpartyPartyId"/>'s 32-byte raw X25519 public key ONLY IF it is bound
    /// into signed admission for <paramref name="purpose"/>. Returns <c>null</c> (fail-closed) when the
    /// key is absent, unsigned-by-association, last-write-wins, or signed for a different purpose — the DM
    /// subkey field does not automatically generalize to another verification purpose (ADR 0152 §Item-(a)).
    /// </summary>
    ReadOnlyMemory<byte>? TryResolvePurposeSignedX25519PublicKey(
        string counterpartyPartyId,
        VerificationLabel purpose);
}
