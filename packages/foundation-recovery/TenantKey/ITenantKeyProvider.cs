using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>
/// Provides per-tenant key material for cryptographic operations such as HMAC and envelope encryption.
/// </summary>
public interface ITenantKeyProvider
{
    /// <summary>
    /// Resolve a 32-byte key for the given tenant + purpose label.
    /// </summary>
    /// <param name="tenant">Tenant identity.</param>
    /// <param name="purpose">Purpose label — e.g., <c>thread-token-hmac</c> or <c>encrypted-field-aes</c>. Different purposes derive different keys for the same tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>32-byte key material suitable for HMAC-SHA256 and AES-256.</returns>
    Task<ReadOnlyMemory<byte>> DeriveKeyAsync(TenantId tenant, string purpose, CancellationToken ct);

    /// <summary>
    /// Resolve a 32-byte <em>per-subject</em> key under the per-tenant DEK for
    /// the given (subject, purpose). Distinct subjects in the same tenant
    /// have independent keys, so a single subject can be crypto-shredded
    /// without affecting any other subject (ADR 0135 GDPR direction).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Production implementations store a random subject key wrapped by the tenant DEK.
    /// Version-aware implementations may import legacy derived keys while reading old ciphertext.
    /// </para>
    /// <para>
    /// <b>Fail-closed after crypto-shred.</b> Before resolving, the implementation MUST
    /// consult <paramref name="erasure"/> and throw
    /// <see cref="SubjectErasedException"/> if the subject is erased. This is the
    /// application-level guard in addition to physical stored-key destruction.
    /// </para>
    /// </remarks>
    /// <param name="tenant">Tenant identity.</param>
    /// <param name="subject">The data subject whose sub-key is requested.</param>
    /// <param name="purpose">Purpose label (e.g. <c>encrypted-field-aes</c>).</param>
    /// <param name="erasure">The crypto-shred register consulted for fail-closed behavior.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>32-byte per-subject sub-key.</returns>
    /// <exception cref="SubjectErasedException">The subject has been crypto-shredded.</exception>
    Task<ReadOnlyMemory<byte>> DeriveSubjectKeyAsync(
        TenantId tenant,
        SubjectId subject,
        string purpose,
        ISubjectErasureRegistry erasure,
        CancellationToken ct);
}
