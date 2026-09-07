using System.Collections.Generic;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// MTW-2 #3107 — the SERVER-SIDE PIN BINDING for a device-pairing invite. When a web-admitted member
/// takes an authenticated "connect your device" action, the four web-plane membership pins (tenant,
/// canonical PrincipalUserId, PartyId, grant identity) are captured off the member's session-derived
/// request principal (ADR 0160 R3-D — the browser supplies none of them) and bound here to the opaque,
/// single-use <see cref="Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionToken.TokenId"/> the mint
/// issues.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the pins live server-side, not on the wire.</b> The pairing token stays a BEARER credential —
/// only the opaque TokenId crosses the wire to the joining device, exactly as the existing invite mode
/// (<see cref="Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionToken"/>). The pins never leave the
/// tenant's home node: the mint (under the member's web session) and the redemption (at wire enrollment,
/// signed by the in-roster admitter) both occur on that node. Because no pin ciphertext travels the wire,
/// the ADR 0113 amendment's G-3 encrypt-then-sign wire discipline is scoped to the MD-1 DEK-distribution
/// path, not to this bearer TokenId. The bearer weakness is bounded exactly as the base invite: single-use
/// + short-TTL (enforced by the token store) + a redemption that RE-READS live web-plane state and refuses
/// on any drift.
/// </para>
/// <para>
/// <b>Provenance is the whole point (the delta over a caller-supplied snapshot).</b> The bound
/// <see cref="Membership"/> is not accepted from an arbitrary caller; it is derived from the authenticated
/// session principal at mint time. The bridge front door consumes THIS binding rather than a caller-passed
/// snapshot, so a roster admission is only ever signed for pins a real, authenticated web session bound.
/// </para>
/// </remarks>
/// <param name="TokenId">The opaque single-use invite token id the mint issued (the redemption key).</param>
/// <param name="Membership">The four web-plane pins captured off the session-derived principal at mint.</param>
/// <param name="BoundPartyId">The People PartyId the token was minted for (the session principal's
/// canonical party). The enrollment must present exactly this party or the redemption refuses.</param>
/// <param name="Anchor">The token's immutable team scope; the receipt independently supplies it at commit.</param>
/// <param name="SessionCorrelationId">#3167 R1.2 — the opaque mint-time SessionCorrelationId captured off the
/// member's authenticated web session at mint. Threaded INTO the signed admission at redemption as the
/// minting-session evidence, so the admission's audit provenance reads "admitted by token T minted under session
/// S" (an AUDIT provenance claim, not a verification claim — amendment R1.2). Empty (default) only for legacy /
/// test bindings that predate provenance capture.</param>
internal sealed record WebPairingInviteBinding(
    string TokenId,
    TenantMembershipSnapshot Membership,
    string BoundPartyId,
    Harborline.Api.Foundation.IdentityAtlas.Enrollment.TeamTrustAnchor Anchor,
    string SessionCorrelationId = "");

/// <summary>
/// The keyed lookup store for <see cref="WebPairingInviteBinding"/>s. A mint BINDS the pins to a fresh
/// token id; the bridge front door LOOKS THEM UP at redemption. Single-use enforcement does NOT live here
/// — it stays with the <see cref="Harborline.Api.Foundation.IdentityAtlas.Enrollment.IAdmissionTokenStore"/>
/// (the token store atomically redeems the same TokenId exactly once). This store is a pure keyed lookup:
/// binding it twice for the same token would be a mint-side bug, and a lookup after the token is redeemed
/// still returns the pins (the redemption's single-use gate is the token store, not this).
/// </summary>
internal interface IWebPairingInviteBindingStore
{
    /// <summary>Record the pin binding for a freshly-minted pairing token so it can be looked up at redemption.</summary>
    void Bind(WebPairingInviteBinding binding);

    /// <summary>Return the pin binding for <paramref name="tokenId"/>, or null if no pairing was minted for it.</summary>
    WebPairingInviteBinding? Lookup(string tokenId);
}

/// <summary>
/// Process-local <see cref="IWebPairingInviteBindingStore"/> (v1). Mirrors the in-memory/durable split of
/// the admission token store: this is the mechanism impl the mint + bridge are unit-tested against; the
/// durable, SQLCipher-at-rest binding row rides with the live enrollment-path wiring follow-on (which is
/// where the pins first need to survive a process restart — the same follow-on that wires the bridge into
/// <c>WireEnrollmentAdmitter</c> and adds the authenticated "connect your device" route).
/// </summary>
internal sealed class InMemoryWebPairingInviteBindingStore : IWebPairingInviteBindingStore
{
    private readonly Dictionary<string, WebPairingInviteBinding> _bindings = new(System.StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Bind(WebPairingInviteBinding binding)
    {
        System.ArgumentNullException.ThrowIfNull(binding);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(binding.TokenId);
        lock (_gate)
        {
            _bindings[binding.TokenId] = binding;
        }
    }

    public WebPairingInviteBinding? Lookup(string tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            return null;
        }
        lock (_gate)
        {
            return _bindings.TryGetValue(tokenId, out var binding) ? binding : null;
        }
    }
}
