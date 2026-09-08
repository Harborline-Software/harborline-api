using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Resolves the KIND of the current authenticated principal — human vs agent/service — server-side, from the
/// same validated token that carries the caller's identity (the confused-deputy guard <see cref="IPartyContext"/>
/// realizes for the party <c>Guid</c>). The adjacent seam ADR 0143 D-INV-5 / D-INV-8 names as owed with the
/// broker-PEP build: the .NET analog of the harborline-sdk's <c>isHumanPrincipal</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a distinct seam, not a property on <see cref="IPartyContext"/>.</b> <see cref="IPartyContext"/>'s
/// contract is deliberately parameterless and single-purpose (resolve the party <c>Guid</c>) so a consumer
/// cannot combine a UserId from one principal with a tenant from another. The human/agent bit is a SEPARATE
/// server-derived fact; keeping it on its own accessor preserves that contract while giving the workflow
/// confirm route the <c>IsHuman</c> value the broker's confirmer identity requires — without ever
/// reading it from a request body.
/// </para>
/// <para>
/// <b>Server-derived, never body-supplied (ADR 0143 R1-E).</b> Like <see cref="IPartyContext"/> this accessor
/// takes no parameters — a mutating endpoint has no seam through which a request body could assert
/// <c>isHuman: true</c> to launder an agent confirmation past separation-of-duties. A CP confirm route resolves
/// the confirmer's kind ONCE here and hands it to the broker; it is never trusted from the client.
/// </para>
/// </remarks>
public interface IPrincipalKindResolver
{
    /// <summary>
    /// True iff the CURRENT authenticated principal is a human (an interactive operator), false for an
    /// agent/service/host principal. Resolved from the ambient authenticated context, never from a request
    /// body. A CP confirmation requires <see langword="true"/> (D-INV-5: an agent may propose, only a human
    /// confirms).
    /// </summary>
    ValueTask<bool> IsCurrentPrincipalHumanAsync(CancellationToken ct = default);
}
