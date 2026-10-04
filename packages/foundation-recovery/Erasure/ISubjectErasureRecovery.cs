using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// T-1048 (DES-0029 ck-6): finishes subject erasures a crash interrupted, without a client retry. Separate from
/// <see cref="ISubjectErasureService"/> so existing implementations of that interface keep compiling; a host
/// recovers erasures only through a service that also implements this.
/// </summary>
public interface ISubjectErasureRecovery
{
    /// <summary>
    /// Finishes up to <paramref name="limit"/> erasures whose registry mark committed but which a crash interrupted
    /// before their audit was secured, from the approval evidence the mark recorded. A host runs it at startup and
    /// on an interval. A failing erasure is deferred with backoff, never abandoned.
    /// </summary>
    /// <returns>The number of erasures this pass completed.</returns>
    Task<int> RecoverInterruptedAsync(int limit, CancellationToken ct = default);
}
