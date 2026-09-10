using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Versions;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// Zero-dependency in-memory <see cref="IEntityStore"/>.
/// </summary>
/// <remarks>
/// Backed by <see cref="InMemoryAssetStorage"/>; paired with <see cref="InMemoryVersionStore"/>
/// over the same storage container so the materialized current-body cache and the append-only
/// version log stay in sync by construction (plan D-VERSION-STORE-SHAPE).
/// </remarks>
public sealed class InMemoryEntityStore : IEntityStore, IEntityMutationStore
{
    private readonly InMemoryAssetStorage _storage;
    private readonly IVersionObserver _observer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an in-memory entity store backed by the given shared storage.</summary>
    /// <remarks>
    /// The store takes no validator: a caller reaches <see cref="CreateAsync"/> and
    /// <see cref="UpdateAsync"/> only by presenting a <see cref="ValidatedBody"/>, which
    /// <see cref="EntityBodyAdmission"/> alone mints (ticket 151, ledger L1418).
    /// </remarks>
    public InMemoryEntityStore(
        InMemoryAssetStorage storage,
        TimeProvider timeProvider,
        IVersionObserver? observer = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _observer = observer ?? NullVersionObserver.Instance;
    }

    /// <inheritdoc />
    public Task<Entity?> GetAsync(EntityId id, VersionSelector version = default, CancellationToken ct = default) =>
        _storage.ExecuteExclusiveAsync(() => GetCoreAsync(id, version, ct), ct);

    private Task<Entity?> GetCoreAsync(EntityId id, VersionSelector version, CancellationToken ct)
    {
        if (!_storage.Entities.TryGetValue(id, out var record))
            return Task.FromResult<Entity?>(null);

        if (!_storage.Versions.TryGetValue(id, out var history))
            return Task.FromResult<Entity?>(null);

        Versions.Version? selected;
        if (version.ExplicitSequence is { } seq)
        {
            selected = history.FirstOrDefault(v => v.Id.Sequence == seq);
            if (selected is null) return Task.FromResult<Entity?>(null);
        }
        else if (version.AsOf is { } at)
        {
            selected = history
                .Where(v => v.ValidFrom <= at && (v.ValidTo is null || at < v.ValidTo))
                .OrderByDescending(v => v.Id.Sequence)
                .FirstOrDefault();
            if (selected is null) return Task.FromResult<Entity?>(null);
            if (IsTombstone(selected)) return Task.FromResult<Entity?>(null);
        }
        else
        {
            if (record.DeletedAt is not null) return Task.FromResult<Entity?>(null);
            selected = history[^1];
        }

        var entity = new Entity(
            record.Id,
            record.Schema,
            record.Tenant,
            selected.Id,
            CloneBody(selected.Body),
            record.CreatedAt,
            record.UpdatedAt,
            record.DeletedAt,
            CloneBinding(record.Binding));

        return Task.FromResult<Entity?>(entity);
    }

    /// <inheritdoc />
    public Task<EntityId> CreateAsync(ValidatedBody body, CreateOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _storage.ExecuteExclusiveAsync(() => CreateCoreAsync(body.Schema, body.Body, options, ct), ct);
    }

    private Task<EntityId> CreateCoreAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var id = DeriveEntityId(schema, options);
        var validFrom = options.ValidFrom ?? _timeProvider.GetUtcNow();
        var canonicalBody = JsonCanonicalizer.ToCanonicalString(body);

