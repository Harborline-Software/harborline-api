namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — in-memory reference <see cref="IReplicaPossessionLedger"/>. Keyed by <c>(record, destinationId)</c>;
/// a later claim for the same destination replaces the earlier one. Thread-safe. A durable, restart-surviving
/// ledger is a later phase; the interface is what the verifier depends on. The interface is async (F-Maj-3) but
/// this reference impl completes synchronously.
/// </summary>
public sealed class InMemoryReplicaPossessionLedger : IReplicaPossessionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Record, string Destination), ConfirmedReplica> _claims = new();

    /// <inheritdoc />
    public ValueTask RecordAsync(ConfirmedReplica claim, CancellationToken ct = default)
    {
        claim.Destination.EnsureValid();
        lock (_gate)
        {
            _claims[(claim.Record.Value, claim.Destination.DestinationId)] = claim;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConfirmedReplica>> ListAsync(DurableRef record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var result = new List<ConfirmedReplica>();
            foreach (var kv in _claims)
            {
                if (string.Equals(kv.Key.Record, record.Value, StringComparison.Ordinal))
                {
                    result.Add(kv.Value);
                }
            }
            return ValueTask.FromResult<IReadOnlyList<ConfirmedReplica>>(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ForgetAsync(DurableRef record, string destinationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationId);
        lock (_gate)
        {
            return ValueTask.FromResult(_claims.Remove((record.Value, destinationId)));
        }
    }
}
