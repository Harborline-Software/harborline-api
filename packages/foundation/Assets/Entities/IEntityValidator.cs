using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// Stage two of the write pipeline (ADR 0065 clause 4). The host binds the schema-registry
/// implementation; <see cref="EntityBodyAdmission"/> is its only production caller.
/// </summary>
public interface IEntityValidator
{
    /// <summary>
    /// Validates a body about to be committed. Throws <see cref="EntityValidationException"/>
    /// on failure; returns normally on success.
    /// </summary>
    Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default);
}

/// <summary>Raised when <see cref="IEntityValidator.ValidateAsync"/> rejects a body.</summary>
/// <remarks>
/// Carries a stable reason code and the RFC 6901 pointers that failed, so an operator surface can
/// say why without the body ever being echoed back.
/// </remarks>
public sealed class EntityValidationException : Exception
{
    /// <summary>The schema named by the write could not be resolved.</summary>
    public const string SchemaUnknown = "entity.validation.schema_unknown";

    /// <summary>The body did not satisfy the schema.</summary>
    public const string BodyInvalid = "entity.validation.body_invalid";

    /// <summary>Creates a validation exception with the given reason code, message and pointers.</summary>
    public EntityValidationException(string reasonCode, string message, IReadOnlyList<string>? pointers = null)
        : base(message)
    {
        ReasonCode = reasonCode;
        Pointers = pointers ?? [];
    }

    /// <summary>Stable reason code — <see cref="SchemaUnknown"/> or <see cref="BodyInvalid"/>.</summary>
    public string ReasonCode { get; }

    /// <summary>RFC 6901 pointers into the failing body nodes; empty for a schema-level refusal.</summary>
    public IReadOnlyList<string> Pointers { get; }
}
