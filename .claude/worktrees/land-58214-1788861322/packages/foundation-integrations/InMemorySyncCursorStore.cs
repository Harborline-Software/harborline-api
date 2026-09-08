using System.Collections.Concurrent;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Integrations;

/// <summary>Default in-memory <see cref="ISyncCursorStore"/>.</summary>
public sealed class InMemorySyncCursorStore : ISyncCursorStore
{
    private readonly ConcurrentDictionary<string, SyncCursor> _byKey = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<SyncCursor?> GetAsync(
        string providerKey,
        TenantId? tenantId,
        string scope,
        CancellationToken cancellationToken = default)
    {
        _byKey.TryGetValue(Key(providerKey, tenantId, scope), out var cursor);
        return ValueTask.FromResult(cursor);
    }

    /// <inheritdoc />
    public ValueTask PutAsync(SyncCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        _byKey[Key(cursor.ProviderKey, cursor.TenantId, cursor.Scope)] = cursor;
        return ValueTask.CompletedTask;
    }

    private static string Key(string providerKey, TenantId? tenantId, string scope)
        => $"{providerKey}|{tenantId?.Value ?? "-"}|{scope}";
}
