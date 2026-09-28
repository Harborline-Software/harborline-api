using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Enrollment;

using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// The desktop plane's actor (ticket 294 slice 3b): the roster party bound to this node's signing key. On the
/// founding node that is the founder's canonical tenant principal (slice 2a keys the roster by it), so the desktop
/// plane, the web plane and the grant store name the operator by one value. There is no compile-time operator
/// constant: a node whose key holds no roster edge has no desktop actor, and every desktop act is refused.
/// </summary>
public sealed class NodeOperatorIdentity(NodeTeamRoster? roster = null, IOperationSigner? signer = null)
{
    /// <summary>The value the desktop actor carried before slice 3b. Read only by the one-time rekey.</summary>
    internal const string RetiredDesktopActor = "local";

    /// <summary>This node's operator, or null when the composition has no roster or the key holds no edge.</summary>
    public ActorId? Principal =>
        roster is not null && signer is not null && PartyOf(roster.Current, signer) is { } party
            ? new ActorId(party)
            : (ActorId?)null;

    /// <summary>The operator composed in <paramref name="services"/>, or null.</summary>
    internal static ActorId? From(IServiceProvider? services) =>
        services?.GetService<NodeOperatorIdentity>()?.Principal;

    /// <summary>The roster party bound to <paramref name="signer"/>'s key, live or ejected; null when none.</summary>
    /// <remarks>Admissions retain revoked keys, so an ejected node party still resolves and reads as ejected.</remarks>
    internal static string? PartyOf(MemberRoster roster, IOperationSigner signer) =>
        roster.Members.FirstOrDefault(member => member.PublicKey.Equals(signer.IssuerId))?.PartyId
        ?? roster.EnumerateAdmissions().FirstOrDefault(member => member.PublicKey.Equals(signer.IssuerId))?.PartyId;
}
