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

/// <summary>Raised when <see cref="IEntityValidator.ValidateAsync"/> rejects a body.</summary>
/// <remarks>
/// Ticket 151: a refusal is legible without the body. <see cref="ReasonCode"/> is the stable,
/// locale-independent cause (e.g. <c>entity.validation.schema_unknown</c> /
/// <c>entity.validation.body_invalid</c>) and <see cref="Pointers"/> locates each failing node as
/// an RFC 6901 JSON Pointer, so the operator CLI and the trace can say WHY without echoing what
/// the caller sent.
/// </remarks>
public sealed class EntityValidationException : Exception
{
    /// <summary>Creates a validation exception with the given message and no reason code.</summary>
    public EntityValidationException(string message) : base(message) { }

    /// <summary>Creates a validation exception wrapping an inner cause.</summary>
    public EntityValidationException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates a named refusal carrying the failing JSON Pointers.</summary>
    public EntityValidationException(string reasonCode, string message, IReadOnlyList<string> pointers)
        : base(message)
    {
        ReasonCode = reasonCode;
        Pointers = pointers ?? Array.Empty<string>();
    }

    /// <summary>Stable machine-readable cause; null for a legacy unnamed refusal.</summary>
    public string? ReasonCode { get; }

    /// <summary>RFC 6901 pointers into the failing nodes (empty when the cause is not body-local).</summary>
    public IReadOnlyList<string> Pointers { get; } = Array.Empty<string>();
}
