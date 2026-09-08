using System.Security.Cryptography;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Wire/durable round-trip coverage for the carried <b>X-Wing</b> (X25519 + ML-KEM-768) public key on the synced
/// roster record (PQC Phase 2 / BL-01 increment 2c-iii-b — the suite-#3 write-side enabler, ADR 0004 Amendment 2).
/// Proves the serialization fail-closed discipline matches the existing key fields: the 1216-byte X-Wing field
/// round-trips through <see cref="RosterRecordCrdtState"/> + <see cref="NodeRosterRecord"/>; a malformed /
/// wrong-length X-Wing key drops the WHOLE record (null, never an exception); an empty/legacy field is tolerated
/// (null X-Wing key — back-compat → the party is simply not X-Wing-capable).
/// </summary>
public sealed class RosterRecordXWingKeyRoundTripTests
{
    private static readonly Guid Team = Guid.Parse("7e57cccc-0000-0000-0000-00000000000c");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private static MemberAdmissionRecord NewAdmission(string party)
    {
        var founderKp = KeyPair.Generate();
        var founderSigner = new Ed25519Signer(founderKp);
        var roster = MemberRoster.Genesis(Team, party, founderSigner, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        return roster.EnumerateAdmissions().Single();
    }

    // A distinct, well-formed 1216-byte value (the X-Wing public-key length). The round-trip only validates LENGTH
    // + base64url; it never interprets the bytes, so random bytes of the right length are a faithful stand-in.
    private static byte[] FakeXWingKey() => RandomNumberGenerator.GetBytes(RosterRecordCrdtState.XWingPublicKeyLength);

    [Fact(DisplayName = "X-Wing key round-trips through the synced wire form (FromAdmission → ToAdmissionOrNull)")]
    public void XWingKey_RoundTrips_Through_Wire_Form()
    {
        var rec = NewAdmission("os:A#founder");
        var xwingKey = FakeXWingKey();

        var wire = RosterRecordCrdtState.FromAdmission(rec, xwingKey: xwingKey);
        Assert.False(string.IsNullOrEmpty(wire.XWingPublicKeyB64Url), "the carried X-Wing key must be on the wire form.");

        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.NotNull(back!.XWingPublicKey);
        Assert.True(back.XWingPublicKey!.AsSpan().SequenceEqual(xwingKey),
            "the reconstructed X-Wing key must be byte-identical to the carried one.");
        // This record's genesis signed NO X-Wing key, so FromAdmission uses the BACK-COMPAT carried-byte[] fallback
        // (the signed-value path is exercised by the foundation RosterSyncRecordsTests C5-X round-trip). The principal
        // binding + signature are unaffected (the X-Wing field is additive/orthogonal).
        Assert.True(back.PublicKey.Equals(rec.PublicKey));
        Assert.Equal(rec.PartyId, back.PartyId);
    }

    [Fact(DisplayName = "an X-Wing key carried via the MemberAdmissionRecord field (no explicit arg) is the default")]
    public void XWingKey_From_Record_Field_Is_Default()
    {
        var rec = NewAdmission("os:A#founder");
        var xwingKey = FakeXWingKey();
        var stamped = rec with { XWingPublicKey = xwingKey };

        var wire = RosterRecordCrdtState.FromAdmission(stamped); // no explicit xwingKey arg → uses the record field.
        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back?.XWingPublicKey);
        Assert.True(back!.XWingPublicKey!.AsSpan().SequenceEqual(xwingKey));
    }

    [Fact(DisplayName = "an EMPTY X-Wing field (legacy record) is tolerated — reconstructs a null X-Wing key")]
    public void Empty_XWing_Field_Is_Tolerated()
    {
        var rec = NewAdmission("os:A#founder");

        var wire = RosterRecordCrdtState.FromAdmission(rec); // no X-Wing key → empty field.
        Assert.Equal(string.Empty, wire.XWingPublicKeyB64Url);

        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.Null(back!.XWingPublicKey); // legacy/not-yet-capable → null, no exception.
    }

    [Fact(DisplayName = "a MALFORMED X-Wing field (non-base64url) drops the WHOLE record (null, fail-closed)")]
    public void Malformed_XWing_Field_Drops_Whole_Record()
    {
        var rec = NewAdmission("os:A#founder");
        var wire = RosterRecordCrdtState.FromAdmission(rec, xwingKey: FakeXWingKey())
            with { XWingPublicKeyB64Url = "!!!not-valid-base64url!!!" };

        var back = wire.ToAdmissionOrNull();
        Assert.Null(back); // fail-closed — never an exception out of the rebuild.
    }

