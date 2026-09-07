using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// In-memory, append-only <see cref="ISubjectTombstoneStore"/> for tests + dev
/// hosts. Restart-volatile — production hosts MUST override with a durable store
/// (see <see cref="ISubjectTombstoneStore"/>). Keyed by <c>(tenant, pseudonym)</c>.
/// </summary>
public sealed class InMemorySubjectTombstoneStore : ISubjectTombstoneStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Pseudonym), SubjectTombstone> _tombstones = new();

    /// <inheritdoc />
    public ValueTask WriteAsync(SubjectTombstone tombstone, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Write-once: TryAdd preserves the first tombstone if a duplicate erasure
        // races. The service makes the erasure itself idempotent upstream.
        _tombstones.TryAdd((tombstone.TenantId.Value, tombstone.Pseudonym), tombstone);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<SubjectTombstone?> FindAsync(TenantId tenant, string pseudonym, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _tombstones.TryGetValue((tenant.Value, pseudonym), out var found);
        return ValueTask.FromResult(found);
    }
}
