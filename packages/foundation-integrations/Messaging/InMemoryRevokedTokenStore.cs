using System.Collections.Concurrent;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Integrations.Messaging;

/// <summary>
/// In-memory <see cref="IRevokedTokenStore"/>. Thread-safe via
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; not durable. Production
/// hosts replace with a persistent store when one ships.
/// </summary>
public sealed class InMemoryRevokedTokenStore : IRevokedTokenStore
{
    // (tenant, hmac-fragment) → tombstone marker; ConcurrentDictionary's bool value is a presence flag.
    private readonly ConcurrentDictionary<(TenantId Tenant, string Hmac), bool> _revoked = new();

    /// <inheritdoc />
    public Task AppendAsync(TenantId tenant, string tokenHmacFragment, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHmacFragment);
        _revoked[(tenant, tokenHmacFragment)] = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsRevokedAsync(TenantId tenant, string tokenHmacFragment, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHmacFragment);
        return Task.FromResult(_revoked.ContainsKey((tenant, tokenHmacFragment)));
    }
}
