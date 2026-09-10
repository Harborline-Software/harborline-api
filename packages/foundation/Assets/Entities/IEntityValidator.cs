using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// Pre-commit validation hook. Populated by the schema registry in a later phase
/// (spec §3.4). Phase A ships a null-object default via <see cref="NullEntityValidator"/>.
/// </summary>
public interface IEntityValidator
{
    /// <summary>
    /// Validates a body about to be committed. Throws <see cref="EntityValidationException"/>
    /// on failure; returns normally on success.
    /// </summary>
    Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default);
}

/// <summary>Stable refusal reasons carried by <see cref="EntityValidationException.ReasonCode"/>.</summary>
public static class EntityValidationReasons
{
    /// <summary>The body's schema is not resolvable — a write is never admitted against a schema nobody holds.</summary>
    public const string SchemaUnknown = "entity.validation.schema_unknown";

    /// <summary>The body does not satisfy its schema.</summary>
    public const string BodyInvalid = "entity.validation.body_invalid";
}

/// <summary>Raised when <see cref="IEntityValidator.ValidateAsync"/> rejects a body.</summary>
/// <remarks>
/// Carries a named <see cref="ReasonCode"/> and the failing <see cref="Pointers"/> (RFC 6901) so an
/// operator surface and the trace can say WHY a write was refused without echoing the body.
/// </remarks>
public sealed class EntityValidationException : Exception
{
    /// <summary>Creates a validation exception with the given message.</summary>
    public EntityValidationException(string message) : base(message) { }

    /// <summary>Creates a validation exception wrapping an inner cause.</summary>
    public EntityValidationException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates a named refusal with the failing JSON pointers.</summary>
    public EntityValidationException(string reasonCode, string message, IReadOnlyList<string> pointers)
        : base(message)
    {
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        Pointers = pointers ?? Array.Empty<string>();
    }

    /// <summary>The stable refusal reason (see <see cref="EntityValidationReasons"/>).</summary>
    public string ReasonCode { get; } = EntityValidationReasons.BodyInvalid;

    /// <summary>RFC 6901 pointers into the failing body nodes; empty when the whole body is at fault.</summary>
    public IReadOnlyList<string> Pointers { get; } = Array.Empty<string>();
}
