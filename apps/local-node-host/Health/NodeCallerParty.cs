using Microsoft.AspNetCore.Http;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Raised when a request on the hosted-WEB plane reaches caller attribution with no
/// <see cref="SelectedSessionRequestPrincipal"/> bound (card #3385). A web-plane member is acting —
/// the listener said so by opening the attribution scope — but the acting identity is not on the
/// request, so there is no honest party to stamp.
/// </summary>
/// <remarks>
/// <b>Why this is a refusal and not the operator fallback.</b> The fallback is correct by design on
/// the DESKTOP plane: bootstrap, hosted services and detached workflow effects legitimately have no
/// principal. On the web plane its absence is a BUG, and falling back stamps the operator on a
/// member's mutation indistinguishably from a legitimate desktop call — a wrong durable record that
/// no test, no log and no verifier would ever surface. This type is what converts that silence into
/// a stop.
/// <para>
/// It deliberately carries NO identity in its message. The operator party is precisely the value that
/// must not travel forward from here, and an exception message is a log surface.
/// </para>
/// </remarks>
internal sealed class NodeCallerAttributionRefusedException()
    : InvalidOperationException(
        "A hosted-web request reached caller attribution with no selected-session principal bound. " +
        "Refusing to attribute this mutation rather than falling back to the single-operator " +
        "identity, which would record the wrong acting party silently.");

/// <summary>
/// Resolves the People <see cref="PartyId"/> stamped as the acting party on a node-local durable
/// mutation. The real actor is the live selected-session principal carried on the request
/// (<see cref="SelectedSessionRequestPrincipal"/> in <c>HttpContext.Features</c>); when no principal
/// feature is bound — the single-operator bootstrap/desktop path — it falls back to the operator
/// party so the shipped local path keeps working (ruled fallback option (a)).
/// </summary>
/// <remarks>
/// It deliberately reads ONLY the request's selected-session feature; it does NOT consult
/// <c>IPartyContext</c>/<c>ICurrentUser</c> or any ambient/AsyncLocal context holder (the resolver change is
/// out of scope — 2611 follow-up territory). The single sourced <see cref="OperatorParty"/> value
/// equals the former per-route <c>LocalActor</c> constant, so the fallback attribution is unchanged.
/// </remarks>
internal static class NodeCallerParty
{
    /// <summary>
    /// The single-operator fallback identity (bootstrap/desktop path). Equals
    /// <see cref="ActiveTeamAuthorizationContext.LocalUserId"/> — the value the former per-route
    /// <c>LocalActor</c> constant carried.
    /// </summary>
    internal static PartyId OperatorParty { get; } = new(ActiveTeamAuthorizationContext.LocalUserId);

    /// <summary>
    /// The acting party for <paramref name="httpContext"/>: the request's selected-session canonical
    /// Party when a principal is bound; <see cref="OperatorParty"/> on the desktop plane; and a
    /// <see cref="NodeCallerAttributionRefusedException"/> when the request is on the WEB plane but
    /// carries no principal — the one combination that is a bug rather than a supported path.
    /// </summary>
    /// <exception cref="NodeCallerAttributionRefusedException">
    /// A web-plane request reached attribution with no principal bound (card #3385).
    /// </exception>
    internal static PartyId Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var canonical = httpContext.Features.Get<SelectedSessionRequestPrincipal>()
            ?.CanonicalParty.Value;
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            return new PartyId(canonical);
        }

        // Absence of a principal is NORMAL on the desktop plane and a BUG on the web plane, so the
        // plane is what decides between the fallback and a refusal. Read the plane from the SAME
        // signal the authorization fence uses (card #3356) rather than a second one — two context holders
        // for "is a web member acting" could disagree, and one of them could go silently inert.
        if (NodeCallerAttributionScope.HasBoundWebPrincipal)
        {
            throw new NodeCallerAttributionRefusedException();
        }

        return OperatorParty;
    }

    /// <summary>
    /// The acting party from the immutable attribution projection carried across an in-request workflow
    /// effect, or <see cref="OperatorParty"/> when the execution is genuinely detached.
    /// </summary>
    internal static PartyId Resolve(NodeCallerAttribution? attribution)
    {
        var canonical = attribution?.MemberPartyId;
        return string.IsNullOrWhiteSpace(canonical) ? OperatorParty : new PartyId(canonical);
    }
}