        var lockObj = _storage.LockFor(id);
        lock (lockObj)
        {
            if (_storage.Entities.TryGetValue(id, out var existing))
            {
                // Idempotent: same body → return same id.
                if (existing.BodyJson == canonicalBody)
                    return Task.FromResult(id);
                throw new IdempotencyConflictException(
                    $"Entity '{id}' already exists with a different body; refusing to overwrite via CreateAsync.");
            }

            var initialBody = CloneBody(body);
            var hash = HashVersion(parentHash: null, canonicalBody: canonicalBody, validFrom: validFrom);
            var versionId = new VersionId(id, 1, hash);
            var version = new Versions.Version(
                Id: versionId,
                ParentId: null,
                Body: initialBody,
                ValidFrom: validFrom,
                ValidTo: null,
                Author: options.Issuer,
                Signature: null,
                Diff: null);

            var history = new List<Versions.Version> { version };
            _storage.Versions[id] = history;

            var record = new EntityRecord
            {
                Id = id,
                Schema = schema,
                Tenant = options.Tenant,
                CurrentVersion = versionId,
                BodyJson = canonicalBody,
                CreatedAt = validFrom,
                UpdatedAt = validFrom,
                DeletedAt = null,
                CreationNonce = options.Nonce,
                CreationIssuer = options.Issuer,
                Binding = CloneBinding(options.Binding),
            };
            _storage.Entities[id] = record;

            _ = _observer.OnVersionAppendedAsync(id, version, ct);
            return Task.FromResult(id);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Atomic override.</b> All inserts succeed or none are visible: if any draft fails (e.g.
    /// validation, idempotency conflict), every entity written earlier in the batch is removed from
    /// the store before the exception propagates, leaving the store in exactly its pre-batch state.
    /// Locks are acquired in a deterministic order (lexicographic on the derived <see cref="EntityId"/>)
    /// to prevent deadlocks when concurrent batches overlap.
    /// </remarks>
    public Task<IReadOnlyList<EntityId>> CreateBatchAsync(
        IEnumerable<EntityDraft> drafts,
        CancellationToken ct = default) =>
        _storage.ExecuteExclusiveAsync(() => CreateBatchCoreAsync(drafts, ct), ct);

    private Task<IReadOnlyList<EntityId>> CreateBatchCoreAsync(
        IEnumerable<EntityDraft> drafts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);

        // Materialise so we can iterate twice (validate + insert) without re-evaluating.
        var draftList = drafts as IReadOnlyList<EntityDraft> ?? drafts.ToList();
        if (draftList.Count == 0)
            return Task.FromResult<IReadOnlyList<EntityId>>(Array.Empty<EntityId>());

        // --- Phase 1: every draft carries its own validation token (ticket 151, L1418) ---
        foreach (var draft in draftList)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(draft.Body, nameof(draft));
            ArgumentNullException.ThrowIfNull(draft.Options, nameof(draft));
        }

        // --- Phase 2: derive all entity IDs (deterministic, lock-free) ---
        var derivedIds = new EntityId[draftList.Count];
        for (int i = 0; i < draftList.Count; i++)
            derivedIds[i] = DeriveEntityId(draftList[i].Schema, draftList[i].Options);

        // --- Phase 3: acquire locks in a consistent order to prevent deadlocks ---
        // Sort DISTINCT ids; two drafts targeting the same id will share one lock.
        var distinctIds = derivedIds
            .Distinct()
            .OrderBy(id => id.Scheme, StringComparer.Ordinal)
            .ThenBy(id => id.Authority, StringComparer.Ordinal)
            .ThenBy(id => id.LocalPart, StringComparer.Ordinal)
            .Select(id => _storage.LockFor(id))
            .ToArray();

        // Acquire all locks. We need a recursive-safe approach: take them one by one in order.
        var locksHeld = new List<object>(distinctIds.Length);
        try
        {
            foreach (var lockObj in distinctIds)
            {
                Monitor.Enter(lockObj);
                locksHeld.Add(lockObj);
            }

            // --- Phase 4: perform all inserts while holding all locks ---
            // `results` is the ordered output (one EntityId per draft).
            // `newlyInserted` tracks only the ids THIS batch wrote — idempotent hits are
            // excluded so rollback never removes a pre-existing entity.
            var results = new List<EntityId>(draftList.Count);
            var newlyInserted = new HashSet<EntityId>();
            try
            {
                for (int i = 0; i < draftList.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var draft = draftList[i];
                    var id = derivedIds[i];
                    var validFrom = draft.Options.ValidFrom ?? _timeProvider.GetUtcNow();
                    var canonicalBody = JsonCanonicalizer.ToCanonicalString(draft.Body.Body);

                    if (_storage.Entities.TryGetValue(id, out var existing))
                    {
                        if (existing.BodyJson == canonicalBody)
                        {
                            // Idempotent match — entity pre-existed with matching body, accept as-is.
                            results.Add(id);
                            continue;
                        }
                        throw new IdempotencyConflictException(
                            $"Entity '{id}' already exists with a different body; batch rolled back.");
                    }

                    var initialBody = CloneBody(draft.Body.Body);
                    var hash = HashVersion(parentHash: null, canonicalBody: canonicalBody, validFrom: validFrom);
                    var versionId = new VersionId(id, 1, hash);
                    var version = new Versions.Version(
                        Id: versionId,
                        ParentId: null,
                        Body: initialBody,
                        ValidFrom: validFrom,
                        ValidTo: null,
                        Author: draft.Options.Issuer,
                        Signature: null,
                        Diff: null);

                    var history = new List<Versions.Version> { version };
                    _storage.Versions[id] = history;

                    var record = new EntityRecord
                    {
                        Id = id,
                        Schema = draft.Schema,
                        Tenant = draft.Options.Tenant,
                        CurrentVersion = versionId,
                        BodyJson = canonicalBody,
                        CreatedAt = validFrom,
                        UpdatedAt = validFrom,
                        DeletedAt = null,
                        CreationNonce = draft.Options.Nonce,
                        CreationIssuer = draft.Options.Issuer,
                        Binding = CloneBinding(draft.Options.Binding),
                    };
                    _storage.Entities[id] = record;
                    results.Add(id);
                    newlyInserted.Add(id);
                }

                // All inserts succeeded.
                return Task.FromResult<IReadOnlyList<EntityId>>(results);
            }
            catch
            {
                // Rollback: undo only the entities THIS batch created. Idempotent re-hits
                // (pre-existing entities) are intentionally excluded from `newlyInserted`
                // and are left untouched.
                foreach (var insertedId in newlyInserted)
                {
                    _storage.Entities.TryRemove(insertedId, out _);
                    _storage.Versions.TryRemove(insertedId, out _);
                }
                throw;
            }
        }
        finally
        {
            // Release all locks in reverse order.
            for (int i = locksHeld.Count - 1; i >= 0; i--)
                Monitor.Exit(locksHeld[i]);
        }
    }

