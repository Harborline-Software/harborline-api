using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Corvus.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

using CorvusSchema = Corvus.Json.Validator.JsonSchema;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// The authority-side pre-commit <see cref="IEntityValidator"/> for record writes
/// (ticket 151, ledger row L1418). Resolves the record type's JSON Schema from the
/// ONE schema registry, compiles it with Corvus.Json.Validator, and refuses a
/// non-conforming body with <see cref="EntityValidationException"/> carrying a stable
/// reason code and RFC 6901 pointers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution.</b> A <see cref="SchemaId"/> is either a content-addressed registry id
/// (<c>schema:{cid}</c> — what <see cref="ISchemaRegistry.RegisterAsync"/> mints at pack activation
/// and what the activated record carries, exactly as a form definition carries its
/// <c>SchemaRef</c>) or a logical name for a record type the node itself ships
/// (<c>legal-entity</c> — what <c>EntityRoutes</c> stamps), which resolves through the host's
/// baseline record-type map. An id that resolves to neither is a REFUSAL by name
/// (<see cref="SchemaUnknownReason"/>), never a pass.
/// </para>
/// <para>
/// <b>Compilation happens on first use, not at pack activation</b>, and is cached by the
/// schema's content hash. First use is the only point at which the validator is guaranteed to
/// see every schema source (pack activation, the form-definition builder, a dev seeder); an
/// activation-time hook would have to be threaded onto each of them, and the cache would still
/// need the content-hash key to stay honest. Because the key IS the content hash, re-activating
/// a pack whose record-type schema changed yields a different hash and therefore a freshly
/// compiled validator — a stale validator cannot survive a schema change by construction.
/// </para>
/// <para>
/// ponytail: the first compile of a given schema costs ~0.4 s (~2 s for the first schema in the
/// process, which pays the engine's one-time Roslyn warm-up) and lands on whichever write gets
/// there first; validations thereafter are ~1-3 µs. Pre-compiling the baseline record types in a
/// startup warm-up is the upgrade path if that first-write latency ever matters.
/// </para>
/// </remarks>
public sealed class CompiledEntityValidator : IEntityValidator
{
    /// <summary>Reason code for a write whose schema id resolves to no schema.</summary>
    public const string SchemaUnknownReason = "entity.validation.schema_unknown";

    /// <summary>Reason code for a write whose body does not satisfy its schema.</summary>
    public const string BodyInvalidReason = "entity.validation.body_invalid";

    // allowFileSystemAndHttpResolution: false — a $ref in an installed schema never reaches the
    // file system or the network from the write path.
    private static readonly CorvusSchema.Options CompileOptions = new(null, false, null, false);

    private readonly ISchemaRegistry _registry;
    private readonly IReadOnlyDictionary<SchemaId, string> _baselineSchemas;

    // Keyed by the schema's content hash, so a changed schema is a different entry.
    private readonly ConcurrentDictionary<string, CorvusSchema> _compiled = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the validator over <paramref name="registry"/>. <paramref name="baselineSchemas"/>
    /// maps a host-owned logical record-type id (e.g. <c>legal-entity</c>) to its JSON Schema text
    /// for the record types that ship with the node rather than arriving in a pack.
    /// </summary>
    public CompiledEntityValidator(
        ISchemaRegistry registry,
        IReadOnlyDictionary<SchemaId, string>? baselineSchemas = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _baselineSchemas = baselineSchemas ?? new Dictionary<SchemaId, string>();
    }

    /// <inheritdoc />
    public async Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var (schemaText, contentHash) = await ResolveAsync(schema, ct).ConfigureAwait(false);
        if (schemaText is null)
        {
            throw new EntityValidationException(
                SchemaUnknownReason,
                $"Record type schema '{schema.Value}' is not registered on this node; the write is refused.",
                Array.Empty<string>());
        }

        var compiled = _compiled.GetOrAdd(
            contentHash!,
            static (hash, text) => CorvusSchema.FromText(text, $"harborline:schema/{hash}", CompileOptions, true),
            schemaText);

        var result = compiled.Validate(body.RootElement, ValidationLevel.Detailed);
        if (result.IsValid)
        {
            return;
        }

        var failures = new List<string>();
        var pointers = new List<string>();
        foreach (var entry in result.Results)
        {
            if (entry.Valid || entry.Location is not { } location)
            {
                continue;
            }

            var pointer = Pointer(location.Item3.ToString());
            var keyword = Keyword(location.Item1.ToString());
            pointers.Add(pointer);
            failures.Add($"{keyword} at '{pointer}'");
        }

        if (failures.Count == 0)
        {
            // Fail CLOSED with a named aggregate: an invalid result we could not itemise is
            // still a refusal, never a pass.
            failures.Add("invalid at ''");
            pointers.Add(string.Empty);
        }

        // The message names the failing keyword and pointer only — never the body's values.
        throw new EntityValidationException(
            BodyInvalidReason,
            $"Record body does not satisfy schema '{schema.Value}': {string.Join(", ", failures)}.",
            pointers);
    }

    /// <summary>
    /// Resolves a schema id to its text plus content hash: the registry by id, else the host's
    /// baseline record types. Resolution runs on every validate and is never memoised, so a
    /// changed schema is seen without an invalidation callback; only the expensive part (the
    /// compile) is cached, under the content hash.
    /// </summary>
    private async ValueTask<(string? Text, string? ContentHash)> ResolveAsync(SchemaId id, CancellationToken ct)
    {
        var direct = await _registry.GetAsync(id, ct).ConfigureAwait(false);
        if (direct is not null)
        {
            return (direct.JsonSchemaText, direct.ContentAddress.Value);
        }

        return _baselineSchemas.TryGetValue(id, out var text)
            ? (text, Sha256(text))
            : (null, null);
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Corvus reports a document location as a JSON-Pointer fragment ("#/legalName").</summary>
    private static string Pointer(string documentLocation) =>
        documentLocation.StartsWith('#', StringComparison.Ordinal) ? documentLocation[1..] : documentLocation;

    /// <summary>
    /// The failing keyword is the last non-numeric segment of the schema-relative validation
    /// location ("#/properties/kind/enum" → "enum"; "#/required/0" → "required"). This is the same
    /// stable code vocabulary <see cref="SchemaValidationError.Code"/> already publishes.
    /// </summary>
    private static string Keyword(string validationLocation)
    {
        var segments = validationLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i] != "#" && !segments[i].All(char.IsAsciiDigit))
            {
                return segments[i];
            }
        }

        return "invalid";
    }
}
