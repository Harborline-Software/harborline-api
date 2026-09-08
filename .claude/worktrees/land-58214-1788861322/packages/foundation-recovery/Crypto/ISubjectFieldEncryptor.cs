using System;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.Crypto;

/// <summary>
/// Encrypts a scalar field at rest into an <see cref="EncryptedField"/> envelope
/// using a <em>per-subject</em> sub-key derived under the per-tenant DEK (ADR
/// 0135 GDPR direction; ADR 0118 D4 sealed sub-keys). The subject scoping is what
/// makes a single data subject's ciphertext independently crypto-shreddable
/// without affecting other subjects in the same tenant.
/// </summary>
/// <remarks>
/// Encryption of an <em>already-erased</em> subject throws
/// <see cref="SubjectErasedException"/> — once shredded, no new ciphertext can be
/// produced under that subject's destroyed key either. The envelope byte-shape is
/// identical to the tenant-DEK <see cref="IFieldEncryptor"/>; only the key the
/// envelope is sealed under differs.
/// </remarks>
public interface ISubjectFieldEncryptor
{
    /// <summary>
    /// Produce an <see cref="EncryptedField"/> wrapping <paramref name="plaintext"/>
    /// under the per-subject sub-key for <paramref name="subject"/> within
    /// <paramref name="tenant"/>.
    /// </summary>
    /// <exception cref="SubjectErasedException">The subject has been crypto-shredded.</exception>
    Task<EncryptedField> EncryptForSubjectAsync(
        ReadOnlyMemory<byte> plaintext,
        TenantId tenant,
        SubjectId subject,
        CancellationToken ct);
}
