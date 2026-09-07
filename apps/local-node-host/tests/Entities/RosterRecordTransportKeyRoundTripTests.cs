using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Wire/durable round-trip coverage for the carried TRANSPORT key on the synced roster record (INFO-2; the
/// ≥3-node mesh follow-on). Proves the serialization fail-closed discipline matches the existing key fields:
/// the transport field round-trips through <see cref="RosterRecordCrdtState"/> + <see cref="NodeRosterRecord"/>;
/// a malformed transport key drops the WHOLE record (null, never an exception); an empty/legacy field is
/// tolerated (null transport key — back-compat).
/// </summary>
public sealed class RosterRecordTransportKeyRoundTripTests
{
    private static readonly Guid Team = Guid.Parse("7e57bbbb-0000-0000-0000-00000000000b");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private static (MemberAdmissionRecord rec, byte[] transportKey) NewAdmissionWithTransport(string party)
    {
        var founderKp = KeyPair.Generate();
        var founderSigner = new Ed25519Signer(founderKp);
        var roster = MemberRoster.Genesis(Team, party, founderSigner, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var rec = roster.EnumerateAdmissions().Single();
        // A distinct 32-byte transport pubkey (a real Ed25519 public key shape).
        var transportKey = KeyPair.Generate().PrincipalId.AsSpan().ToArray();
        return (rec, transportKey);
    }

    [Fact(DisplayName = "transport key round-trips through the synced wire form (FromAdmission → ToAdmissionOrNull)")]
    public void TransportKey_RoundTrips_Through_Wire_Form()
    {
        var (rec, transportKey) = NewAdmissionWithTransport("os:A#founder");

        var wire = RosterRecordCrdtState.FromAdmission(rec, transportKey);
        Assert.False(string.IsNullOrEmpty(wire.TransportPublicKeyB64Url), "the carried key must be on the wire form.");

        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.NotNull(back!.TransportPublicKey);
        Assert.True(back.TransportPublicKey!.AsSpan().SequenceEqual(transportKey),
            "the reconstructed transport key must be byte-identical to the carried one.");
        // The principal binding + signature are unaffected (the transport field is additive/orthogonal).
        Assert.True(back.PublicKey.Equals(rec.PublicKey));
        Assert.Equal(rec.PartyId, back.PartyId);
    }

    [Fact(DisplayName = "a record CarrieD via the MemberAdmissionRecord field (no explicit arg) is used as the default")]
    public void TransportKey_From_Record_Field_Is_Default()
    {
        var (rec, transportKey) = NewAdmissionWithTransport("os:A#founder");
        // Stamp the field on the record itself, then call FromAdmission WITHOUT the explicit transport arg.
        var stamped = rec with { TransportPublicKey = transportKey };

        var wire = RosterRecordCrdtState.FromAdmission(stamped);
        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back?.TransportPublicKey);
        Assert.True(back!.TransportPublicKey!.AsSpan().SequenceEqual(transportKey));
    }

    [Fact(DisplayName = "an EMPTY transport field (legacy record) is tolerated — reconstructs a null transport key")]
    public void Empty_Transport_Field_Is_Tolerated()
    {
        var (rec, _) = NewAdmissionWithTransport("os:A#founder");

        var wire = RosterRecordCrdtState.FromAdmission(rec); // no transport key → empty field.
        Assert.Equal(string.Empty, wire.TransportPublicKeyB64Url);

        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.Null(back!.TransportPublicKey); // legacy/genesis-without-transport → null, no exception.
    }

    [Fact(DisplayName = "a MALFORMED transport field drops the WHOLE record (null, fail-closed — same as the principal key)")]
    public void Malformed_Transport_Field_Drops_Whole_Record()
    {
        var (rec, transportKey) = NewAdmissionWithTransport("os:A#founder");
        var wire = RosterRecordCrdtState.FromAdmission(rec, transportKey)
            with { TransportPublicKeyB64Url = "!!!not-valid-base64url!!!" };

        var back = wire.ToAdmissionOrNull();
        Assert.Null(back); // fail-closed — a malformed transport key drops the record, never an exception.
    }

    [Fact(DisplayName = "a non-32-byte transport key is treated as ABSENT (empty wire field) — fail-closed, never a malformed wire record")]
    public void NonThirtyTwoByte_Transport_Key_Treated_As_Absent()
    {
        var (rec, _) = NewAdmissionWithTransport("os:A#founder");
        var shortKey = new byte[16]; // not a 32-byte Ed25519 public key.

        // FromAdmission only encodes a transport key that is exactly PrincipalId.LengthInBytes; any other length is
        // treated as absent (empty wire field) rather than producing a malformed record a peer would have to drop.
        // The record itself is still well-formed and converges; it simply carries no transport key (back-compat).
        var wire = RosterRecordCrdtState.FromAdmission(rec, shortKey);
        Assert.Equal(string.Empty, wire.TransportPublicKeyB64Url);
        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.Null(back!.TransportPublicKey);
    }

    [Fact(DisplayName = "the durable row preserves the transport key (FromCrdtState → ToCrdtState — cold-start hydration)")]
    public void Durable_Row_Preserves_Transport_Key()
    {
        var (rec, transportKey) = NewAdmissionWithTransport("os:A#founder");
        var wire = RosterRecordCrdtState.FromAdmission(rec, transportKey);

        var row = NodeRosterRecord.FromCrdtState(wire);
        Assert.Equal(wire.TransportPublicKeyB64Url, row.TransportPublicKeyB64Url);

        var back = NodeRosterRecord.ToCrdtState(row);
        Assert.Equal(wire.TransportPublicKeyB64Url, back.TransportPublicKeyB64Url);
        // And it survives the full hydration → admission reconstruction.
        var admission = back.ToAdmissionOrNull();
        Assert.NotNull(admission?.TransportPublicKey);
        Assert.True(admission!.TransportPublicKey!.AsSpan().SequenceEqual(transportKey));
    }

    [Fact(DisplayName = "a legacy durable row (empty transport column) hydrates to an empty wire field (back-compat)")]
    public void Legacy_Durable_Row_Empty_Transport_Hydrates_Empty()
    {
        var (rec, _) = NewAdmissionWithTransport("os:A#founder");
        var wire = RosterRecordCrdtState.FromAdmission(rec); // empty transport field.
        var row = NodeRosterRecord.FromCrdtState(wire);
        Assert.Equal(string.Empty, row.TransportPublicKeyB64Url);

        var back = NodeRosterRecord.ToCrdtState(row);
        Assert.Equal(string.Empty, back.TransportPublicKeyB64Url);
        Assert.Null(back.ToAdmissionOrNull()!.TransportPublicKey);
    }
}
