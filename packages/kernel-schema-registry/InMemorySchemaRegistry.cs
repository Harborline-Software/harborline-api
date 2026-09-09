using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Json.Schema;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.SchemaRegistry.Epochs;
using Harborline.Api.Kernel.SchemaRegistry.Lenses;
using Harborline.Api.Kernel.SchemaRegistry.Upcasters;

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
/// Validation uses JsonSchema.Net (<c>Json.Schema</c> namespace) in
/// draft 2020-12 mode. The parsed <see cref="JsonSchema"/> is cached alongside
/// the <see cref="Schema"/> record to avoid re-parsing on every
/// <see cref="ValidateAsync"/> call.
/// </para>
/// </remarks>
public sealed class InMemorySchemaRegistry : ISchemaRegistry
{
    private readonly SchemaRegistryOptions _options;
    private readonly ConcurrentDictionary<SchemaId, Entry> _schemas = new();

    /// <summary>
    /// Creates a new <see cref="InMemorySchemaRegistry"/>. The migration surface
    /// (lenses, upcasters, epochs) is initialized with empty in-memory stores;
    /// callers mutate them via the public <see cref="Lenses"/> /
    /// <see cref="Upcasters"/> / <see cref="Epochs"/> members.
    /// <paramref name="options"/> tunes the INV-S2 register-time resource bounds;
    /// <c>null</c> uses <see cref="SchemaRegistryOptions.Default"/>.
    /// </summary>
    public InMemorySchemaRegistry(TimeProvider timeProvider, SchemaRegistryOptions? options = null)
        : this(new LensGraph(), new UpcasterChain(), new EpochCoordinator(timeProvider), options)
    {
    }

