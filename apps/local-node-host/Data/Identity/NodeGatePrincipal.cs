using Microsoft.AspNetCore.Http;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// The <see cref="ActorId"/> an authorization decision is made ABOUT: the canonical PRINCIPAL of the
/// request, which is the key every grant is issued to and every closure read looks up.
/// </summary>
/// <remarks>
/// <para>
/// A selected-session request carries TWO identities, and they are deliberately different values: the
/// canonical PARTY (<c>SelectedSessionRequestPrincipal.CanonicalParty</c>, a freshly minted party record
/// id) is the attribution stamped on what the act writes, and the canonical PRINCIPAL
/// (<c>PrincipalUserId</c>) is the grant subject — <c>InitialGrantIssuanceService</c> issues to it and
/// <c>NodeEfAuthorizationClosureReader</c> selects <c>WHERE principal_id = …</c> by it. Asking the gate
/// about the party is asking about an actor that holds nothing, which refuses every legitimate web-plane
/// member (ticket 205 slice 4 review 1, F1).
/// </para>
/// <para>
/// <b>Ticket 294 slice 2a — this is now THE party key, not just the grant key.</b> The signed roster's
/// <c>RosterMember.PartyId</c> and the wire's <c>JoiningPartyId</c> carry
/// <c>CanonicalPartyBinding.PrincipalUserId.Value</c> as well, so the roster plane and the grant plane
/// answer about one actor id. The direction was chosen rather than reversed because the grant plane is
/// already unanimous on the principal (~248 <c>SubjectId</c> sites and five EF migrations), and because
/// reversing it would re-open the decided defect this type's own remark names. The People
/// <c>CanonicalPartyReference</c> keeps its existing job — attribution stamped on what an act writes —
/// and is no longer an authorization key.
/// </para>
/// <para>
/// Both places that name a caller to the authorization side resolve it HERE — the shared route guard
/// (<c>RequestAuthorization</c>) and the production PEP (<c>SelectedSessionPermissionResolver</c>) — so
/// the two cannot drift onto different keys again. <c>SharedGatePrincipalResolutionArchTests</c> is the
/// fence.
/// </para>
/// </remarks>
internal static class NodeGatePrincipal
{
    /// <summary>The grant subject of a bound selected-session principal.</summary>
    internal static ActorId Of(SelectedSessionRequestPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new ActorId(principal.PrincipalUserId.Value);
    }

    /// <summary>
    /// The grant subject for THIS request: the bound selected-session principal on the web plane, and the
    /// single-operator principal on the desktop plane. With no principal bound the PLANE decides, and that
    /// rule has exactly one copy — <see cref="NodeCallerParty.Resolve(HttpContext)"/> — which falls back to
    /// the operator (whose party id IS the operator principal id) and refuses a web-plane request that lost
    /// its principal.
    /// </summary>
    internal static ActorId Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var principal = httpContext.Features.Get<SelectedSessionRequestPrincipal>();
        return principal is null
            ? new ActorId(NodeCallerParty.Resolve(httpContext).Value)
            : Of(principal);
    }
}
