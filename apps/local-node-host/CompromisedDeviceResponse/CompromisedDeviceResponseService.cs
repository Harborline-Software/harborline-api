using System.Text.Json;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

/// <summary>Coordinates roster revocation, key disposition, and signed exposure accounting.</summary>
public sealed class CompromisedDeviceResponseService : ICompromisedDeviceResponseService
{
    private const string OperatorAccount =
        "The device retains every document in the entitlement snapshot and the operation history already replicated to it. "
        + "Deleted content remains in its operation log until ticket 039 compaction lands. Revocation blocks future trust "
        + "but removes no data or key material from the device. Re-keying protects future data only; it does not undo "
        + "disclosure or make copies already obtained unreadable. Key rotation is deferred, so every key family listed "
        + "in this account remains exposed.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ICompromisedDeviceRevocationPublisher _revocations;
    private readonly IDeviceEntitlementSnapshotSource _entitlements;
    private readonly ICompromiseKeyRotation _keyRotation;
    private readonly ICompromisedDeviceResponseStore _store;
    private readonly IOperationSigner _signer;
    private readonly TimeProvider _time;
    private readonly AuthorizationGate _gate;

    /// <summary>Creates the response coordinator from its public revocation, inventory, rotation, storage, and signing seams.</summary>
    public CompromisedDeviceResponseService(
        ICompromisedDeviceRevocationPublisher revocations,
        IDeviceEntitlementSnapshotSource entitlements,
        ICompromiseKeyRotation keyRotation,
        ICompromisedDeviceResponseStore store,
        IOperationSigner signer,
        TimeProvider time,
        AuthorizationGate gate)
    {
        _revocations = revocations ?? throw new ArgumentNullException(nameof(revocations));
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
        _keyRotation = keyRotation ?? throw new ArgumentNullException(nameof(keyRotation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async ValueTask<CompromisedDeviceResponseResult> RespondAsync(
        CompromisedDeviceResponseRequest request,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RevokedPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RevokedByPartyId);

        if (!Guid.TryParse(request.TeamId, out var teamId)
            || !string.Equals(teamId.ToString("D"), authority.Tenant.Value, StringComparison.Ordinal))
            throw new ArgumentException("The compromised-device response team does not match the write authority.", nameof(request));
        var decision = await _gate.DecideAsync(authority.Request(
            AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
            "members",
            request.RevokedPartyId), cancellationToken).ConfigureAwait(false);
        decision.RequireAllowed();

        var correlationNonce = Guid.NewGuid();
        var correlationId = correlationNonce.ToString("D");
        var revokedAt = decision.DecidedAt;
        var entitledDocumentIds = _entitlements.SnapshotDocumentIds(request.TeamId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        var revocation = await _revocations
            .RevokeAsync(request, correlationId, decision, cancellationToken)
            .ConfigureAwait(false);
        var keyResponse = await _keyRotation
            .RespondAsync(revocation, correlationId, cancellationToken)
            .ConfigureAwait(false);
        var auditSteps = new[]
        {
            new CompromiseResponseAuditStep("revocation", correlationId, revokedAt),
            new CompromiseResponseAuditStep("key-response", correlationId, revokedAt),
            new CompromiseResponseAuditStep("exposure-accounting", correlationId, revokedAt),
        };

        var payload = new CompromisedDeviceResponsePayload(
            correlationId,
            request.TeamId,
            request.RevokedPartyId,
            request.RevokedByPartyId,
            revocation.RecordId,
            revokedAt,
            entitledDocumentIds,
            keyResponse,
            OperatorAccount,
            auditSteps);
        var canonicalPayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var signed = await _signer
            .SignAsync(canonicalPayloadJson, revokedAt, correlationNonce, cancellationToken)
            .ConfigureAwait(false);

        await _store.AppendAsync(new CompromisedDeviceResponseRecord(
            correlationId,
            payload,
            canonicalPayloadJson,
            signed.IssuerId.ToBase64Url(),
            signed.IssuedAt,
            signed.Nonce.ToString("D"),
            signed.Signature.AsSpan().ToArray()), cancellationToken).ConfigureAwait(false);

        return new CompromisedDeviceResponseResult(
            correlationId,
            OperatorAccount,
            entitledDocumentIds,
            keyResponse.Disposition,
            keyResponse.RemainingExposedKeys);
    }
}
