using System.Collections.Generic;

namespace Harborline.Api.Kernel.Sync.Handshake;

/// <summary>
/// Supplies the live set of trusted TRANSPORT-layer member public keys for the multi-user
/// <see cref="MemberSetTrustPolicy"/> (enrollment Phase B production wiring). The per-team registrar resolves
/// THIS (optionally) from the install-level outer provider — the same dependency-direction trick the
/// container-bridge uses (resolve a kernel-sync INTERFACE from the outer provider so kernel-runtime does NOT
/// depend on local-node-host).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why TRANSPORT keys, not author keys.</b> The trust gate answers a wire-handshake question: "is this peer's
/// HELLO public key one of my enrolled members' SYNC keys?" A peer presents its team-scoped transport subkey in
/// HELLO (HKDF(root, teamId) — <c>TeamScopedNodeIdentity</c>), NOT its comms-author principal key. So this
/// provider returns the team-scoped TRANSPORT keys of the roster's members. (Forge-proof comms ATTRIBUTION is a
/// separate gate keyed on the AUTHOR principal key — the roster's <c>PublicKeyOf</c> — wired into the comms
/// projection, not here.) Keeping the two gates on their respective key types is the whole correctness point.
/// </para>
/// <para>
/// <b>Live snapshot.</b> <see cref="TrustedTransportKeys"/> is read on every handshake, so an admit/revoke takes
/// effect on the next round without re-wiring the daemon (subject to the eventual-convergence offline window — a
/// node honors a just-revoked member until it syncs the revocation). When NO provider is registered (the empty
/// outer provider — DI composition tests, or a Bridge multi-tenant child), the registrar falls back to its own
/// team subkey alone, so a single-user node still trusts its own sibling devices and never bricks.
/// </para>
/// </remarks>
public interface ITrustedMemberKeyProvider
{
    /// <summary>
    /// The current set of trusted member TRANSPORT public keys (raw 32-byte Ed25519). Read per handshake. May be
    /// empty (no admitted peers yet); the registrar always unions in its own team subkey so the gate is never
    /// genuinely empty for a live node.
    /// </summary>
    IReadOnlyList<byte[]> TrustedTransportKeys();
}