    [Fact(DisplayName = "a WRONG-LENGTH X-Wing field (valid base64url, not 1216 bytes) drops the WHOLE record")]
    public void WrongLength_XWing_Field_Drops_Whole_Record()
    {
        var rec = NewAdmission("os:A#founder");
        // A perfectly valid base64url of 64 bytes — but not the 1216-byte X-Wing length. Must drop the record,
        // never silently accept a short/forged key.
        var shortB64Url = System.Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var wire = RosterRecordCrdtState.FromAdmission(rec, xwingKey: FakeXWingKey())
            with { XWingPublicKeyB64Url = shortB64Url };

        var back = wire.ToAdmissionOrNull();
        Assert.Null(back); // wrong length → fail-closed.
    }

    [Fact(DisplayName = "a non-1216-byte carried X-Wing key is treated as ABSENT (empty wire field), never malformed")]
    public void NonXWingLength_Key_Treated_As_Absent()
    {
        var rec = NewAdmission("os:A#founder");
        var shortKey = new byte[32]; // not a 1216-byte X-Wing public key.

        // FromAdmission only encodes a key that is exactly XWingPublicKeyLength; any other length is treated as
        // absent (empty wire field) rather than producing a malformed record a peer would have to drop. The record
        // itself stays well-formed + converges; it simply carries no X-Wing key (back-compat).
        var wire = RosterRecordCrdtState.FromAdmission(rec, xwingKey: shortKey);
        Assert.Equal(string.Empty, wire.XWingPublicKeyB64Url);
        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.Null(back!.XWingPublicKey);
    }

    [Fact(DisplayName = "the durable row preserves the X-Wing key (FromCrdtState → ToCrdtState — cold-start hydration)")]
    public void Durable_Row_Preserves_XWing_Key()
    {
        var rec = NewAdmission("os:A#founder");
        var xwingKey = FakeXWingKey();
        var wire = RosterRecordCrdtState.FromAdmission(rec, xwingKey: xwingKey);

        var row = NodeRosterRecord.FromCrdtState(wire);
        Assert.Equal(wire.XWingPublicKeyB64Url, row.XWingPublicKeyB64Url);

        var back = NodeRosterRecord.ToCrdtState(row);
        Assert.Equal(wire.XWingPublicKeyB64Url, back.XWingPublicKeyB64Url);
        // And it survives the full hydration → admission reconstruction.
        var admission = back.ToAdmissionOrNull();
        Assert.NotNull(admission?.XWingPublicKey);
        Assert.True(admission!.XWingPublicKey!.AsSpan().SequenceEqual(xwingKey));
    }

    [Fact(DisplayName = "a legacy durable row (empty X-Wing column) hydrates to an empty wire field (back-compat)")]
    public void Legacy_Durable_Row_Empty_XWing_Hydrates_Empty()
    {
        var rec = NewAdmission("os:A#founder");
        var wire = RosterRecordCrdtState.FromAdmission(rec); // empty X-Wing field.
        var row = NodeRosterRecord.FromCrdtState(wire);
        Assert.Equal(string.Empty, row.XWingPublicKeyB64Url);

        var back = NodeRosterRecord.ToCrdtState(row);
        Assert.Equal(string.Empty, back.XWingPublicKeyB64Url);
        Assert.Null(back.ToAdmissionOrNull()!.XWingPublicKey);
    }

    [Fact(DisplayName = "the X-Wing field is ORTHOGONAL to the transport + DM fields (all three round-trip together)")]
    public void XWing_Transport_Dm_Fields_Are_Orthogonal()
    {
        var rec = NewAdmission("os:A#founder");
        var transportKey = KeyPair.Generate().PrincipalId.AsSpan().ToArray();
        var dmKey = KeyPair.Generate().PrincipalId.AsSpan().ToArray();
        var xwingKey = FakeXWingKey();

        var wire = RosterRecordCrdtState.FromAdmission(rec, transportKey, dmKey, xwingKey);
        var back = wire.ToAdmissionOrNull();

        Assert.NotNull(back);
        Assert.True(back!.TransportPublicKey!.AsSpan().SequenceEqual(transportKey));
        Assert.True(back.XWingPublicKey!.AsSpan().SequenceEqual(xwingKey));
        // The DM + X-Wing keys are harvested from the SIGNED admission (C5 / C5-X), so on the wire they are present iff
        // the admission signed them; this genesis signed neither, so both ride via the BACK-COMPAT carried-byte[]
        // fallback here. The transport key is the one genuinely unsigned-by-association field. The point of this test
        // is that adding the X-Wing field perturbs neither the transport nor the principal binding.
        Assert.True(back.PublicKey.Equals(rec.PublicKey));
    }
}
