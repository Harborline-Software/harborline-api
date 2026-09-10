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
    /// <summary>Stable, locale-independent refusal code.</summary>
    public string ReasonCode { get; }

    /// <summary>RFC 6901 locations of the rejected values.</summary>
    public IReadOnlyList<string> Pointers { get; }

    /// <summary>Creates a validation exception with the given message.</summary>
    public EntityValidationException(string message)
        : this("entity.validation.body_invalid", Array.Empty<string>(), message)
    {
    }

    /// <summary>Creates a validation exception with a stable code and JSON pointers.</summary>
    public EntityValidationException(string reasonCode, IReadOnlyList<string> pointers, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
        Pointers = pointers;
    }

    /// <summary>Creates a validation exception wrapping an inner cause.</summary>
    public EntityValidationException(string message, Exception inner)
        : base(message, inner)
    {
        ReasonCode = "entity.validation.body_invalid";
        Pointers = Array.Empty<string>();
    }
}
