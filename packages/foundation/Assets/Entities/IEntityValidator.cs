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
public sealed class EntityValidationException : Exception
{
    /// <summary>Creates a validation exception with the given message.</summary>
    public EntityValidationException(string message)
        : this(message, "entity.validation.body_invalid", Array.Empty<string>())
    {
    }

    /// <summary>Creates a structured validation refusal without retaining the candidate body.</summary>
    public EntityValidationException(
        string message,
        string reasonCode,
        IReadOnlyList<string>? pointers = null)
        : base(message)
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? throw new ArgumentException("A validation refusal must have a reason code.", nameof(reasonCode))
            : reasonCode;
        Pointers = pointers ?? Array.Empty<string>();
    }

    /// <summary>Creates a validation exception wrapping an inner cause.</summary>
    public EntityValidationException(string message, Exception inner)
        : this(message, "entity.validation.body_invalid", Array.Empty<string>(), inner)
    {
    }

    /// <summary>Creates a structured validation refusal wrapping an inner cause.</summary>
    public EntityValidationException(
        string message,
        string reasonCode,
        IReadOnlyList<string>? pointers,
        Exception inner)
        : base(message, inner)
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? throw new ArgumentException("A validation refusal must have a reason code.", nameof(reasonCode))
            : reasonCode;
        Pointers = pointers ?? Array.Empty<string>();
    }

    /// <summary>Stable machine-readable refusal reason.</summary>
    public string ReasonCode { get; }

    /// <summary>RFC 6901 pointers to the invalid candidate locations.</summary>
    public IReadOnlyList<string> Pointers { get; }
}
