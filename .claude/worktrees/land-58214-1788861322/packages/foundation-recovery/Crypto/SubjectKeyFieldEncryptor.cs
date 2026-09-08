using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;

namespace Harborline.Api.Foundation.Recovery.Crypto;

/// <summary>
/// Reference <see cref="ISubjectFieldEncryptor"/>. Derives a per-subject sub-key
/// (purpose label <c>"encrypted-field-aes"</c>) under the per-tenant DEK via
/// <see cref="ITenantKeyProvider.DeriveSubjectKeyAsync"/> and AES-GCM-encrypts
/// with a fresh 12-byte random nonce + 16-byte tag. Byte-for-byte the same
/// envelope shape as <see cref="TenantKeyProviderFieldEncryptor"/>; only the key
/// scope differs (subject vs. tenant).
/// </summary>
public sealed class SubjectKeyFieldEncryptor : ISubjectFieldEncryptor
{
    // Reuse the tenant-DEK encryptor's constants so the envelope contract stays identical.
    internal const string PurposeLabel = TenantKeyProviderFieldEncryptor.PurposeLabel;
    private const int CurrentKeyVersion = TenantKeyProviderFieldEncryptor.CurrentKeyVersion;
    private const int NonceLength = TenantKeyProviderFieldEncryptor.NonceLength;
    private const int TagLength = TenantKeyProviderFieldEncryptor.TagLength;
    private const CryptoSuite CurrentSuite = TenantKeyProviderFieldEncryptor.CurrentSuite;

    private readonly ITenantKeyProvider _tenantKeys;
    private readonly ISubjectErasureRegistry _erasure;

    public SubjectKeyFieldEncryptor(ITenantKeyProvider tenantKeys, ISubjectErasureRegistry erasure)
    {
        _tenantKeys = tenantKeys ?? throw new ArgumentNullException(nameof(tenantKeys));
        _erasure = erasure ?? throw new ArgumentNullException(nameof(erasure));
    }

    /// <inheritdoc />
    public async Task<EncryptedField> EncryptForSubjectAsync(
        ReadOnlyMemory<byte> plaintext,
        TenantId tenant,
        SubjectId subject,
        CancellationToken ct)
    {
        // DeriveSubjectKeyAsync throws SubjectErasedException for a shredded
        // subject, so we never write fresh ciphertext under a destroyed key.
        var subKey = await _tenantKeys
            .DeriveSubjectKeyAsync(tenant, subject, PurposeLabel, _erasure, ct)
            .ConfigureAwait(false);

        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(subKey.Span, tagSizeInBytes: TagLength))
        {
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag);
        }

        var packed = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, packed, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, ciphertext.Length, tag.Length);
        return new EncryptedField(packed, nonce, CurrentKeyVersion, CurrentSuite);
    }
}
