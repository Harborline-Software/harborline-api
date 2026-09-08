using System.Collections.Concurrent;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Versions;

namespace Harborline.Api.Foundation.Assets.Common;

/// <summary>
/// Shared in-memory storage that backs the InMemory implementations of
/// <see cref="IEntityStore"/>, <see cref="IVersionStore"/> and <see cref="IAuditLog"/>.
/// </summary>
/// <remarks>
/// Keeping a single container ensures the "materialized current body" on an entity stays in
/// sync with the append-only version log by construction (plan D-VERSION-STORE-SHAPE).
/// Consumers who want independent stores can construct independent storage instances.
/// </remarks>
public sealed class InMemoryAssetStorage
{
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly AsyncLocal<int> _transactionDepth = new();

    /// <summary>Materialized current body + metadata, keyed by entity id.</summary>
    public ConcurrentDictionary<EntityId, EntityRecord> Entities { get; } = new();

    /// <summary>Append-only version history, keyed by entity id.</summary>
    public ConcurrentDictionary<EntityId, List<Versions.Version>> Versions { get; } = new();

    /// <summary>Append-only audit log, keyed by entity id.</summary>
    public ConcurrentDictionary<EntityId, List<AuditRecord>> Audit { get; } = new();

    /// <summary>Per-entity write lock for serialising version-log mutations.</summary>
    public ConcurrentDictionary<EntityId, object> EntityLocks { get; } = new();

    /// <summary>Returns the per-entity lock, lazily creating one if needed.</summary>
    public object LockFor(EntityId id) => EntityLocks.GetOrAdd(id, static _ => new object());

    /// <summary>
    /// Runs one storage operation beneath the same gate used by hierarchy composites. Nested calls
    /// made by a composite enlist in its existing unit instead of deadlocking on the semaphore.
    /// </summary>
    internal async Task<T> ExecuteExclusiveAsync<T>(
        Func<Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_transactionDepth.Value != 0)
            return await action().ConfigureAwait(false);

        await _transactionGate.WaitAsync(ct).ConfigureAwait(false);
        _transactionDepth.Value++;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _transactionDepth.Value--;
            _transactionGate.Release();
        }
    }

    internal Task ExecuteExclusiveAsync(Func<Task> action, CancellationToken ct = default) =>
        ExecuteExclusiveAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, ct);
}

/// <summary>
/// Materialized "current" projection of an entity. Phase A keeps this alongside the append-only
/// version log for O(1) reads (plan D-VERSION-STORE-SHAPE).
/// </summary>
public sealed class EntityRecord
{
    /// <summary>The entity's canonical id.</summary>
    public required EntityId Id { get; init; }

    /// <summary>The schema this entity conforms to.</summary>
    public required SchemaId Schema { get; init; }

    /// <summary>The tenant that owns this entity.</summary>
    public required TenantId Tenant { get; init; }

    /// <summary>Cursor to the currently materialized version.</summary>
    public VersionId CurrentVersion { get; set; }

    /// <summary>Serialized canonical body for the current version.</summary>
    public required string BodyJson { get; set; }

    /// <summary>When the entity was first minted.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the entity was last updated (or deleted).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>If non-null, the time at which this entity was tombstoned.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// The <see cref="CreateOptions"/> nonce recorded at mint time, used for idempotent
    /// <c>CreateAsync</c> checks.
    /// </summary>
    public string? CreationNonce { get; init; }

    /// <summary>The issuer recorded at mint time.</summary>
    public ActorId CreationIssuer { get; init; }

    /// <summary>Definition-and-engine provenance co-committed at mint time.</summary>
    public EntityBinding? Binding { get; init; }
}
