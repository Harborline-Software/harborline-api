using Harborline.Api.Foundation.Definitions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.SchemaRegistry.Epochs;
using Harborline.Api.Kernel.SchemaRegistry.Lenses;
using Harborline.Api.Kernel.SchemaRegistry.Upcasters;

using Platform = Harborline.Kernel.SchemaValidation;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// In-memory default backend for <see cref="ISchemaRegistry"/>.
/// Schemas are kept in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed
/// by <see cref="SchemaId"/>. The <see cref="Schema"/> record carries the full
/// <see cref="Schema.JsonSchemaText"/> inline plus its canonical-bytes
/// <see cref="Schema.ContentAddress"/>, so no blob side-store is involved: a
/// federation peer that wants the bytes re-canonicalizes the inline text and
/// verifies it against the CID. (An earlier revision also wrote the canonical
/// bytes to an injected <c>IBlobStore</c>; against the node's envelope-sealed
/// store that discarded the returned ciphertext CID and permanently orphaned
/// one encrypted file per register — earlier repository card 3776.)
/// </summary>
/// <remarks>
/// <para>
/// This backend is not persistent — process restarts clear the dictionary.
/// Persistent backends (spec §3.4 follow-ups) can implement the same interface
/// over Postgres, Confluent Schema Registry, or Apicurio without touching
/// consumers.
/// </para>
/// <para>
/// T-303: registration and validation run in the platform
/// <c>Harborline.Kernel.SchemaValidation</c> library (draft 2020-12 only, the resource bounds,
/// the timed <c>pattern</c> and the field-addressable errors). This class keeps the api-only
/// responsibilities: the <c>schema:{cid}</c> id every stored definition already carries, parents,
/// tags, the blob threshold, pack-projection staging, and the lens, upcaster and epoch surface.
/// </para>
/// </remarks>
public sealed class InMemorySchemaRegistry : ISchemaRegistry, IPackProjectionParticipant
{
    private readonly Platform.SchemaRegistryOptions _options;
    private readonly Platform.InMemorySchemaRegistry _validator;
    private ConcurrentDictionary<SchemaId, Entry> _schemas = new();

