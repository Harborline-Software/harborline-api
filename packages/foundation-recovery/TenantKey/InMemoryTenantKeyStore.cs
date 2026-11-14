using System.Collections.Concurrent;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>Volatile stored-key implementation for tests and development composition.</summary>
public sealed class InMemoryTenantKeyStore : IStoredTenantKeyStore
{
    private readonly ConcurrentDictionary<string, byte[]> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ReadOnlyMemory<byte>? Read(string slot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _records.TryGetValue(slot, out var value) ? (ReadOnlyMemory<byte>?)value.ToArray() : null;
    }

    /// <inheritdoc />
    public bool TryCreate(string slot, ReadOnlyMemory<byte> wrappedKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _records.TryAdd(slot, wrappedKey.ToArray());
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> ReadAsync(string slot, CancellationToken ct) => Task.FromResult(Read(slot, ct));

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(string slot, ReadOnlyMemory<byte> wrappedKey, CancellationToken ct) =>
        Task.FromResult(TryCreate(slot, wrappedKey, ct));

    /// <inheritdoc />
    public Task DeleteAsync(string slot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _records.TryRemove(slot, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteByPrefixAsync(string slotPrefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var slot in _records.Keys)
        {
            if (slot.StartsWith(slotPrefix, StringComparison.Ordinal))
            {
                _records.TryRemove(slot, out _);
            }
        }

        return Task.CompletedTask;
    }
}
