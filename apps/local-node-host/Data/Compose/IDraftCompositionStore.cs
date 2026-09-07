using System.Collections.Concurrent;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Compose;

/// <summary>
/// The per-node in-memory store of <see cref="DraftComposition"/> snapshots the Pack Composer authoring
/// session holds (B-2a). A draft lives only for the compose session; it is an authoring scratchpad, not a
/// durable artifact (the durable pack store, F5, is deferred — cerebrum 2026-07-06). Reads are
/// tenant-scoped: a draft is only visible to the tenant that composed it (isolation fence).
/// </summary>
public interface IDraftCompositionStore
{
    /// <summary>Saves (or replaces) a draft under its <see cref="DraftComposition.ComposeId"/>.</summary>
    void Save(DraftComposition draft);

    /// <summary>Fetches a draft by id, or <c>null</c> if unknown OR owned by a different tenant.</summary>
    DraftComposition? Get(string composeId, TenantId tenant);

    /// <summary>Removes a draft (e.g. after a successful export, or on discard).</summary>
    void Remove(string composeId, TenantId tenant);
}

/// <summary>Default <see cref="IDraftCompositionStore"/> — a thread-safe in-memory map keyed by compose id.
/// Ephemeral by design (node-lifetime).</summary>
public sealed class InMemoryDraftCompositionStore : IDraftCompositionStore
{
    private readonly ConcurrentDictionary<string, DraftComposition> _drafts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Save(DraftComposition draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _drafts[draft.ComposeId] = draft;
    }

    /// <inheritdoc />
    public DraftComposition? Get(string composeId, TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(composeId)) return null;
        if (!_drafts.TryGetValue(composeId, out var draft)) return null;
        // Tenant fence — a draft is only visible to the tenant that composed it.
        return draft.Tenant == tenant ? draft : null;
    }

    /// <inheritdoc />
    public void Remove(string composeId, TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(composeId)) return;
        if (_drafts.TryGetValue(composeId, out var draft) && draft.Tenant == tenant)
        {
            _drafts.TryRemove(composeId, out _);
        }
    }
}
