using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery.Audit;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.Crypto;

/// <summary>
/// Reference <see cref="IFieldDecryptor"/> per ADR 0046-A4 + A5. Delegates
/// per-tenant DEK derivation to <see cref="ITenantKeyProvider"/>, validates
/// the supplied <see cref="IDecryptCapability"/>, AES-GCM-decrypts, and
/// (when audit dependencies are wired) emits a
/// <see cref="AuditEventType.FieldDecrypted"/> or
/// <see cref="AuditEventType.FieldDecryptionDenied"/> record per call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-overload constructor (A5.7).</b> The audit-disabled overload is
/// for tests + bootstrap; the audit-enabled overload requires both
/// <see cref="IAuditTrail"/> and <see cref="IOperationSigner"/> together
/// — there is no mid-state. The DI factory in
/// <c>AddHarborlineRecoveryCoordinator()</c> throws
/// <see cref="InvalidOperationException"/> at first resolution if exactly
/// one of the two is registered.
/// </para>
/// </remarks>
public sealed class TenantKeyProviderFieldDecryptor : IFieldDecryptor
{
    private const string PurposeLabel = TenantKeyProviderFieldEncryptor.PurposeLabel;
    private const int TagLength = TenantKeyProviderFieldEncryptor.TagLength;

    /// <summary>The only suite this AES-256-GCM decryptor can open. Any other suite fails closed.</summary>
    private const CryptoSuite AcceptedSuite = CryptoSuite.AesGcm256Hkdf_v1;

    private readonly ITenantKeyProvider _tenantKeys;
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly IRecoveryClock _clock;

    /// <summary>Audit-disabled overload (test / bootstrap).</summary>
    public TenantKeyProviderFieldDecryptor(ITenantKeyProvider tenantKeys, IRecoveryClock clock)
    {
        ArgumentNullException.ThrowIfNull(tenantKeys);
        _tenantKeys = tenantKeys;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Audit-enabled overload — both audit trail and signer required.</summary>
    public TenantKeyProviderFieldDecryptor(
        ITenantKeyProvider tenantKeys,
        IAuditTrail auditTrail,
        IOperationSigner signer,
        IRecoveryClock clock)
    {
        ArgumentNullException.ThrowIfNull(tenantKeys);
        ArgumentNullException.ThrowIfNull(auditTrail);
        ArgumentNullException.ThrowIfNull(signer);
        _tenantKeys = tenantKeys;
        _auditTrail = auditTrail;
        _signer = signer;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ReadOnlyMemory<byte>> DecryptAsync(
        EncryptedField field,
        IDecryptCapability capability,
        TenantId tenant,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (field.KeyVersion < 1)
        {
            await EmitDeniedAsync(capability, tenant, "unsupported key version", ct).ConfigureAwait(false);
            throw new FieldDecryptionDeniedException(capability.CapabilityId, "unsupported key version");
        }

        // Fail closed on any suite this AES-256-GCM decryptor does not implement. A legacy
        // untagged field reads as the implicit default (suite #1) and passes here; a
        // forward-version or garbage suite tag is rejected rather than mis-decrypted under
        // the wrong construction. This is the crypto-policy boundary (the converter parses
        // the suite tag faithfully; rejection lives here, mirroring the key-version check).
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

        var dek = _tenantKeys is IVersionedTenantKeyProvider versioned
            ? await versioned.ResolveKeyAsync(tenant, PurposeLabel, field.KeyVersion, ct).ConfigureAwait(false)
            : await _tenantKeys.DeriveKeyAsync(tenant, PurposeLabel, ct).ConfigureAwait(false);

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
            using var aes = new AesGcm(dek.Span, tagSizeInBytes: TagLength);
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
