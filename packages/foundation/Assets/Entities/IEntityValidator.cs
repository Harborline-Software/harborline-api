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
    /// <summary>Stable machine-readable refusal reason.</summary>
    public string Code { get; }

    /// <summary>RFC 6901 pointers to the rejected body locations. Never carries body content.</summary>
    public IReadOnlyList<string> Pointers { get; }

    /// <summary>Creates a validation exception with the given message.</summary>
    public EntityValidationException(string message)
        : this("entity.validation.body_invalid", Array.Empty<string>(), message) { }

    /// <summary>Creates a validation exception wrapping an inner cause.</summary>
    public EntityValidationException(string message, Exception inner)
        : this("entity.validation.body_invalid", Array.Empty<string>(), message, inner) { }

    /// <summary>Creates a structured validation refusal without exposing the submitted body.</summary>
    public EntityValidationException(string code, IReadOnlyList<string> pointers, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("A validation refusal code is required.", nameof(code))
            : code;
        Pointers = pointers ?? throw new ArgumentNullException(nameof(pointers));
    }
}
