using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Records which data subjects have been erased and prevents replacement-key creation.
/// </summary>
/// <remarks>
/// Physical crypto-shredding is performed by
/// <see cref="Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyDestroyer"/>, which deletes the stored
/// subject key. Both operations are required: deletion makes ciphertext unreadable, while this durable
/// registry prevents an erased identity from receiving a newly generated replacement key.
/// </remarks>
public interface ISubjectErasureRegistry
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="subject"/> has been crypto-shredded
    /// for <paramref name="tenant"/>. The key-resolution path consults this and
    /// fails closed when it returns <c>true</c>.
    /// </summary>
    ValueTask<bool> IsErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default);

    /// <summary>
    /// Mark <paramref name="subject"/> as crypto-shredded for
    /// <paramref name="tenant"/>. Idempotent: a second call for the same subject
    /// returns <c>false</c> (already erased) without altering the recorded
    /// tombstone. The registry is append-only — there is NO un-erase. After this
    /// returns, every subject-key resolution for the subject fails closed.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this call performed the erasure (first time);
    /// <c>false</c> when the subject was already erased.
    /// </returns>
    ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default);
}