    /// <inheritdoc />
    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var before = _schemas;
        var next = new ConcurrentDictionary<SchemaId, Entry>(before);
        _schemas = next;
        return () => _schemas = before;
    });

    /// <summary>
    /// Creates a new <see cref="InMemorySchemaRegistry"/>. The migration surface
    /// (lenses, upcasters, epochs) is initialized with empty in-memory stores;
    /// callers mutate them via the public <see cref="Lenses"/> /
    /// <see cref="Upcasters"/> / <see cref="Epochs"/> members.
    /// <paramref name="options"/> tunes the INV-S2 register-time resource bounds;
    /// <c>null</c> uses <see cref="Platform.SchemaRegistryOptions.Default"/>.
    /// </summary>
    public InMemorySchemaRegistry(TimeProvider timeProvider, Platform.SchemaRegistryOptions? options = null)
        : this(new LensGraph(), new UpcasterChain(), new EpochCoordinator(timeProvider), options)
    {
    }

    /// <summary>
    /// Creates a new <see cref="InMemorySchemaRegistry"/> with explicitly-supplied
    /// lens graph / upcaster chain / epoch coordinator. Intended for DI paths where
    /// the migration primitives are registered as separate singletons so downstream
    /// components can depend on them directly. <paramref name="options"/> tunes the
    /// INV-S2 register-time resource bounds; <c>null</c> uses
    /// <see cref="Platform.SchemaRegistryOptions.Default"/>.
    /// </summary>
    public InMemorySchemaRegistry(
        LensGraph lenses,
        UpcasterChain upcasters,
        IEpochCoordinator epochs,
        Platform.SchemaRegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lenses);
        ArgumentNullException.ThrowIfNull(upcasters);
        ArgumentNullException.ThrowIfNull(epochs);
        _options = options ?? Platform.SchemaRegistryOptions.Default;
        _validator = new Platform.InMemorySchemaRegistry(_options);
        Lenses = lenses;
        Upcasters = upcasters;
        Epochs = epochs;
    }

    /// <inheritdoc />
    public LensGraph Lenses { get; }

    /// <inheritdoc />
    public UpcasterChain Upcasters { get; }

    /// <inheritdoc />
    public IEpochCoordinator Epochs { get; }

    /// <inheritdoc />
    public ValueTask<Schema?> GetAsync(SchemaId id, CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        ct.ThrowIfCancellationRequested();
        return _schemas.TryGetValue(id, out var entry)
            ? new ValueTask<Schema?>(entry.Schema)
            : new ValueTask<Schema?>((Schema?)null);
    }

    /// <inheritdoc />
    public async ValueTask<Schema> RegisterAsync(
        string jsonSchemaText,
        IReadOnlyList<SchemaId>? parents = null,
        IReadOnlyList<string>? tags = null,
        int? blobThreshold = null,
        CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        ArgumentNullException.ThrowIfNull(jsonSchemaText);
        ct.ThrowIfCancellationRequested();

        if (blobThreshold.HasValue && blobThreshold.Value <= 0)
        {
            throw new ArgumentException(
                $"blobThreshold must be a positive integer; got {blobThreshold.Value}.",
                nameof(blobThreshold));
        }

        // 1. Canonicalize the schema text before hashing so that two clients
        //    who register logically-equivalent schemas with different
        //    whitespace / key order produce the same CID (federation parity).
        //    INV-S2 (b): bound the parse depth so a pathologically-nested schema
        //    is rejected here (as a JsonException → InvalidSchemaException)
        //    rather than recursing the evaluator at validate-time.
        byte[] canonicalBytes;
        try
        {
            var node = JsonNode.Parse(
                    jsonSchemaText,
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions { MaxDepth = _options.MaxNestingDepth })
                ?? throw new InvalidSchemaException("JSON Schema text parsed to null.");
            canonicalBytes = CanonicalJson.Serialize(node);
        }
        catch (JsonException ex)
        {
            throw new InvalidSchemaException(
                $"JSON Schema text is not valid JSON, or exceeds the configured nesting-depth " +
                $"limit ({_options.MaxNestingDepth}): {ex.Message}", ex);
        }

        // The platform library owns the dialect, the size bound and the schema build (T-303).
        Platform.Schema platformSchema;
        try
        {
            platformSchema = await _validator.RegisterAsync(jsonSchemaText, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Platform.InvalidSchemaException ex)
        {
            throw new InvalidSchemaException(ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}", ex);
        }

        // 2. Content-address the canonical bytes. No blob side-store: the Schema
        //    record carries JsonSchemaText inline and the CID is recomputable from
        //    it, so a blob copy is unreadable dead weight (and, against the node's
        //    envelope-sealed store, a permanently orphaned encrypted file — card
        //    3776).
        var cid = Cid.FromBytes(canonicalBytes);

        // 3. Build the schema record with a self-identifying id of the form
        //    "schema:{cid}". The id embeds the CID so schemas are addressable
        //    by content across federation boundaries without a side lookup.
        var id = new SchemaId($"schema:{cid.Value}");
        var schema = new Schema(
            Id: id,
            JsonSchemaText: jsonSchemaText,
            ParentSchemas: parents ?? Array.Empty<SchemaId>(),
            Migrations: Array.Empty<Migration>(),
            Tags: tags ?? Array.Empty<string>(),
            ContentAddress: cid,
            BlobThreshold: blobThreshold);

        // Register-or-return-existing so the operation is idempotent.
        var entry = _schemas.GetOrAdd(id, _ => new Entry(schema, platformSchema.Id));
        return entry.Schema;
    }

    /// <inheritdoc />
    public async ValueTask<SchemaValidationResult> ValidateAsync(
        SchemaId id,
        ReadOnlyMemory<byte> documentBytes,
        CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        ct.ThrowIfCancellationRequested();

        if (!_schemas.TryGetValue(id, out var entry))
        {
            throw new SchemaNotFoundException(
                $"Schema '{id.Value}' is not registered with this ISchemaRegistry instance.");
        }

        var result = await _validator.ValidateAsync(entry.PlatformId, documentBytes, ct).ConfigureAwait(false);
        return new SchemaValidationResult(
            result.IsValid,
            result.Errors.Select(e => new SchemaValidationError(e.JsonPointer, e.Message, e.Code, e.Params)).ToArray());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Schema> ListAsync(
        string? tagFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A paused iterator must not keep activation waiting while its consumer processes a row.
        // Materialize one stable projection before yielding control back to that consumer.
        Schema[] snapshot;
        using (PackProjectionActivationBarrier.Read(ct))
            snapshot = _schemas.Values.Select(entry => entry.Schema)
                .Where(schema => tagFilter is null || schema.Tags.Contains(tagFilter))
                .ToArray();
        foreach (var schema in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return schema;
        }

        // Satisfy the async-iterator contract without adding real asynchrony —
        // the in-memory dictionary scan is pure sync but the method signature
        // must expose IAsyncEnumerable for interface conformance.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<MigrationPlan> PlanMigrationAsync(SchemaId from, SchemaId to, CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        throw new NotSupportedException(
            "Migration half of ISchemaRegistry is deferred — see gap analysis G2 follow-up.");
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> MigrateAsync(SchemaId from, SchemaId to, ReadOnlyMemory<byte> document, CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        throw new NotSupportedException(
            "Migration half of ISchemaRegistry is deferred — see gap analysis G2 follow-up.");
    }

        /// <summary>Pairs the api <see cref="Schema"/> record with the platform schema that validates it.</summary>
    private sealed record Entry(Schema Schema, Platform.SchemaId PlatformId);
}
