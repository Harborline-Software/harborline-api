using System.Text;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterPermissionSignatureTests
{
    private static readonly Guid Tenant = Guid.Parse("29100000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private static (MemberRoster Roster, MemberAdmissionRecord Root, MemberAdmissionRecord Member) Fixture()
    {
        var founder = new Ed25519Signer(KeyPair.Generate());
        var roster = MemberRoster.Genesis(Tenant, "founder", founder, Verifier, Now, Guid.NewGuid())
            .Admit("founder", founder, "member", KeyPair.Generate().PrincipalId,
                PermissionCompositions.Member, Verifier, Now, Guid.NewGuid());
        return (roster, roster.EnumerateAdmissions().Single(a => a.Admission.IsGenesis),
            roster.EnumerateAdmissions().Single(a => !a.Admission.IsGenesis));
    }

    [Fact]
    public void SignedProvenanceSubstitutionIsRefusedAcrossWireAndDurableRoundTrips()
    {
        var (_, root, member) = Fixture();
        var wire = RosterRecordCrdtState.FromAdmission(member) with
        {
            MintingSessionEvidence = "tampered-session",
        };
        var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(member));
        row.MintingSessionEvidence = "tampered-session";

        foreach (var forged in new[] { wire.ToAdmissionOrNull()!,
                     NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull()! })
        {
            Assert.Equal(member.Admission.Signature, forged.Admission.Signature);
            Assert.False(RosterSigning.VerifyAdmission(Tenant, forged.PartyId, forged.PublicKey,
                forged.Admission, Verifier));
            Assert.False(Rebuild(root, forged).Contains("member"));
        }
    }

    [Fact]
    public void PermissionFreeAdmissionRoundTripsAndLocalGrantDoesNotRewriteMembershipEvidence()
    {
        var (roster, root, member) = Fixture();
        var hydrated = NodeRosterRecord.ToCrdtState(NodeRosterRecord.FromCrdtState(
            RosterRecordCrdtState.FromAdmission(member))).ToAdmissionOrNull()!;
        Assert.True(RosterSigning.VerifyAdmission(Tenant, "member", member.PublicKey,
            hydrated.Admission, Verifier));
        Assert.True(Rebuild(root, hydrated).Contains("member"));

        var narrowed = roster.Grant("founder", "member", PermissionSet.Empty);
        var revoked = narrowed.Revoke("founder", "member");
        foreach (var state in new[] { narrowed, revoked })
        {
            var exported = state.EnumerateAdmissions().Single(a => a.PartyId == "member");
            Assert.Equal(member.Admission, exported.Admission);
            Assert.Equal(member.PublicKey, exported.PublicKey);
        }
    }

    [Fact]
    public async Task CanonicalBytesMarkVersionWithoutPermissionSetAndRejectOldShapeSignature()
    {
        var signer = new CapturingSigner(new Ed25519Signer(KeyPair.Generate()));
        var nonce = Guid.NewGuid();
        var admission = RosterSigning.SignAdmission(signer, Tenant, "member", signer.IssuerId,
            "founder", false, Now, nonce);
        Assert.Contains("\"FormatVersion\":3", signer.CanonicalBytes);
        Assert.DoesNotContain("AdmittedPermissions", signer.CanonicalBytes, StringComparison.Ordinal);
        Assert.True(RosterSigning.VerifyAdmission(Tenant, "member", signer.IssuerId, admission, Verifier));
        var record = Assert.IsType<AdmissionRecord>(signer.Payload);
        var same = RosterSigning.SignAdmission(signer, Tenant, "member", signer.IssuerId,
            "founder", false, Now, nonce);
        Assert.Equal(admission.Signature, same.Signature);

        var old = await signer.SignAsync(new
        {
            record.TeamId, record.AdmittedPartyId, record.AdmittedPublicKey, record.AdmittedByPartyId,
            record.AdmittedByPublicKey, record.IsGenesis, record.AdmittedDmPublicKey,
            record.AdmittedXWingPublicKey, record.AdmittedViaTokenId, record.AdmittedUnderSessionEvidence,
            AdmittedPermissions = new[] { "records:read" }, FormatVersion = 2,
        }, Now, nonce);
        Assert.False(RosterSigning.VerifyAdmission(Tenant, "member", signer.IssuerId,
            admission with { Signature = old.Signature.ToBase64Url() }, Verifier));
    }

    private static MemberRoster Rebuild(params MemberAdmissionRecord[] admissions) =>
        MemberRoster.FromSyncedRecords(admissions, [], Verifier);

    private sealed class CapturingSigner(IOperationSigner inner) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;
        public object? Payload { get; private set; }
        public string CanonicalBytes { get; private set; } = "";
        public ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce,
            CancellationToken ct = default)
        {
            Payload = payload;
            CanonicalBytes = Encoding.UTF8.GetString(CanonicalJson.SerializeSignable(payload, IssuerId, issuedAt, nonce));
            return inner.SignAsync(payload, issuedAt, nonce, ct);
        }
    }
}
