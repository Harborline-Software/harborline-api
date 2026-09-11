using System.Buffers;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Ticket 151 (L1418) — the real pre-commit entity validator: stage two of the write pipeline
/// (ADR 0065 clause 4 — gate, then validation, then persistence). It replaces
/// <see cref="NullEntityValidator"/> as the composed default.
/// </summary>
/// <remarks>
/// It NEVER compiles a schema. A body is evaluated against the artefact an activation already
/// compiled (see <see cref="CompiledSchemaCatalog"/>); a schema with no compiled artefact is a
/// refusal by name, not a pass. A schema that declares nothing still accepts a well-formed body.
/// </remarks>
public sealed class CompiledSchemaEntityValidator(
    ISchemaRegistry registry,
    CompiledSchemaCatalog catalog) : IEntityValidator
{
    /// <summary>
    /// The DI key the RECORD write coordinators ask for. A record coordinator takes its validator
    /// under this key, never from the unkeyed <see cref="IEntityValidator"/> slot: that slot is the
    /// asset store's pre-commit hook, which also fires for definition ENVELOPES and form INSTANCES
    /// whose bodies a record-type schema must not judge, and it keeps its null-object default. A
    /// composition that forgets this key fails to construct the coordinator instead of silently
    /// handing it an always-accepting null object (ticket 151, L1418).
    /// </summary>
    // holds RW-7 · closes RW-H8: the key the record coordinators resolve; a composition that omits it
    // cannot construct them, so no composition silently resolves the always-accepting stand-in.
    public const string RecordWriteKey = "harborline.records.write";

    /// <summary>No activation has compiled a validator for this schema id.</summary>
    public const string SchemaUnknown = "entity.validation.schema_unknown";

    /// <summary>The body failed the compiled schema.</summary>
    public const string BodyInvalid = "entity.validation.body_invalid";

    /// <inheritdoc />
    public async Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        SchemaId compiled;
        if (catalog.TryGet(schema, out var activated))
        {
            compiled = activated.CompiledSchemaId;
        }
        else if (await registry.GetAsync(schema, ct).ConfigureAwait(false) is not null)
        {
            // A content-addressed id the caller carries directly (a pack-activated form's SchemaRef).
            // Registration compiled it, at activation, in the registry — still no compile here.
            compiled = schema;
        }
        else
        {
            throw UnknownSchema(schema);
        }

        SchemaValidationResult result;
        try
        {
            result = await registry.ValidateAsync(compiled, Utf8(body), ct).ConfigureAwait(false);
        }
        catch (SchemaNotFoundException)
        {
            // RW-3: a catalog binding can outlive a registry artefact (for example after a registry
            // restart); treat that missing compiled artefact as the same named refusal as no binding.
            throw UnknownSchema(schema);
        }

        if (result.IsValid)
        {
            return;
        }

        var pointers = result.Errors
            .Select(error => error.JsonPointer)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // holds RW-2, RW-4 — the body failed its activated schema: refuse before persistence, with the
        // code and the RFC 6901 pointers. closes RW-H1, RW-H6: codes and pointers only, never the body.
        throw new EntityValidationException(
            $"The body does not satisfy schema '{schema.Value}': "
            + string.Join(
                "; ",
                result.Errors.Select(error =>
                    $"{(error.JsonPointer.Length == 0 ? "/" : error.JsonPointer)} failed '{error.Code ?? "schema"}'")),
            BodyInvalid,
            pointers);
    }

    // holds RW-3 — an unknown, missing or unresolvable schema is a named refusal, never a pass.
    // closes RW-H3: a mis-registered record type cannot persist unchecked.
    private static EntityValidationException UnknownSchema(SchemaId schema) =>
        new(
            $"No compiled validator is activated for schema '{schema.Value}'.",
            SchemaUnknown,
            [string.Empty]);

    private static ReadOnlyMemory<byte> Utf8(JsonDocument body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            body.WriteTo(writer);
        }

        return buffer.WrittenMemory;
    }
}
