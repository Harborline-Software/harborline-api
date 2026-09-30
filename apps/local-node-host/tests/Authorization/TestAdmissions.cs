using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// ck-10: the signed admission a fixture's conferral carries into the pipeline's authorize stage. The roster is
/// real: <paramref name="admitter"/> founds it and signs <paramref name="admitted"/>'s admission (or, when they
/// are the same party, the genesis self-admission is the admission).
/// </summary>
internal static class TestAdmissions
{
    internal static AdmissionConferralAuthority SignedBy(string admitter, string admitted) =>
        AdmissionConferralAuthority.SignedAdmission(Roster(admitter, admitted));

    internal static MemberRoster Roster(string admitter, string admitted)
    {
        var verifier = new Ed25519Verifier();
        var signer = new Ed25519Signer(KeyPair.Generate());
        var roster = MemberRoster.Genesis(Guid.NewGuid(), admitter, signer, verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
        return admitter == admitted
            ? roster
            : roster.Admit(admitter, signer, admitted, KeyPair.Generate().PrincipalId, PermissionCompositions.Member,
                verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
    }
}
