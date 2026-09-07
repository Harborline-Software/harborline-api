using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Versions;

/// <summary>
/// Zero-dependency in-memory <see cref="IVersionStore"/> sharing storage with
/// <see cref="Harborline.Api.Foundation.Assets.Entities.InMemoryEntityStore"/>.
/// </summary>
public sealed class InMemoryVersionStore : IVersionStore
{
    private readonly InMemoryAssetStorage _storage;

    /// <summary>Creates an in-memory version store backed by the given shared storage.</summary>
    public InMemoryVersionStore(InMemoryAssetStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <inheritdoc />
    public Task<Version?> GetVersionAsync(VersionId id, CancellationToken ct = default) =>
        _storage.ExecuteExclusiveAsync(() => Task.FromResult(GetVersion(id)), ct);

    private Version? GetVersion(VersionId id)
    {
        if (!_storage.Versions.TryGetValue(id.Entity, out var history))
            return null;
        return history.FirstOrDefault(v => v.Id.Sequence == id.Sequence && v.Id.Hash == id.Hash);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Version> GetHistoryAsync(EntityId entity, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var snapshot = await _storage.ExecuteExclusiveAsync(() => Task.FromResult(
            _storage.Versions.TryGetValue(entity, out var history)
                ? history.ToArray()
                : Array.Empty<Version>()), ct).ConfigureAwait(false);
        if (snapshot.Length == 0)
            yield break;
        foreach (var version in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return version;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public Task<Version?> GetAsOfAsync(EntityId entity, DateTimeOffset at, CancellationToken ct = default) =>
        _storage.ExecuteExclusiveAsync(() => Task.FromResult(GetAsOf(entity, at)), ct);

    private Version? GetAsOf(EntityId entity, DateTimeOffset at)
    {
        if (!_storage.Versions.TryGetValue(entity, out var history))
            return null;
        return history
            .Where(v => v.ValidFrom <= at && (v.ValidTo is null || at < v.ValidTo))
            .OrderByDescending(v => v.Id.Sequence)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public Task<VersionId> BranchAsync(VersionId from, BranchOptions options, CancellationToken ct = default)
        => throw new NotImplementedException(
            "Phase A ships linear history only. Branch/merge lands in Platform Phase B; see plan D-CRDT-ROUTE.");

    /// <inheritdoc />
    public Task<VersionId> MergeAsync(VersionId left, VersionId right, MergeOptions options, CancellationToken ct = default)
        => throw new NotImplementedException(
            "Phase A ships linear history only. Branch/merge lands in Platform Phase B; see plan D-CRDT-ROUTE.");
}
