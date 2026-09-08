using System.Linq;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MTW-2 #3167 (R1.2) — wire/durable round-trip + forge-proof coverage for the SIGNED admitter-provenance fields
/// (pairing token id + minting-session evidence) bound into the admission envelope. Unlike the
/// unsigned-by-association transport key, these are part of the SIGNED payload, so they MUST survive the
/// <see cref="RosterRecordCrdtState"/> sync round-trip AND the <see cref="NodeRosterRecord"/> durable cold-start
/// hydration intact — else a pairing admission's signature would fail verify on a peer / after a restart and the
/// record would be dropped. Proves: a provenance-bearing admission round-trips and still validates to genesis; a
/// provenance-free (non-pairing) admission is byte-stable (still validates); and a writer who STRIPS or ALTERS the
/// carried provenance drops the record on rebuild (the forge-proof property that makes "an admission whose
/// signature does not bind the token identity is invalid on this path" hold across the sync + durable channels).
/// </summary>
public sealed class RosterRecordAdmissionProvenanceRoundTripTests
{
    private static readonly System.Guid Team = System.Guid.Parse("7e57dddd-0000-0000-0000-00000000000d");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly System.DateTimeOffset Now =
        System.DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);
    private const string TokenId = "pairing-token-abc123";
    private const string SessionEvidence = "session-corr-xyz789";

    private sealed record Party(string PartyId, KeyPair Key, IOperationSigner Signer);

    private static Party NewParty(string partyId)
    {
        var kp = KeyPair.Generate();
        return new Party(partyId, kp, new Ed25519Signer(kp));
    }

    /// <summary>
    /// Genesis + one admission of <paramref name="joiner"/> signed WITH the provenance fields (the pairing-path
    /// shape). Returns the founder + the two-record admission log (genesis + joiner).
    /// </summary>
    private static (MemberRoster Roster, MemberAdmissionRecord Genesis, MemberAdmissionRecord Joiner)
        AdmitWithProvenance(Party founder, Party joiner, string tokenId, string sessionEvidence)
    {
        var roster = MemberRoster.Genesis(Team, founder.PartyId, founder.Signer, Verifier, Now, System.Guid.NewGuid());
        var admitted = roster.Admit(
            admitterPartyId: founder.PartyId,
            admitterSigner: founder.Signer,
            newPartyId: joiner.PartyId,
            newPublicKey: joiner.Key.PrincipalId,
            grantedPermissions: PermissionCompositions.Member,
            verifier: Verifier,
            issuedAt: Now,
            nonce: System.Guid.NewGuid(),
            admittedViaTokenId: tokenId,
            admittedUnderSessionEvidence: sessionEvidence);
        var admissions = admitted.EnumerateAdmissions();
        var genesis = admissions.Single(a => a.Admission.IsGenesis);
        var joinerRec = admissions.Single(a => a.PartyId == joiner.PartyId);
        return (admitted, genesis, joinerRec);
    }

    [Fact(DisplayName = "R1.2: a provenance-bearing admission round-trips through the CRDT wire form and still validates")]
    public void Provenance_RoundTrips_Through_Crdt_Wire_Form_And_Validates()
    {
        var founder = NewParty("os:A#founder");
        var joiner = NewParty("os:B#joiner");
        var (_, genesis, joinerRec) = AdmitWithProvenance(founder, joiner, TokenId, SessionEvidence);

        // The joiner admission projects to the wire form carrying the SIGNED provenance…
        var wire = RosterRecordCrdtState.FromAdmission(joinerRec);
        Assert.Equal(TokenId, wire.AdmittedViaTokenId);
        Assert.Equal(SessionEvidence, wire.MintingSessionEvidence);

        // …and reconstructs an AdmissionSignature that carries it back verbatim.
        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        Assert.Equal(TokenId, back!.Admission.AdmittedViaTokenId);
        Assert.Equal(SessionEvidence, back.Admission.MintingSessionEvidence);

        // The rebuilt roster [genesis, round-tripped-joiner] validates to genesis — the signature COVERS the
        // provenance, so it only verifies because the round-trip preserved it exactly.
        var rebuilt = MemberRoster.FromSyncedRecords(
            new[] { genesis, back }, System.Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.True(rebuilt.ValidatesToGenesis(Verifier));
        Assert.True(rebuilt.Contains(joiner.PartyId));
    }

    [Fact(DisplayName = "R1.2: a provenance-bearing admission survives durable-row hydration (cold start)")]
    public void Provenance_Survives_Durable_Row_Hydration()
    {
        var founder = NewParty("os:A#founder");
        var joiner = NewParty("os:B#joiner");
        var (_, genesis, joinerRec) = AdmitWithProvenance(founder, joiner, TokenId, SessionEvidence);

        var wire = RosterRecordCrdtState.FromAdmission(joinerRec);
        var row = NodeRosterRecord.FromCrdtState(wire);
        Assert.Equal(TokenId, row.AdmittedViaTokenId);
        Assert.Equal(SessionEvidence, row.MintingSessionEvidence);

        var hydrated = NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull();
        Assert.NotNull(hydrated);
        Assert.Equal(TokenId, hydrated!.Admission.AdmittedViaTokenId);
        Assert.Equal(SessionEvidence, hydrated.Admission.MintingSessionEvidence);

        // Signature still verifies after the full durable → CRDT → admission hydration.
        var rebuilt = MemberRoster.FromSyncedRecords(
            new[] { genesis, hydrated }, System.Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.True(rebuilt.ValidatesToGenesis(Verifier));
        Assert.True(rebuilt.Contains(joiner.PartyId));
    }

    [Fact(DisplayName = "R1.2 byte-stability: a provenance-FREE (non-pairing) admission round-trips and still validates")]
    public void Provenance_Free_Admission_Is_Byte_Stable()
    {
        var founder = NewParty("os:A#founder");
        var joiner = NewParty("os:B#joiner");
        // Empty provenance = the proximity / plain-invite shape (the additive fields default empty).
        var (_, genesis, joinerRec) = AdmitWithProvenance(founder, joiner, tokenId: "", sessionEvidence: "");

        var wire = RosterRecordCrdtState.FromAdmission(joinerRec);
        Assert.Equal(string.Empty, wire.AdmittedViaTokenId);
        Assert.Equal(string.Empty, wire.MintingSessionEvidence);

        var back = wire.ToAdmissionOrNull();
        Assert.NotNull(back);
        var rebuilt = MemberRoster.FromSyncedRecords(
            new[] { genesis, back! }, System.Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.True(rebuilt.ValidatesToGenesis(Verifier));
        Assert.True(rebuilt.Contains(joiner.PartyId));
    }

    [Fact(DisplayName = "R1.2 forge-proof: ALTERING the carried token id drops the record on rebuild")]
    public void Altered_Token_Id_Drops_The_Record_On_Rebuild()
    {
        var founder = NewParty("os:A#founder");
        var joiner = NewParty("os:B#joiner");
        var (_, genesis, joinerRec) = AdmitWithProvenance(founder, joiner, TokenId, SessionEvidence);

        // A roster writer swaps the carried token id away from what the admitter signed.
        var tampered = RosterRecordCrdtState.FromAdmission(joinerRec) with { AdmittedViaTokenId = "attacker-token" };
        var tamperedAdmission = tampered.ToAdmissionOrNull();
        Assert.NotNull(tamperedAdmission); // reconstruction itself is fail-open on well-formed fields…

        // …but the signature no longer covers the altered token id, so the joiner is DROPPED on rebuild.
        var rebuilt = MemberRoster.FromSyncedRecords(
            new[] { genesis, tamperedAdmission! }, System.Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.False(rebuilt.Contains(joiner.PartyId));
    }

    [Fact(DisplayName = "R1.2 forge-proof: STRIPPING the signed session evidence drops the record on rebuild")]
    public void Stripped_Session_Evidence_Drops_The_Record_On_Rebuild()
    {
        var founder = NewParty("os:A#founder");
        var joiner = NewParty("os:B#joiner");
        var (_, genesis, joinerRec) = AdmitWithProvenance(founder, joiner, TokenId, SessionEvidence);

        // A writer strips the signed evidence back to empty — the signature was over the non-empty value.
        var stripped = RosterRecordCrdtState.FromAdmission(joinerRec) with { MintingSessionEvidence = string.Empty };
        var strippedAdmission = stripped.ToAdmissionOrNull();
        Assert.NotNull(strippedAdmission);

        var rebuilt = MemberRoster.FromSyncedRecords(
            new[] { genesis, strippedAdmission! }, System.Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.False(rebuilt.Contains(joiner.PartyId));
    }
}
