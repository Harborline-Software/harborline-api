using System;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.Crypto;

/// <summary>
/// Decrypts a per-subject <see cref="EncryptedField"/> (one produced by
/// <see cref="ISubjectFieldEncryptor"/>) after validating an
/// <see cref="IDecryptCapability"/>. When the subject has been crypto-shredded
/// the per-subject key is gone, so decrypt <b>fails closed</b> with
/// <see cref="FieldDecryptionDeniedException"/> (reason <c>"subject erased"</c>)
/// and emits a denial audit record — never plaintext.
/// </summary>
public interface ISubjectFieldDecryptor
{
    /// <summary>
    /// Validate <paramref name="capability"/> and, if valid AND the subject is not
    /// erased, AES-GCM-decrypt <paramref name="field"/> using the per-subject
    /// sub-key for <paramref name="subject"/>.
    /// </summary>
    /// <exception cref="FieldDecryptionDeniedException">
    /// Capability rejected; the subject has been crypto-shredded (key destroyed);
    /// ciphertext truncated; AES-GCM tag verification failed; or unsupported
    /// <see cref="EncryptedField.KeyVersion"/>.
    /// </exception>
    Task<ReadOnlyMemory<byte>> DecryptForSubjectAsync(
        EncryptedField field,
        IDecryptCapability capability,
        TenantId tenant,
        SubjectId subject,
        CancellationToken ct);
}
