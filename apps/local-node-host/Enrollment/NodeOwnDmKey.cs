using System;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// C5 — the install's OWN team-scoped DM-encryption PUBLIC key for its genesis team — the X25519 public half of
/// HKDF(root, genesisTeamId) over the DM domain (<see cref="NodeDmKeyDerivation"/>). The DM counterpart of
/// <see cref="NodeOwnTransportKey"/>. Registered as a singleton at bootstrap (Program.cs, where the root secret +
/// genesis team id are in scope) so <see cref="RosterSyncBootstrapHostedService"/> can STAMP it onto the genesis
/// self-admission it publishes to the synced roster doctype — every converging member then harvests the founder's
/// DM public key from the converged roster (roster-derived DM keys; the #1310 pattern extended to the DM half),
/// so a 1:1 DM with the founder "just works" with no key-exchange handshake.
/// </summary>
/// <remarks>
/// It is a PUBLIC key (leaks nothing) and rides UNSIGNED-by-association, honored only because the genesis party is
/// the (self-signed) chain root the <c>FromSyncedRecords</c> injection guard already validated. The PRIVATE half is
/// never registered/exposed — it is re-derived node-secret in <c>RosterDmKeyResolver</c> from the same root secret.
/// A minimal DI test that does not register one falls back to an empty DM field on the genesis record (back-compat —
/// the team simply cannot seal DMs with the founder until its record carries the key).
/// </remarks>
/// <param name="PublicKey">The raw 32-byte X25519 team-scoped DM public key for the genesis team.</param>
public sealed record NodeOwnDmKey(byte[] PublicKey)
{
    /// <summary>The raw 32-byte X25519 team-scoped DM public key for the genesis team (defensive copy).</summary>
    public byte[] PublicKey { get; } =
        (byte[])(PublicKey ?? throw new ArgumentNullException(nameof(PublicKey))).Clone();
}
