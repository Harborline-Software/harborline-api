using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// The shipped <see cref="IEntityValidator"/>: resolves the record type's schema from
/// <see cref="ISchemaRegistry"/> and validates the body against it with the registry's own
/// JSON Schema draft 2020-12 engine (ticket 151, ledger L1418 — replaces the always-accepting
/// null object).
/// </summary>
/// <remarks>
/// An unknown <see cref="SchemaId"/> is a refusal, never a pass. A schema that declares no
/// constraints still accepts any well-formed body — that is the schema author's choice, not a
/// hole in the pipeline.
/// </remarks>
public sealed class SchemaRegistryEntityValidator(ISchemaRegistry registry) : IEntityValidator
{
    private readonly ISchemaRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public async Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (await _registry.GetAsync(schema, ct).ConfigureAwait(false) is null)
        {
            throw new EntityValidationException(
                EntityValidationException.SchemaUnknown,
                $"No schema is registered under '{schema}'; the write is refused rather than accepted unvalidated.");
        }

        var bytes = Encoding.UTF8.GetBytes(body.RootElement.GetRawText());
        var result = await _registry.ValidateAsync(schema, bytes, ct).ConfigureAwait(false);
        if (result.IsValid)
            return;

        // Pointers and keyword codes only — the body is never echoed into the refusal.
        var pointers = result.Errors
            .Select(e => string.IsNullOrEmpty(e.JsonPointer) ? "/" : e.JsonPointer)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var detail = string.Join(
            "; ",
            result.Errors.Select(e =>
                $"{(string.IsNullOrEmpty(e.JsonPointer) ? "/" : e.JsonPointer)} failed {e.Code ?? "constraint"}"));
        throw new EntityValidationException(
            EntityValidationException.BodyInvalid,
            $"Body does not satisfy schema '{schema}': {detail}.",
            pointers);
    }
}
