using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Stands in for the local grant store on the replicated path: no permission set rides the wire (293 s3b2), so
/// the rebuild's admitter, revoker and no-escalation gates read a party's authority from here. A party with no
/// row holds nothing, which is what an ungranted party holds in production.
/// </summary>
internal sealed class TestRosterAuthority(params (string Party, PermissionSet Set)[] rows) : IRosterAuthority
{
    /// <summary>Every (party, instant) this authority was asked for, in call order.</summary>
    internal List<(string Party, DateTimeOffset At)> Reads { get; } = [];

    public PermissionSet PermissionsFor(string teamId, string partyId, DateTimeOffset at)
    {
        Reads.Add((partyId, at));
        foreach (var row in rows) if (string.Equals(row.Party, partyId, StringComparison.Ordinal)) return row.Set;
        return PermissionSet.Empty;
    }
}
