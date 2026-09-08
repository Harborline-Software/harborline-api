using System;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The install's OWN team-scoped TRANSPORT public key for its genesis team — HKDF(root, genesisTeamId) public half,
/// the SAME key the per-team registrar's own-subkey floor (<c>DefaultTeamServiceRegistrar</c>) presents on the
/// trusted sync HELLO. Registered as a singleton at bootstrap (Program.cs, where the root identity + genesis team
/// id are in scope) so <see cref="RosterSyncBootstrapHostedService"/> can STAMP it onto the genesis self-admission
/// it publishes to the synced roster doctype (INFO-2; the ≥3-node mesh fix).
/// </summary>
/// <remarks>
/// Carrying the founder's own transport key on the synced genesis record lets a converging member harvest it from
/// the converged roster and trust the founder's wire HELLO without a separate enrollment response — the
/// roster-derived-trust property applied to the chain root. It is a PUBLIC key (leaks nothing) and rides
/// UNSIGNED-by-association, honored only because the genesis party is the (self-signed) chain root the
/// <c>FromSyncedRecords</c> injection guard already validated. A minimal DI test that does not register one falls
/// back to an empty transport field on the genesis record (back-compat — the own-subkey floor still covers self).
/// </remarks>
/// <param name="PublicKey">The raw 32-byte Ed25519 team-scoped transport public key for the genesis team.</param>
public sealed record NodeOwnTransportKey(byte[] PublicKey)
{
    /// <summary>The raw 32-byte Ed25519 team-scoped transport public key for the genesis team (defensive copy).</summary>
    public byte[] PublicKey { get; } =
        (byte[])(PublicKey ?? throw new ArgumentNullException(nameof(PublicKey))).Clone();
}