    /// <summary>
    /// Creates a new <see cref="InMemorySchemaRegistry"/> with explicitly-supplied
    /// lens graph / upcaster chain / epoch coordinator. Intended for DI paths where
    /// the migration primitives are registered as separate singletons so downstream
    /// components can depend on them directly. <paramref name="options"/> tunes the
    /// INV-S2 register-time resource bounds; <c>null</c> uses
    /// <see cref="SchemaRegistryOptions.Default"/>.
    /// </summary>
    public InMemorySchemaRegistry(
        LensGraph lenses,
        UpcasterChain upcasters,
        IEpochCoordinator epochs,
        SchemaRegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lenses);
        ArgumentNullException.ThrowIfNull(upcasters);
        ArgumentNullException.ThrowIfNull(epochs);
        _options = options ?? SchemaRegistryOptions.Default;
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
        ct.ThrowIfCancellationRequested();
        return _schemas.TryGetValue(id, out var entry)
            ? new ValueTask<Schema?>(entry.Schema)
            : new ValueTask<Schema?>((Schema?)null);
    }

    /// <inheritdoc />
    public ValueTask<Schema> RegisterAsync(
        string jsonSchemaText,
        IReadOnlyList<SchemaId>? parents = null,
        IReadOnlyList<string>? tags = null,
        int? blobThreshold = null,
        CancellationToken ct = default)
    {
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

        // INV-S2 (a): reject an over-large schema before the (more expensive)
        // JsonSchema.Net parse — a cheap, deterministic register-time pre-reject.
        if (canonicalBytes.Length > _options.MaxSchemaBytes)
        {
            throw new InvalidSchemaException(
                $"JSON Schema is {canonicalBytes.Length} canonical bytes, exceeding the " +
                $"{_options.MaxSchemaBytes}-byte register-time limit (ADR 0055 INV-S2).");
        }

        // 2. Parse + validate with JsonSchema.Net so we fail at register-time
        //    on malformed schema documents rather than on the first payload.
        //    INV-S2 (c): build against SchemaRegistryDialect.TimedBuildOptions so the
        //    schema's `pattern` regexes are compiled by TimedPatternKeyword with an
        //    explicit match-timeout — the ReDoS control. A schema declaring a dialect
        //    other than draft 2020-12 fails the build and surfaces fail-closed below.
        JsonSchema parsedSchema;
        try
        {
            parsedSchema = JsonSchema.FromText(
                jsonSchemaText, SchemaRegistryDialect.TimedBuildOptions, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidSchemaException(
                $"JSON Schema text is not a valid JSON Schema: {ex.Message}", ex);
        }

        // 3. Content-address the canonical bytes. No blob side-store: the Schema
        //    record carries JsonSchemaText inline and the CID is recomputable from
        //    it, so a blob copy is unreadable dead weight (and, against the node's
        //    envelope-sealed store, a permanently orphaned encrypted file — card
        //    3776).
        var cid = Cid.FromBytes(canonicalBytes);

        // 4. Build the schema record with a self-identifying id of the form
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
        var entry = _schemas.GetOrAdd(id, _ => new Entry(schema, parsedSchema));
        return ValueTask.FromResult(entry.Schema);
    }

    /// <inheritdoc />
    public ValueTask<SchemaValidationResult> ValidateAsync(
        SchemaId id,
        ReadOnlyMemory<byte> documentBytes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_schemas.TryGetValue(id, out var entry))
        {
            throw new SchemaNotFoundException(
                $"Schema '{id.Value}' is not registered with this ISchemaRegistry instance.");
        }

        using var document = JsonDocument.Parse(documentBytes);

        // OutputFormat.List gives us a flat list of sub-results, each with its
        // own InstanceLocation (JSON Pointer) — exactly the shape we map to
        // SchemaValidationError. Hierarchical would nest; Flag would collapse
        // to a single pass/fail bit.
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        };

        EvaluationResults results;
        try
        {
            results = entry.ParsedSchema.Evaluate(document.RootElement, options);
        }
        catch (RegexMatchTimeoutException)
        {
            // INV-S2 (c): a schema `pattern` exceeded the per-pattern match-timeout that
            // TimedPatternKeyword compiled into its regex (see SchemaRegistryDialect).
            // Fail CLOSED — a validation we could not complete is never reported valid.
            return new ValueTask<SchemaValidationResult>(
                new SchemaValidationResult(false, new[]
                {
                    new SchemaValidationError(
                        JsonPointer: string.Empty,
                        Message: "Validation aborted: a schema `pattern` exceeded the regex "
                            + "match-timeout budget (possible ReDoS).",
                        Code: "pattern-timeout"),
                }));
        }

        if (results.IsValid)
        {
            return new ValueTask<SchemaValidationResult>(
                new SchemaValidationResult(true, Array.Empty<SchemaValidationError>()));
        }

        // Parse the schema JSON once so the walk can resolve a failing keyword's structured
        // constraint value (e.g. minimum: 1) by its EvaluationPath. Best-effort: a parse miss
        // simply leaves Params null (the code + English message still localize the error).
        JsonNode? schemaNode = TryParseSchemaNode(entry.Schema.JsonSchemaText);

        var errors = CollectErrors(results, schemaNode);
        return new ValueTask<SchemaValidationResult>(
            new SchemaValidationResult(false, errors));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Schema> ListAsync(
        string? tagFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in _schemas.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (tagFilter is null || entry.Schema.Tags.Contains(tagFilter))
            {
                yield return entry.Schema;
            }
        }

        // Satisfy the async-iterator contract without adding real asynchrony —
        // the in-memory dictionary scan is pure sync but the method signature
        // must expose IAsyncEnumerable for interface conformance.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<MigrationPlan> PlanMigrationAsync(SchemaId from, SchemaId to, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Migration half of ISchemaRegistry is deferred — see gap analysis G2 follow-up.");
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> MigrateAsync(SchemaId from, SchemaId to, ReadOnlyMemory<byte> document, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Migration half of ISchemaRegistry is deferred — see gap analysis G2 follow-up.");
    }

    /// <summary>
    /// Flattens a JsonSchema.Net <see cref="EvaluationResults"/> tree into the
    /// Harborline <see cref="SchemaValidationError"/> list. The List-format result
    /// has a top-level <see cref="EvaluationResults.Details"/> collection; we
    /// walk it and emit one Harborline error per keyword entry in the node's
    /// <see cref="EvaluationResults.Errors"/> dictionary, attaching the failing
    /// keyword as the stable <see cref="SchemaValidationError.Code"/> plus any
    /// structured constraint values as <see cref="SchemaValidationError.Params"/>
    /// (resolved from <paramref name="schemaNode"/> by the leaf's evaluation path),
    /// so a client can localize the error instead of parsing English.
    /// </summary>
    private static IReadOnlyList<SchemaValidationError> CollectErrors(EvaluationResults results, JsonNode? schemaNode)
    {
        var collected = new List<SchemaValidationError>();
        Walk(results, schemaNode, collected);
        if (collected.Count == 0)
        {
            // Defensive: IsValid was false but we found no errored leaves.
            // Surface a single aggregate error rather than silently returning
            // an empty list on an invalid result.
            collected.Add(new SchemaValidationError(
                JsonPointer: results.InstanceLocation.ToString(),
                Message: "Validation failed (no keyword-level details reported).",
                Code: "invalid"));
        }
        return collected;
    }

    private static void Walk(EvaluationResults node, JsonNode? schemaNode, List<SchemaValidationError> sink)
    {
        // Errors is a Dictionary<string, string>? on EvaluationResults —
        // keyed by the failing keyword (e.g. "type", "required", "minimum")
        // and valued with a human-readable message produced by the validator.
        if (node.Errors is { Count: > 0 } keywordErrors)
        {
            var pointer = node.InstanceLocation.ToString();
            var evalPath = node.EvaluationPath.ToString();
            foreach (var kvp in keywordErrors)
            {
                EmitKeywordError(kvp.Key, kvp.Value, pointer, evalPath, schemaNode, sink);
            }
        }

        // Details is nullable on EvaluationResults — walk only when present.
        if (node.Details is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                Walk(child, schemaNode, sink);
            }
        }
    }

    /// <summary>
    /// Projects one JsonSchema.Net keyword failure onto one or more
    /// <see cref="SchemaValidationError"/>s with a stable <c>Code</c> + structured
    /// <c>Params</c>. The English <c>Message</c> keeps the historic
    /// <c>"{keyword}: {detail}"</c> shape (the engine/UI fallback). A
    /// <c>required</c> failure is SPLIT into one per-field error located at the
    /// missing field's pointer, so a per-field renderer can attach it (the
    /// validator reports a single object-level <c>required</c> error naming all
    /// missing properties).
    /// </summary>
    private static void EmitKeywordError(
        string keyword,
        string detail,
        string instancePointer,
        string evaluationPath,
        JsonNode? schemaNode,
        List<SchemaValidationError> sink)
    {
        // An empty keyword key is the boolean-false-schema rejection (e.g. an
        // `additionalProperties: false` violation, which the validator reports
        // with no keyword name). Give it a stable code rather than dropping it.
        var code = string.IsNullOrEmpty(keyword) ? "additional-properties" : keyword;
        var message = string.IsNullOrEmpty(keyword) ? detail : $"{keyword}: {detail}";

        // The keyword's declared value in the schema, looked up at the leaf's
        // evaluation path (e.g. /properties/conditionRating + "minimum" → 1).
        var keywordValue = ResolveKeywordValue(schemaNode, evaluationPath, keyword);

        // `required` reports ONE object-level error naming every missing property.
        // Split it into one per-field error at each missing field's pointer so a
        // per-field UI can bind it; the field name rides in `Params["field"]`.
        if (code == "required" && keywordValue is JsonArray requiredArr)
        {
            var missing = ExtractMissingRequiredNames(detail);
            foreach (var item in requiredArr)
            {
                var name = item?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                // The validator only lists the ABSENT ones in the detail; emit a
                // per-field error only for those genuinely missing.
                if (missing.Count > 0 && !missing.Contains(name)) continue;
                var fieldPointer = instancePointer.Length == 0 ? $"/{EncodePointerSegment(name)}" : $"{instancePointer}/{EncodePointerSegment(name)}";
                sink.Add(new SchemaValidationError(
                    JsonPointer: fieldPointer,
                    Message: $"required: '{name}' is required.",
                    Code: "required",
                    Params: new Dictionary<string, string> { ["field"] = name }));
            }
            return;
        }

        var parameters = BuildParams(code, keywordValue);
        sink.Add(new SchemaValidationError(
            JsonPointer: instancePointer,
            Message: message,
            Code: code,
            Params: parameters));
    }

    /// <summary>
    /// Builds the structured-params map for a keyword from its declared schema value.
    /// Numeric/length bounds expose <c>min</c>/<c>max</c>; <c>enum</c> exposes the JSON
    /// <c>allowed</c> array; <c>pattern</c> exposes the <c>pattern</c> text; others carry no
    /// param (null). The client interpolates these into a localized template.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BuildParams(string code, JsonNode? keywordValue)
    {
        if (keywordValue is null) return null;
        var value = keywordValue.ToJsonString().Trim('"');
        return code switch
        {
            "minimum" or "exclusiveMinimum" or "minLength" or "minItems" or "minProperties"
                => new Dictionary<string, string> { ["min"] = value },
            "maximum" or "exclusiveMaximum" or "maxLength" or "maxItems" or "maxProperties"
                => new Dictionary<string, string> { ["max"] = value },
            "multipleOf"
                => new Dictionary<string, string> { ["multiple"] = value },
            "enum" or "const"
                => new Dictionary<string, string> { ["allowed"] = keywordValue.ToJsonString() },
            "pattern"
                => new Dictionary<string, string> { ["pattern"] = value },
            "format"
                => new Dictionary<string, string> { ["format"] = value },
            _ => null,
        };
    }

    /// <summary>
    /// Resolves a failing keyword's declared value out of the schema document by the
    /// leaf's <paramref name="evaluationPath"/> (a JSON Pointer into the schema, e.g.
    /// <c>/properties/conditionRating</c>). The keyword value sits at
    /// <c>evaluationPath + "/" + keyword</c>. Best-effort: returns null on any miss.
    /// </summary>
    private static JsonNode? ResolveKeywordValue(JsonNode? schemaNode, string evaluationPath, string keyword)
    {
        if (schemaNode is null || string.IsNullOrEmpty(keyword)) return null;
        var subSchema = ResolvePointer(schemaNode, evaluationPath);
        if (subSchema is JsonObject obj && obj.TryGetPropertyValue(keyword, out var value))
        {
            return value;
        }
        return null;
    }

    /// <summary>Walks a JSON Pointer (RFC 6901) into a node; returns null on any miss.</summary>
    private static JsonNode? ResolvePointer(JsonNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer)) return root;
        JsonNode? current = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
            {
                current = next;
            }
            else if (current is JsonArray arr && int.TryParse(segment, out var index) && index >= 0 && index < arr.Count)
            {
                current = arr[index];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>Encodes a property name into one RFC-6901 JSON-Pointer segment.</summary>
    private static string EncodePointerSegment(string name)
        => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// Extracts the property names the validator listed as absent in a `required` detail
    /// message (e.g. <c>Required properties ["assetId","inspectedOn"] are not present</c>).
    /// Returns an empty set when none can be parsed — the caller then treats every declared
    /// required name as a candidate (a safe over-approximation that the validator wouldn't
    /// have raised at all if nothing was missing).
    /// </summary>
    private static HashSet<string> ExtractMissingRequiredNames(string detail)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var start = detail.IndexOf('[', StringComparison.Ordinal);
        var end = detail.IndexOf(']', StringComparison.Ordinal);
        if (start < 0 || end < start) return names;
        var inner = detail.Substring(start, end - start + 1);
        try
        {
            if (JsonNode.Parse(inner) is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var name = item?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable detail — leave the set empty (over-approximate, see summary).
        }
        return names;
    }

    /// <summary>Best-effort parse of the schema JSON text into a node for keyword-value lookup.</summary>
    private static JsonNode? TryParseSchemaNode(string schemaText)
    {
        try
        {
            return JsonNode.Parse(schemaText);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pairs a <see cref="Schema"/> with its parsed <see cref="JsonSchema"/> for validation reuse.</summary>
    private sealed record Entry(Schema Schema, JsonSchema ParsedSchema);
}
