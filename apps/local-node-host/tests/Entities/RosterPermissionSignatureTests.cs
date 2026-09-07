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

    private static (MemberRoster Roster, MemberAdmissionRecord Root, MemberAdmissionRecord Member)
        Fixture(PermissionSet permissions)
    {
        var founder = new Ed25519Signer(KeyPair.Generate());
        var roster = MemberRoster.Genesis(Tenant, "founder", founder, Verifier, Now, Guid.NewGuid())
            .Admit("founder", founder, "member", KeyPair.Generate().PrincipalId,
                permissions, Verifier, Now, Guid.NewGuid());
        return (roster, roster.EnumerateAdmissions().Single(a => a.Admission.IsGenesis),
            roster.EnumerateAdmissions().Single(a => !a.Admission.IsGenesis));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PermissionOnlySubstitutionIsRefusedAndReplayOrderCannotChooseIt(bool elevate)
    {
        var (_, root, member) = Fixture(PermissionCompositions.Member);
        var replacement = elevate ? PermissionCompositions.Owner : PermissionSet.Empty;
        var carriedOnly = member with { Permissions = replacement };
        var wireOnly = RosterRecordCrdtState.FromAdmission(member) with
        {
            Permissions = replacement.Permissions.ToArray(),
        };
        var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(member));
        row.PermissionsJson = System.Text.Json.JsonSerializer.Serialize(replacement.Permissions);
        foreach (var forged in new[] { carriedOnly, wireOnly.ToAdmissionOrNull()!,
                     NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull()! })
        {
            Assert.Equal(member.Admission.Signature, forged.Admission.Signature);
            Assert.False(Rebuild(root, forged).Contains("member"));
            foreach (var ordered in new[] { new[] { root, forged, member }, new[] { root, member, forged } })
            {
                var rebuilt = MemberRoster.FromSyncedRecords(ordered, [], Verifier);
                Assert.Equal(member.Permissions, rebuilt.PermissionsOf("member"));
                Assert.True(rebuilt.ValidatesToGenesis(Verifier));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenuineSetIncludingEmptyRoundTripsAndCannotBeRewritten(bool empty)
    {
        var permissions = empty ? PermissionSet.Empty : PermissionCompositions.Member;
        var (_, root, member) = Fixture(permissions);
        var wire = RosterRecordCrdtState.FromAdmission(member);
        // A set is order-insensitive and deduplicated, including on hydration.
        wire = wire with { Permissions = wire.Permissions.Reverse().Concat(wire.Permissions).ToArray() };
        var hydrated = NodeRosterRecord.ToCrdtState(NodeRosterRecord.FromCrdtState(wire)).ToAdmissionOrNull()!;
        var signatureJson = System.Text.Json.JsonSerializer.Serialize(hydrated.Admission);
        var signature = System.Text.Json.JsonSerializer.Deserialize<AdmissionSignature>(signatureJson)!;
        Assert.True(RosterSigning.VerifyAdmission(Tenant, "member", member.PublicKey, signature, Verifier));
        Assert.True(Rebuild(root, hydrated).Contains("member"));
        Assert.Equal(permissions, Rebuild(root, hydrated).PermissionsOf("member"));
        var rewritten = wire with { Permissions = PermissionCompositions.Owner.Permissions.ToArray() };
        Assert.False(Rebuild(root, rewritten.ToAdmissionOrNull()!).Contains("member"));
    }

    [Fact]
    public void GenesisRequiresMatchingSignedPermissionsIncludingRefusalOfSignedEmpty()
    {
        var (_, root, _) = Fixture(PermissionSet.Empty);
        Assert.Equal(PermissionCompositions.Owner, Rebuild(root).PermissionsOf("founder"));
        Assert.Empty(Rebuild(root with { Permissions = PermissionSet.Empty }).Members);
        var wire = RosterRecordCrdtState.FromAdmission(root) with { Permissions = [] };
        Assert.Empty(Rebuild(wire.ToAdmissionOrNull()!).Members);
        var founder = new Ed25519Signer(KeyPair.Generate());
        var emptyRoot = new MemberAdmissionRecord(Tenant.ToString("D"), "empty-root", founder.IssuerId,
            PermissionSet.Empty, RosterSigning.SignAdmission(founder, Tenant, "empty-root", founder.IssuerId,
                "empty-root", true, Now, Guid.NewGuid(), admittedPermissions: PermissionSet.Empty));
        Assert.True(RosterSigning.VerifyAdmission(Tenant, "empty-root", founder.IssuerId, emptyRoot.Admission, Verifier));
        Assert.Empty(Rebuild(emptyRoot).Members);
    }

    [Fact]
    public void YesterdayOwnerRebuildsWithoutAcquiringTheNewAtom()
    {
        Assert.True(PermissionCompositions.Owner.Contains(Permission.MembersRevoke));
        var yesterday = PermissionCompositions.Owner.Without(Permission.MembersRevoke);
        var root = SignedGenesis(yesterday);
        var rebuilt = Rebuild(root);
        Assert.True(rebuilt.Contains("founder"));
        Assert.Equal(yesterday, rebuilt.PermissionsOf("founder"));
        Assert.False(rebuilt.PermissionsOf("founder")!.Contains(Permission.MembersRevoke));
        Assert.True(rebuilt.ValidatesToGenesis(Verifier));
    }

    [Theory]
    [InlineData(Permission.GrantPermissions)]
    [InlineData(Permission.OrgTransferOwnership)]
    [InlineData(Permission.MembersAdmit)]
    public void SignedFloorIsSufficientButEveryFloorAtomIsRequired(string missingAtom)
    {
        var floor = PermissionSet.Of(Permission.GrantPermissions,
            Permission.OrgTransferOwnership, Permission.MembersAdmit);
        Assert.Equal(floor, Rebuild(SignedGenesis(floor)).PermissionsOf("founder"));
        var narrowed = SignedGenesis(floor.Without(missingAtom));
        Assert.True(RosterSigning.VerifyAdmission(Tenant, "founder", narrowed.PublicKey,
            narrowed.Admission, Verifier));
        Assert.Empty(Rebuild(narrowed).Members);
    }

    private static MemberAdmissionRecord SignedGenesis(PermissionSet permissions)
    {
        var founder = new Ed25519Signer(KeyPair.Generate());
        return new MemberAdmissionRecord(Tenant.ToString("D"), "founder", founder.IssuerId,
            permissions, RosterSigning.SignAdmission(founder, Tenant, "founder", founder.IssuerId,
                "founder", true, Now, Guid.NewGuid(), admittedPermissions: permissions));
    }

    [Fact]
    public void ExportKeepsSignedSetAfterLiveGrantAndRevocation()
    {
        var (roster, _, member) = Fixture(PermissionCompositions.Admin);
        var narrowed = roster.Grant("founder", "member", PermissionSet.Empty);
        var revoked = narrowed.Revoke("founder", "member");
        foreach (var state in new[] { narrowed, revoked })
        {
            var exported = state.EnumerateAdmissions().Single(a => a.PartyId == "member");
            Assert.Equal(member.Permissions, exported.Permissions);
            Assert.Equal(member.Admission, exported.Admission);
            Assert.True(Rebuild(state.EnumerateAdmissions().ToArray()).ValidatesToGenesis(Verifier));
        }
    }

    [Fact]
    public async Task CanonicalBytesMarkVersionAndBindSortedSetAndRejectPreVersionedSignature()
    {
        var signer = new CapturingSigner(new Ed25519Signer(KeyPair.Generate()));
        var nonce = Guid.NewGuid();
        var permissionSet = PermissionSet.Of("z:read", "a:read", "z:read");
        var admission = RosterSigning.SignAdmission(signer, Tenant, "member", signer.IssuerId,
            "founder", false, Now, nonce, admittedPermissions: permissionSet);
        Assert.Contains("\"FormatVersion\":2", signer.CanonicalBytes);
        Assert.Contains("\"AdmittedPermissions\":[\"a:read\",\"z:read\"]", signer.CanonicalBytes);
        Assert.True(RosterSigning.VerifyAdmission(Tenant, "member", signer.IssuerId, admission, Verifier));
        var record = Assert.IsType<AdmissionRecord>(signer.Payload);
        var sameSet = RosterSigning.SignAdmission(signer, Tenant, "member", signer.IssuerId,
            "founder", false, Now, nonce, admittedPermissions: PermissionSet.Of("a:read", "z:read"));
        Assert.Equal(admission.Signature, sameSet.Signature);
        RosterSigning.SignAdmission(signer, Tenant, "member", signer.IssuerId,
            "founder", false, Now, nonce, admittedPermissions: PermissionSet.Empty);
        Assert.Contains("\"AdmittedPermissions\":[]", signer.CanonicalBytes);
        var wrongVersion = await signer.SignAsync(record with { FormatVersion = 1 }, Now, nonce);
        Assert.False(RosterSigning.VerifyAdmission(Tenant, "member", signer.IssuerId,
            admission with { Signature = wrongVersion.Signature.ToBase64Url() }, Verifier));
        // Exact old payload shape: neither permissions nor a format marker was signed.
        var old = await signer.SignAsync(new
        {
            record.TeamId, record.AdmittedPartyId, record.AdmittedPublicKey, record.AdmittedByPartyId,
            record.AdmittedByPublicKey, record.IsGenesis, record.AdmittedDmPublicKey,
            record.AdmittedXWingPublicKey, record.AdmittedViaTokenId, record.AdmittedUnderSessionEvidence,
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
