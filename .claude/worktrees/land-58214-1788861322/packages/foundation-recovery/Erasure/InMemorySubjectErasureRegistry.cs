using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// In-memory, append-only <see cref="ISubjectErasureRegistry"/> for tests, dev
/// hosts, and v1 substrates that have an erasure requirement but no durable
/// tombstone store yet. Restart-volatile — a process restart forgets erasures,
/// so production hosts MUST override this with a durable implementation backed by
/// the same append-only store that owns the audit trail (the erasure tombstone is
/// itself a compliance record and must survive restart per ADR 0068 §1.3).
/// </summary>
/// <remarks>
/// The set is keyed by <c>(tenant, subject)</c> and is grow-only — there is no
/// removal API, mirroring the one-directional crypto-shred semantics
/// (<see cref="ISubjectErasureRegistry"/>). Concurrent <c>MarkErasedAsync</c>
/// calls for the same subject are resolved by the <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// so exactly one returns <c>true</c>.
/// </remarks>
public sealed class InMemorySubjectErasureRegistry : ISubjectErasureRegistry
{
    private readonly ConcurrentDictionary<(string Tenant, string Subject), byte> _erased = new();

    /// <inheritdoc />
    public ValueTask<bool> IsErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var hit = _erased.ContainsKey((tenant.Value, subject.Value));
        return ValueTask.FromResult(hit);
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var added = _erased.TryAdd((tenant.Value, subject.Value), 0);
        return ValueTask.FromResult(added);
    }
}
