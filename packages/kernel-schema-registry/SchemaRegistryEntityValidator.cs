using System.Collections.Concurrent;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// The real pre-commit <see cref="IEntityValidator"/> (ticket 151, L1418): stage two of the write
/// pipeline — the gate decides, THIS validates, and only then does the body reach persistence
/// (ADR 0065 clause 4). It replaces <c>NullEntityValidator</c>, the always-accepting null object
/// that made device-local validation the only validation a record write ever met.
/// </summary>
/// <remarks>
/// <para>
/// No new validation vocabulary and no new dependency: the authority's JSON Schema engine is
/// <see cref="ISchemaRegistry"/> (JsonSchema.Net, draft 2020-12, already pinned), and its
/// <see cref="SchemaValidationError"/> rows already carry the stable keyword code and the RFC 6901
/// pointer the forms engine surfaces. This type is the adapter from that result onto
/// <see cref="EntityValidationException"/>; it derives nothing of its own.
/// </para>
/// <para>
/// <b>Unknown schema refuses.</b> A <see cref="SchemaId"/> this validator cannot resolve to a
/// registered schema is an <see cref="EntityValidationReasons.SchemaUnknown"/> refusal, never a pass:
/// a write admitted against a schema nobody holds is exactly the hole the null object left. A schema
/// that declares no constraints still admits any well-formed body — that is the schema's decision,
/// made by the authority, which is the point.
/// </para>
/// <para>
/// Two kinds of id resolve. A registry-issued id (<c>schema:{cid}</c>, e.g. a pack-activated Records
/// type's form schema) is read straight from the registry. A host-declared records schema is supplied
/// by name in <paramref name="recordsSchemas"/> and registered on first use —
/// <see cref="ISchemaRegistry.RegisterAsync"/> is content-addressed and idempotent, so the
/// registration needs no boot ordering and repeats are free.
/// </para>
/// </remarks>
/// <param name="registry">The authority's schema registry.</param>
/// <param name="recordsSchemas">
/// Host-declared records schemas as <c>schemaId → JSON Schema 2020-12 text</c>. Empty is legal: a
/// host that only writes pack-activated types resolves every id from the registry directly.
/// </param>
public sealed class SchemaRegistryEntityValidator(
    ISchemaRegistry registry,
    IReadOnlyDictionary<string, string> recordsSchemas) : IEntityValidator
{
    // Only successful resolutions are remembered (a re-registration is idempotent, so a race is free);
    // a refusal is never cached, so a schema registered later is picked up on the next write.
    private readonly ConcurrentDictionary<string, SchemaId> _resolved = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!_resolved.TryGetValue(schema.Value, out var resolved))
        {
            if (await ResolveAsync(schema.Value, ct).ConfigureAwait(false) is not { } found)
            {
                // Do not echo the body: a refusal says which schema could not be resolved, nothing more.
                throw new EntityValidationException(
                    EntityValidationReasons.SchemaUnknown,
                    $"Schema '{schema.Value}' is not registered with the authority; the write is refused.",
                    Array.Empty<string>());
            }

            resolved = _resolved.GetOrAdd(schema.Value, found);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(body.RootElement);
        var result = await registry.ValidateAsync(resolved, bytes, ct).ConfigureAwait(false);
        if (result.IsValid)
        {
            return;
        }

        var pointers = result.Errors
            .Select(error => error.JsonPointer)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // The message names the failing keywords and locations; it never quotes a value from the body.
        var detail = string.Join(
            "; ",
            result.Errors.Select(error =>
                $"{(error.JsonPointer.Length == 0 ? "(root)" : error.JsonPointer)}: {error.Code ?? "invalid"}"));
        throw new EntityValidationException(
            EntityValidationReasons.BodyInvalid,
            $"The body does not satisfy schema '{schema.Value}' — {detail}.",
            pointers);
    }

    private async Task<SchemaId?> ResolveAsync(string name, CancellationToken ct)
    {
        var registered = await registry.GetAsync(new SchemaId(name), ct).ConfigureAwait(false);
        if (registered is not null)
        {
            return registered.Id;
        }

        if (recordsSchemas.TryGetValue(name, out var schemaText))
        {
            var minted = await registry.RegisterAsync(schemaText, ct: ct).ConfigureAwait(false);
            return minted.Id;
        }

        return null;
    }
}
