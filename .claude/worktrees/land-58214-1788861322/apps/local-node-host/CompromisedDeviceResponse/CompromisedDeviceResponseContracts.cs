using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

/// <summary>Starts containment and accounting for a roster member whose device is compromised.</summary>
public sealed record CompromisedDeviceResponseRequest(
    string TeamId,
    string RevokedPartyId,
    string RevokedByPartyId);

/// <summary>The signed roster-revocation evidence correlated with a device response.</summary>
public sealed record CompromisedDeviceRevocation(
    string RecordId,
    string TeamId,
    string RevokedPartyId,
    string RevokedByPartyId,
    DateTimeOffset RevokedAt,
    string Signature);

/// <summary>Publishes the signed roster revocation that begins a compromised-device response.</summary>
public interface ICompromisedDeviceRevocationPublisher
{
    /// <summary>Revokes the requested roster member and returns its signed durable evidence.</summary>
    ValueTask<CompromisedDeviceRevocation> RevokeAsync(
        CompromisedDeviceResponseRequest request,
        string correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default);
}

/// <summary>Snapshots document identifiers a team member is entitled to replicate.</summary>
public interface IDeviceEntitlementSnapshotSource
{
    /// <summary>Returns document identifiers only; document content is never returned.</summary>
    IReadOnlyCollection<string> SnapshotDocumentIds(string teamId);
}

/// <summary>Recorded disposition of key material available to the compromised device.</summary>
public sealed record CompromiseKeyResponse(
    string Disposition,
    IReadOnlyList<string> RemainingExposedKeys);

/// <summary>Plans or executes key response after a roster revocation.</summary>
public interface ICompromiseKeyRotation
{
    /// <summary>Returns the exact key disposition to include in the signed response record.</summary>
    ValueTask<CompromiseKeyResponse> RespondAsync(
        CompromisedDeviceRevocation revocation,
        string correlationId,
        CancellationToken cancellationToken = default);
}

/// <summary>One phase recorded inside the response's single correlated audit event.</summary>
public sealed record CompromiseResponseAuditStep(
    string Kind,
    string CorrelationId,
    DateTimeOffset RecordedAt);

/// <summary>The signed response payload covering revocation, entitlement accounting, and key disposition.</summary>
public sealed record CompromisedDeviceResponsePayload(
    string CorrelationId,
    string TeamId,
    string RevokedPartyId,
    string RevokedByPartyId,
    string RevocationRecordId,
    DateTimeOffset RevokedAt,
    IReadOnlyList<string> EntitledDocumentIds,
    CompromiseKeyResponse KeyResponse,
    string OperatorAccount,
    IReadOnlyList<CompromiseResponseAuditStep> AuditSteps);

/// <summary>An append-only signed compromised-device response record.</summary>
public sealed record CompromisedDeviceResponseRecord(
    string CorrelationId,
    CompromisedDeviceResponsePayload Payload,
    string CanonicalPayloadJson,
    string SignerPublicKey,
    DateTimeOffset SignedAt,
    string SigningNonce,
    byte[] Signature);

/// <summary>Appends signed compromised-device response records to durable storage.</summary>
public interface ICompromisedDeviceResponseStore
{
    /// <summary>Appends one immutable signed response record.</summary>
    ValueTask AppendAsync(
        CompromisedDeviceResponseRecord record,
        CancellationToken cancellationToken = default);
}

/// <summary>Result returned to the operator after the response record is durable.</summary>
public sealed record CompromisedDeviceResponseResult(
    string CorrelationId,
    string OperatorAccount,
    IReadOnlyList<string> EntitledDocumentIds,
    string KeyDisposition,
    IReadOnlyList<string> RemainingExposedKeys);

/// <summary>Operator seam for compromised-device containment and accounting.</summary>
public interface ICompromisedDeviceResponseService
{
    /// <summary>Revokes the member, records key disposition, and durably signs the exposure account.</summary>
    ValueTask<CompromisedDeviceResponseResult> RespondAsync(
        CompromisedDeviceResponseRequest request,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}
