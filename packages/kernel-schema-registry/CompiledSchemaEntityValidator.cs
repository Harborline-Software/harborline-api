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
            throw new EntityValidationException(
                $"No compiled validator is activated for schema '{schema.Value}'.",
                SchemaUnknown,
                [string.Empty]);
        }

        var result = await registry.ValidateAsync(compiled, Utf8(body), ct).ConfigureAwait(false);
        if (result.IsValid)
        {
            return;
        }

        var pointers = result.Errors
            .Select(error => error.JsonPointer)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Codes and pointers only — a refusal never echoes the body it refused.
        throw new EntityValidationException(
            $"The body does not satisfy schema '{schema.Value}': "
            + string.Join(
                "; ",
                result.Errors.Select(error =>
                    $"{(error.JsonPointer.Length == 0 ? "/" : error.JsonPointer)} failed '{error.Code ?? "schema"}'")),
            BodyInvalid,
            pointers);
    }

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
