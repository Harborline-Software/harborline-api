using System;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery.Audit;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.Crypto;

/// <summary>
/// Reference <see cref="ISubjectFieldDecryptor"/>. Mirrors
/// <see cref="TenantKeyProviderFieldDecryptor"/> but resolves the per-subject
/// sub-key via <see cref="ITenantKeyProvider.DeriveSubjectKeyAsync"/>. A
/// crypto-shredded subject's sub-key is gone, so derivation throws
/// <see cref="SubjectErasedException"/>, which this decryptor converts into a
/// fail-closed <see cref="FieldDecryptionDeniedException"/> (reason
/// <c>"subject erased"</c>) + a <see cref="AuditEventType.FieldDecryptionDenied"/>
/// record. Plaintext is never returned for an erased subject.
/// </summary>
/// <remarks>
/// Two-overload constructor mirrors A5.7: the audit-enabled overload requires
/// BOTH <see cref="IAuditTrail"/> and <see cref="IOperationSigner"/> together;
/// the audit-disabled overload takes neither.
/// </remarks>
public sealed class SubjectKeyFieldDecryptor : ISubjectFieldDecryptor
{
    private const string PurposeLabel = SubjectKeyFieldEncryptor.PurposeLabel;
    private const int TagLength = TenantKeyProviderFieldEncryptor.TagLength;
    private const string SubjectErasedReason = "subject erased";

    /// <summary>The only suite this AES-256-GCM decryptor can open. Any other suite fails closed.</summary>
    private const CryptoSuite AcceptedSuite = CryptoSuite.AesGcm256Hkdf_v1;

    private readonly ITenantKeyProvider _tenantKeys;
    private readonly ISubjectErasureRegistry _erasure;
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly IRecoveryClock _clock;

    /// <summary>Audit-disabled overload (test / bootstrap).</summary>
    public SubjectKeyFieldDecryptor(
        ITenantKeyProvider tenantKeys,
        ISubjectErasureRegistry erasure,
        IRecoveryClock clock)
    {
        _tenantKeys = tenantKeys ?? throw new ArgumentNullException(nameof(tenantKeys));
        _erasure = erasure ?? throw new ArgumentNullException(nameof(erasure));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Audit-enabled overload — both audit trail and signer required.</summary>
    public SubjectKeyFieldDecryptor(
        ITenantKeyProvider tenantKeys,
        ISubjectErasureRegistry erasure,
        IAuditTrail auditTrail,
        IOperationSigner signer,
        IRecoveryClock clock)
    {
        _tenantKeys = tenantKeys ?? throw new ArgumentNullException(nameof(tenantKeys));
        _erasure = erasure ?? throw new ArgumentNullException(nameof(erasure));
        _auditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> DecryptForSubjectAsync(
        EncryptedField field,
        IDecryptCapability capability,
        TenantId tenant,
        SubjectId subject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (field.KeyVersion < 1)
        {
            await EmitDeniedAsync(capability, tenant, "unsupported key version", ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, "unsupported key version");
        }

        // Fail closed on any suite this AES-256-GCM decryptor does not implement (legacy
        // untagged fields read as the implicit default suite #1 and pass). See the tenant
        // decryptor for the full rationale — this is the crypto-policy boundary.
        if (field.Suite != AcceptedSuite)
        {
            await EmitDeniedAsync(capability, tenant, "unsupported crypto suite", ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, "unsupported crypto suite");
        }

        var now = _clock.UtcNow();
        var rejection = capability.ValidateForDecrypt(tenant, now);
        if (rejection is not null)
        {
            await EmitDeniedAsync(capability, tenant, rejection, ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, rejection);
        }

        // Resolve the per-subject sub-key. A shredded subject fails closed here:
        // the key is gone, so the ciphertext is permanently undecryptable.
        ReadOnlyMemory<byte> subKey;
        try
        {
            subKey = _tenantKeys is IVersionedTenantKeyProvider versioned
                ? await versioned.ResolveSubjectKeyAsync(
                    tenant,
                    subject,
                    PurposeLabel,
                    field.KeyVersion,
                    _erasure,
                    ct).ConfigureAwait(false)
                : await _tenantKeys
                    .DeriveSubjectKeyAsync(tenant, subject, PurposeLabel, _erasure, ct)
                    .ConfigureAwait(false);
        }
        catch (SubjectErasedException)
        {
            await EmitDeniedAsync(capability, tenant, SubjectErasedReason, ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, SubjectErasedReason);
        }

        var packed = field.Ciphertext.Span;
        if (packed.Length < TagLength)
        {
            await EmitDeniedAsync(capability, tenant, "ciphertext too short", ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, "ciphertext too short");
        }

        var ciphertextLen = packed.Length - TagLength;
        var ciphertext = packed[..ciphertextLen];
        var tag = packed[ciphertextLen..];
        var plaintext = new byte[ciphertextLen];
        try
        {
            using var aes = new AesGcm(subKey.Span, tagSizeInBytes: TagLength);
            aes.Decrypt(field.Nonce.Span, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            await EmitDeniedAsync(capability, tenant, "AES-GCM tag verification failed", ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, "AES-GCM tag verification failed");
        }

        await EmitAuditAsync(
            AuditEventType.FieldDecrypted,
            FieldEncryptionAuditPayloadFactory.Decrypted(capability, tenant, field.KeyVersion),
            tenant,
            ct).ConfigureAwait(false);
        return plaintext;
    }

    private Task EmitDeniedAsync(IDecryptCapability capability, TenantId tenant, string reason, CancellationToken ct) =>
        EmitAuditAsync(
            AuditEventType.FieldDecryptionDenied,
            FieldEncryptionAuditPayloadFactory.DecryptionDenied(capability, tenant, reason),
            tenant,
            ct);

    private async Task EmitAuditAsync(AuditEventType eventType, AuditPayload payload, TenantId tenant, CancellationToken ct)
    {
        if (_auditTrail is null || _signer is null)
        {
            return;
        }

        var occurredAt = _clock.UtcNow();
        var signed = await _signer.SignAsync(payload, occurredAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: tenant,
            EventType: eventType,
            OccurredAt: occurredAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail.AppendAsync(record, ct).ConfigureAwait(false);
    }
}
