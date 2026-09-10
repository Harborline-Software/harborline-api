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
/// A refusal is legible without the body: <see cref="ReasonCode"/> names WHY (a stable,
/// locale-independent code the operator CLI and the trace key off) and <see cref="Pointers"/>
/// locates WHERE (RFC 6901 JSON pointers into the rejected body). Neither carries body values.
/// </remarks>
public sealed class EntityValidationException : Exception
{
    /// <summary>Reason code used when a caller does not name one.</summary>
    public const string UnnamedReason = "entity.validation.body_invalid";

    /// <summary>Creates a validation exception with the given message.</summary>
    public EntityValidationException(string message) : this(message, UnnamedReason, []) { }

    /// <summary>Creates a validation exception wrapping an inner cause.</summary>
    public EntityValidationException(string message, Exception inner)
        : base(message, inner)
    {
        ReasonCode = UnnamedReason;
        Pointers = [];
    }

    /// <summary>Creates a named refusal carrying the failing JSON pointers.</summary>
    public EntityValidationException(string message, string reasonCode, IReadOnlyList<string> pointers)
        : base(message)
    {
        ReasonCode = reasonCode;
        Pointers = pointers;
    }

    /// <summary>Stable refusal code, e.g. <c>entity.validation.schema_unknown</c>.</summary>
    public string ReasonCode { get; }

    /// <summary>RFC 6901 pointers into the rejected body (empty string = document root).</summary>
    public IReadOnlyList<string> Pointers { get; }
}
