using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Append-only store for <see cref="SubjectTombstone"/> records. A production host
/// backs this with the same durable, append-only substrate that owns the audit
/// trail (the tombstone is a compliance record per ADR 0068 §1.3 and must survive
/// restart). There is no update or delete — tombstones are write-once.
/// </summary>
public interface ISubjectTombstoneStore
{
    /// <summary>Persist a tombstone. Called exactly once per subject erasure.</summary>
    ValueTask WriteAsync(SubjectTombstone tombstone, CancellationToken ct = default);

    /// <summary>
    /// Look up the tombstone for a pseudonym within a tenant; returns <c>null</c>
    /// when no erasure has been recorded for that pseudonym.
    /// </summary>
    ValueTask<SubjectTombstone?> FindAsync(TenantId tenant, string pseudonym, CancellationToken ct = default);
}