    /// <inheritdoc />
    public Task<VersionId> UpdateAsync(EntityId id, ValidatedBody newBody, UpdateOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newBody);
        return _storage.ExecuteExclusiveAsync(() => UpdateCoreAsync(id, newBody, options, ct), ct);
    }

    private async Task<VersionId> UpdateCoreAsync(
        EntityId id,
        ValidatedBody validated,
        UpdateOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_storage.Entities.TryGetValue(id, out var record))
            throw new InvalidOperationException($"Entity '{id}' not found.");

        // The update was validated against the schema the caller named; it must be the schema this
        // record actually carries, or that validation proves nothing about the version appended here.
        if (validated.Schema != record.Schema)
            throw new EntityValidationException(
                EntityValidationException.SchemaUnknown,
                $"Entity '{id}' is an instance of schema '{record.Schema}'; the update was validated against '{validated.Schema}'.");

        var newBody = validated.Body;
        var canonicalBody = JsonCanonicalizer.ToCanonicalString(newBody);
        var validFrom = options.ValidFrom ?? _timeProvider.GetUtcNow();

        Versions.Version appended;
        var lockObj = _storage.LockFor(id);
        lock (lockObj)
        {
            var history = _storage.Versions[id];
            var tail = history[^1];

            if (options.ExpectedVersion is { } expected && tail.Id != expected)
                throw new ConcurrencyException(
                    $"Entity '{id}' has advanced beyond expected version {expected}; current tip is {tail.Id}.");

            // Close out the previous version's validity so ranges are contiguous.
            var closed = tail with { ValidTo = validFrom };
            history[^1] = closed;

            var parentHash = closed.Id.Hash;
            var hash = HashVersion(parentHash: parentHash, canonicalBody: canonicalBody, validFrom: validFrom);
            var versionId = new VersionId(id, closed.Id.Sequence + 1, hash);
            appended = new Versions.Version(
                Id: versionId,
                ParentId: closed.Id,
                Body: CloneBody(newBody),
                ValidFrom: validFrom,
                ValidTo: null,
                Author: options.Actor,
                Signature: null,
                Diff: null);
            history.Add(appended);

            record.CurrentVersion = versionId;
            record.BodyJson = canonicalBody;
            record.UpdatedAt = validFrom;
            record.DeletedAt = null;
        }

        await _observer.OnVersionAppendedAsync(id, appended, ct).ConfigureAwait(false);
        return appended.Id;
    }

    /// <inheritdoc />
    public Task DeleteAsync(EntityId id, DeleteOptions options, CancellationToken ct = default) =>
        _storage.ExecuteExclusiveAsync(() => DeleteCoreAsync(id, options, ct), ct);

    private async Task DeleteCoreAsync(EntityId id, DeleteOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_storage.Entities.TryGetValue(id, out var record))
            throw new InvalidOperationException($"Entity '{id}' not found.");

        var validFrom = options.ValidFrom ?? _timeProvider.GetUtcNow();
        using var tombstoneBody = JsonDocument.Parse("{}");

        Versions.Version appended;
        var lockObj = _storage.LockFor(id);
        lock (lockObj)
        {
            var history = _storage.Versions[id];
            var tail = history[^1];
            var closed = tail with { ValidTo = validFrom };
            history[^1] = closed;

            var canonicalBody = JsonCanonicalizer.ToCanonicalString(tombstoneBody);
            var parentHash = closed.Id.Hash;
            var hash = HashVersion(parentHash: parentHash, canonicalBody: canonicalBody, validFrom: validFrom);
            var versionId = new VersionId(id, closed.Id.Sequence + 1, hash);
            appended = new Versions.Version(
                Id: versionId,
                ParentId: closed.Id,
                Body: JsonDocument.Parse("{}"),
                ValidFrom: validFrom,
                ValidTo: null,
                Author: options.Actor,
                Signature: null,
                Diff: null);
            history.Add(appended);

            record.CurrentVersion = versionId;
            record.BodyJson = canonicalBody;
            record.UpdatedAt = validFrom;
            record.DeletedAt = validFrom;
        }

        await _observer.OnVersionAppendedAsync(id, appended, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Entity> QueryAsync(EntityQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await _storage.ExecuteExclusiveAsync(
            () => Task.FromResult(MaterializeQuery(query, ct)), ct).ConfigureAwait(false);

        foreach (var entity in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return entity;
            await Task.Yield();
        }
    }

    private IReadOnlyList<Entity> MaterializeQuery(EntityQuery query, CancellationToken ct)
    {
        var snapshot = new List<Entity>();
        int emitted = 0;
        foreach (var record in _storage.Entities.Values)
        {
            ct.ThrowIfCancellationRequested();

            if (query.Schema is { } schema && !string.Equals(record.Schema.Value, schema.Value, StringComparison.Ordinal))
                continue;
            if (query.Tenant is not null && !query.Tenant.Matches(record.Tenant))
                continue;

            if (!query.IncludeDeleted)
            {
                if (query.AsOf is { } asOf)
                {
                    if (record.DeletedAt is { } deletedAt && deletedAt <= asOf)
                        continue;
                }
                else if (record.DeletedAt is not null)
                {
                    continue;
                }
            }

            if (!_storage.Versions.TryGetValue(record.Id, out var history))
                continue;

            // Body-containment predicate. Mirrors PostgreSQL @> against the current materialized
            // body (latest version), evaluated before version selection just as the SQL store
            // filters on entities.body_json before picking the version row.
            if (query.BodyContains is { } operand && !JsonContainsOperand(history[^1].Body, operand))
                continue;

            Versions.Version? picked;
            if (query.AsOf is { } at)
            {
                picked = history
                    .Where(v => v.ValidFrom <= at && (v.ValidTo is null || at < v.ValidTo))
                    .OrderByDescending(v => v.Id.Sequence)
                    .FirstOrDefault();
                if (picked is null) continue;
            }
            else
            {
                picked = history[^1];
            }

            snapshot.Add(new Entity(
                record.Id,
                record.Schema,
                record.Tenant,
                picked.Id,
                CloneBody(picked.Body),
                record.CreatedAt,
                record.UpdatedAt,
                record.DeletedAt,
                CloneBinding(record.Binding)));

            emitted++;
            if (query.Limit is { } limit && emitted >= limit) break;
        }

        return snapshot;
    }

    private static bool IsTombstone(Versions.Version version)
    {
        using var doc = JsonDocument.Parse(JsonCanonicalizer.ToCanonicalBytes(version.Body));
        return doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.EnumerateObject().Any();
    }

    private static JsonDocument CloneBody(JsonDocument body)
        => JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(body.RootElement));

    private static EntityBinding? CloneBinding(EntityBinding? binding) => binding is null
        ? null
        : binding with { LocaleChain = binding.LocaleChain.ToArray() };

    private static bool JsonContainsOperand(JsonDocument body, string operandJson)
    {
        using var operandDoc = JsonDocument.Parse(operandJson);
        return JsonContains(body.RootElement, operandDoc.RootElement);
    }

    /// <summary>
    /// Mirrors PostgreSQL <c>jsonb @&gt;</c> containment so the in-memory store is a faithful double
    /// of the durable store: object = every operand key present and recursively contained; array =
    /// every operand element contained in some target element; scalar = equality. As a special
    /// exception (also Postgres'), an array contains a primitive value when any element equals it.
    /// </summary>
    private static bool JsonContains(JsonElement target, JsonElement operand)
    {
        if (target.ValueKind != operand.ValueKind)
        {
            if (target.ValueKind == JsonValueKind.Array && IsPrimitive(operand))
            {
                foreach (var element in target.EnumerateArray())
                    if (JsonContains(element, operand))
                        return true;
            }
            return false;
        }

        switch (operand.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in operand.EnumerateObject())
                {
                    if (!target.TryGetProperty(prop.Name, out var targetValue))
                        return false;
                    if (!JsonContains(targetValue, prop.Value))
                        return false;
                }
                return true;

            case JsonValueKind.Array:
                foreach (var operandElement in operand.EnumerateArray())
                {
                    var matched = false;
                    foreach (var targetElement in target.EnumerateArray())
                    {
                        if (JsonContains(targetElement, operandElement))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                        return false;
                }
                return true;

            case JsonValueKind.String:
                return string.Equals(target.GetString(), operand.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                if (target.TryGetDecimal(out var targetNumber) && operand.TryGetDecimal(out var operandNumber))
                    return targetNumber == operandNumber;
                return string.Equals(target.GetRawText(), operand.GetRawText(), StringComparison.Ordinal);

            default:
                // True / False / Null: kinds already match, so containment holds.
                return true;
        }

    }

    private static bool IsPrimitive(JsonElement element) => element.ValueKind is
        JsonValueKind.String or JsonValueKind.Number or
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

    public static EntityId DeriveEntityId(SchemaId schema, CreateOptions options)
    {
        if (options.ExplicitLocalPart is { Length: > 0 } explicitLocal)
            return new EntityId(options.Scheme, options.Authority, explicitLocal);

        var input = Encoding.UTF8.GetBytes($"{schema.Value}|{options.Authority}|{options.Nonce}|{options.Issuer.Value}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        // Take 16 bytes (128-bit) and base32-encode via the existing Blobs Base32 shape.
        var local = Base32Lower.Encode(digest[..16]);
        return new EntityId(options.Scheme, options.Authority, local);
    }

    private static string HashVersion(string? parentHash, string canonicalBody, DateTimeOffset validFrom)
    {
        var prefix = parentHash ?? string.Empty;
        var input = Encoding.UTF8.GetBytes($"{prefix}|{canonicalBody}|{validFrom:O}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return Convert.ToHexStringLower(digest);
    }
}

/// <summary>Read-only facade that prevents a resolved <see cref="IEntityStore"/> from being cast to a raw writer.</summary>
public sealed class InMemoryEntityStoreReader(InMemoryEntityStore inner) : IEntityStore
{
    public Task<Entity?> GetAsync(
        EntityId id,
        VersionSelector version = default,
        CancellationToken ct = default) => inner.GetAsync(id, version, ct);

    public IAsyncEnumerable<Entity> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
        inner.QueryAsync(query, ct);
}

/// <summary>RFC 4648 base32 lowercase, no padding. Internal duplicate to avoid Blobs coupling.</summary>
internal static class Base32Lower
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;

        var outputLength = (bytes.Length * 8 + 4) / 5;
        var output = new char[outputLength];
        int buffer = 0, bitsLeft = 0, outputIndex = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output[outputIndex++] = Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F];
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            output[outputIndex] = Alphabet[(buffer << (5 - bitsLeft)) & 0x1F];

        return new string(output);
    }
}
