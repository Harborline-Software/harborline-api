using Harborline.Api.Foundation.Crypto;
namespace Harborline.Api.Foundation.IdentityAtlas;
/// <summary>The single roster record/wire format shared by admissions, revocations and receive attestations.</summary>
public static class RosterWireFormat
{
    public const int CurrentVersion = 2;
}
public sealed record RosterReceiveAttestation(int FormatVersion, string NodePartyId, string NodePublicKey,
    DateTimeOffset ReceivedAt, string Signature);
public sealed record RosterReceiveAttestationRecord(int FormatVersion, string NodePartyId, string NodePublicKey,
    string RecordId, string ReceivedAtIso);
/// <summary>Creates and verifies canonical Ed25519 receive attestations.</summary>
public static class RosterReceiveAttestationSigning
{
    public static RosterReceiveAttestation Sign(IOperationSigner signer, string nodePartyId, string recordId,
        DateTimeOffset receivedAt, Guid recordNonce)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodePartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(receivedAt.ToUnixTimeMilliseconds());
        var key = signer.IssuerId.ToBase64Url();
        var payload = new RosterReceiveAttestationRecord(RosterWireFormat.CurrentVersion, nodePartyId, key,
            recordId, at.ToString("O"));
        var signed = signer.SignAsync(payload, at, recordNonce).AsTask().GetAwaiter().GetResult();
        return new(RosterWireFormat.CurrentVersion, nodePartyId, key, at, signed.Signature.ToBase64Url());
    }

    public static bool Verify(string recordId, Guid recordNonce, RosterReceiveAttestation attestation,
        IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(verifier);
        if (attestation.FormatVersion != RosterWireFormat.CurrentVersion ||
            string.IsNullOrWhiteSpace(attestation.NodePartyId) || string.IsNullOrWhiteSpace(attestation.NodePublicKey)
            || string.IsNullOrWhiteSpace(attestation.Signature)) return false;
        try
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(attestation.ReceivedAt.ToUnixTimeMilliseconds());
            var payload = new RosterReceiveAttestationRecord(attestation.FormatVersion, attestation.NodePartyId,
                attestation.NodePublicKey, recordId, at.ToString("O"));
            return verifier.Verify(new SignedOperation<RosterReceiveAttestationRecord>(payload,
                PrincipalId.FromBase64Url(attestation.NodePublicKey), at, recordNonce,
                Signature.FromBase64Url(attestation.Signature)));
        }
        catch (FormatException) { return false; }
    }
}
