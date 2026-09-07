using System;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>The party role whose own ephemeral X25519 key an engagement attestation binds.</summary>
public enum EngagementPartyRole
{
    /// <summary>The customer that initiates and signs the grant request.</summary>
    Customer = 0,

    /// <summary>The external support vendor named by the grant request.</summary>
    Vendor = 1,
}

/// <summary>
/// A party's statement binding its own ephemeral X25519 public key to one engagement window.
/// The statement is carried only inside a <see cref="SignedOperation{T}"/> signed by that party's
/// Ed25519 identity. Neither party signs the counterparty's key.
/// </summary>
/// <param name="EngagementId">
/// The engagement binding. Until the named support-engagement record ships, Card D uses the signed
/// grant id in canonical <c>D</c> format as this value.
/// </param>
/// <param name="Role">The signing party's role in the engagement.</param>
/// <param name="EphemeralX25519PublicKey">The signing party's 32-byte ephemeral X25519 public key.</param>
/// <param name="NotBefore">Inclusive start of the attestation's validity window.</param>
/// <param name="NotAfter">Exclusive end of the attestation's validity window.</param>
public sealed record EngagementKeyAttestation(
    string EngagementId,
    EngagementPartyRole Role,
    byte[] EphemeralX25519PublicKey,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

/// <summary>
/// The Card-D signed grant packet: the customer-signed grant request plus one self-attestation from
/// each party. The outer record adds no unsigned authority; verification trusts only the three inner
/// <see cref="SignedOperation{T}"/> envelopes after <see cref="EngagementGrantKeyPacketVerifier"/>
/// validates their complete binding chain.
/// </summary>
public sealed record EngagementGrantKeyPacket(
    SignedOperation<GrantIssuanceRequest> Grant,
    SignedOperation<EngagementKeyAttestation> CustomerAttestation,
    SignedOperation<EngagementKeyAttestation> VendorAttestation);

/// <summary>Outcome of signed engagement-key packet verification.</summary>
public enum EngagementKeyPacketStatus
{
    /// <summary>Every signature, identity, role, engagement, key-shape, and lifetime check passed.</summary>
    Verified = 0,

    /// <summary>The packet is legacy or outside its active window, so no verification key is available.</summary>
    Unavailable = 1,

    /// <summary>The packet was malformed, tampered, mis-bound, or signed by the wrong identity.</summary>
    Invalid = 2,
}

/// <summary>
/// The two authenticated ephemeral public keys released only after the complete packet verifies.
/// Arrays are defensive copies so callers cannot mutate the signed packet through the result.
/// </summary>
public sealed record VerifiedEngagementKeyPair(
    byte[] CustomerEphemeralX25519PublicKey,
    byte[] VendorEphemeralX25519PublicKey);

/// <summary>A fail-closed verification result for <see cref="EngagementGrantKeyPacket"/>.</summary>
public sealed record EngagementKeyPacketVerificationResult(
    EngagementKeyPacketStatus Status,
    VerifiedEngagementKeyPair? Keys,
    string Reason)
{
    /// <summary>True only when authenticated key material is present.</summary>
    public bool IsVerified => Status == EngagementKeyPacketStatus.Verified && Keys is not null;
}

/// <summary>
/// Verifies the Card-D attestation chain before releasing either X25519 public key.
/// </summary>
/// <remarks>
/// Verification order is load-bearing: first verify the customer-signed grant and its customer roster
/// anchor; then resolve the vendor identity from the signed request; then verify each party's self-signed
/// attestation against the identity for its role; only then release the X25519 keys. A caller must never
/// read key bytes directly from the packet and bypass this boundary.
/// <para>
/// The <c>expectedCustomerIdentity</c> argument is a trust anchor, not packet data. The consumer must
/// resolve it from the customer's authenticated roster or an equivalently trusted session binding; it
/// must never come from the packet, request payload, or another attacker-controlled input.
/// </para>
/// <para>
/// For this verifier, the grant subject and <see cref="GrantIssuanceRequest.VendorIdentityPublicKey"/>
/// are two encodings of the same vendor identity. Equality is enforced here so authorization cannot name
/// one vendor while the engagement-key attestation authenticates another. General access grants retain
/// their existing <c>ActorId</c> shape; only grants consumed as engagement-key packets require the
/// canonical base64url Ed25519 subject form.
/// </para>
/// <para>
/// The vendor identity is trust-on-first-use at grant issuance. It is protected by the human
/// fingerprint-confirm ceremony and by the customer's signature after issuance, but it is not yet
/// cryptographically vouched for by the unbuilt verified-party directory. That directory can replace the
/// anchor later without changing the attestation shape.
/// </para>
/// </remarks>
public static class EngagementGrantKeyPacketVerifier
{
    private const int X25519PublicKeyLength = 32;

    /// <summary>
    /// Validates <paramref name="packet"/> at an explicit instant. No wall clock is read.
    /// </summary>
    /// <param name="packet">The customer grant and both signed self-attestations.</param>
    /// <param name="expectedCustomerIdentity">
    /// The roster-resolved customer Ed25519 identity. This is a trusted consumer-supplied anchor and must
    /// never be copied from <paramref name="packet"/> or any attacker-controlled request field.
    /// </param>
    /// <param name="at">The explicit verification instant.</param>
    /// <param name="verifier">The operation-signature verifier.</param>
    public static EngagementKeyPacketVerificationResult Verify(
        EngagementGrantKeyPacket packet,
        PrincipalId expectedCustomerIdentity,
        DateTimeOffset at,
        IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(verifier);

        if (packet.Grant is null
            || packet.CustomerAttestation is null
            || packet.VendorAttestation is null)
        {
            return Invalid("packet-envelope-missing");
        }

        var request = packet.Grant.Payload;
        if (request is null)
        {
            if (!verifier.Verify(packet.Grant))
            {
                return Invalid("grant-signature-invalid");
            }

            return Invalid("grant-payload-missing");
        }

        // The signed records expose arrays for serialization compatibility. Snapshot the key before
        // verification, authenticate an envelope containing that exact snapshot, and never reread the
        // caller-owned array. Otherwise a concurrent mutation can change the identity after its signature
        // has already been checked.
        var vendorIdentityPublicKey = request.VendorIdentityPublicKey is { } liveVendorIdentityPublicKey
            ? liveVendorIdentityPublicKey.AsSpan().ToArray()
            : null;
        var grantSnapshot = packet.Grant with
        {
            Payload = request with { VendorIdentityPublicKey = vendorIdentityPublicKey },
        };

        if (!verifier.Verify(grantSnapshot))
        {
            return Invalid("grant-signature-invalid");
        }

        if (packet.Grant.IssuerId != expectedCustomerIdentity)
        {
            return Invalid("grant-customer-identity-mismatch");
        }

        if (request.ValidUntil is { } malformedUntil && malformedUntil <= request.ValidFrom)
        {
            return Invalid("grant-window-malformed");
        }

        if (at < request.ValidFrom || request.ValidUntil is { } until && at >= until)
        {
            return Unavailable("grant-inactive");
        }

        if (vendorIdentityPublicKey is null)
        {
            return Unavailable("legacy-grant-has-no-vendor-identity-anchor");
        }

        if (vendorIdentityPublicKey.Length != PrincipalId.LengthInBytes)
        {
            return Invalid("vendor-identity-key-malformed");
        }

        var vendorIdentity = PrincipalId.FromBytes(vendorIdentityPublicKey);
        if (!string.Equals(request.PrincipalId, vendorIdentity.ToBase64Url(), StringComparison.Ordinal))
        {
            return Invalid("grant-subject-vendor-identity-mismatch");
        }

        var expectedEngagementId = request.GrantId.ToString("D");

        var customerVerification = VerifyAttestation(
            packet.CustomerAttestation,
            expectedCustomerIdentity,
            EngagementPartyRole.Customer,
            expectedEngagementId,
            at,
            verifier,
            "customer");
        if (customerVerification.Failure is not null)
        {
            return customerVerification.Failure;
        }

        var vendorVerification = VerifyAttestation(
            packet.VendorAttestation,
            vendorIdentity,
            EngagementPartyRole.Vendor,
            expectedEngagementId,
            at,
            verifier,
            "vendor");
        if (vendorVerification.Failure is not null)
        {
            return vendorVerification.Failure;
        }

        return new EngagementKeyPacketVerificationResult(
            EngagementKeyPacketStatus.Verified,
            new VerifiedEngagementKeyPair(
                customerVerification.VerifiedKey!,
                vendorVerification.VerifiedKey!),
            "verified");
    }

    private static (EngagementKeyPacketVerificationResult? Failure, byte[]? VerifiedKey) VerifyAttestation(
        SignedOperation<EngagementKeyAttestation> attestation,
        PrincipalId expectedIdentity,
        EngagementPartyRole expectedRole,
        string expectedEngagementId,
        DateTimeOffset at,
        IOperationVerifier verifier,
        string party)
    {
        if (attestation is null)
        {
            return (Invalid($"{party}-attestation-missing"), null);
        }

        if (attestation.IssuerId != expectedIdentity)
        {
            return (Invalid($"{party}-identity-mismatch"), null);
        }

        var payload = attestation.Payload;
        if (payload is null)
        {
            return !verifier.Verify(attestation)
                ? (Invalid($"{party}-attestation-signature-invalid"), null)
                : (Invalid($"{party}-attestation-payload-missing"), null);
        }

        var ephemeralPublicKey = payload.EphemeralX25519PublicKey is { } liveEphemeralPublicKey
            ? liveEphemeralPublicKey.AsSpan().ToArray()
            : null;
        var attestationSnapshot = attestation with
        {
            Payload = payload with { EphemeralX25519PublicKey = ephemeralPublicKey! },
        };

        if (!verifier.Verify(attestationSnapshot))
        {
            return (Invalid($"{party}-attestation-signature-invalid"), null);
        }

        if (payload.Role != expectedRole)
        {
            return (Invalid($"{party}-role-mismatch"), null);
        }

        if (!string.Equals(payload.EngagementId, expectedEngagementId, StringComparison.Ordinal))
        {
            return (Invalid($"{party}-engagement-mismatch"), null);
        }

        if (ephemeralPublicKey is null
            || ephemeralPublicKey.Length != X25519PublicKeyLength)
        {
            return (Invalid($"{party}-x25519-key-malformed"), null);
        }

        if (payload.NotBefore >= payload.NotAfter)
        {
            return (Invalid($"{party}-attestation-window-malformed"), null);
        }

        if (at < payload.NotBefore || at >= payload.NotAfter)
        {
            return (Unavailable($"{party}-attestation-inactive"), null);
        }

        return (null, ephemeralPublicKey);
    }

    private static EngagementKeyPacketVerificationResult Invalid(string reason)
        => new(EngagementKeyPacketStatus.Invalid, null, reason);

    private static EngagementKeyPacketVerificationResult Unavailable(string reason)
        => new(EngagementKeyPacketStatus.Unavailable, null, reason);
}
