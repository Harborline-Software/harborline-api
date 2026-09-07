namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>Persists opaque, wrapped tenant-key hierarchy records.</summary>
public interface IStoredTenantKeyStore
{
    /// <summary>Read a wrapped key record, or <c>null</c> when the slot is absent.</summary>
    Task<ReadOnlyMemory<byte>?> ReadAsync(string slot, CancellationToken ct);

    /// <summary>Create a wrapped key record only when the slot is absent.</summary>
    /// <returns><c>true</c> when this call created the slot.</returns>
    Task<bool> TryCreateAsync(string slot, ReadOnlyMemory<byte> wrappedKey, CancellationToken ct);

    /// <summary>Delete a wrapped key record.</summary>
    Task DeleteAsync(string slot, CancellationToken ct);

    /// <summary>Delete every wrapped key record whose opaque slot starts with a prefix.</summary>
    Task DeleteByPrefixAsync(string slotPrefix, CancellationToken ct);
}
