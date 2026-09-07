using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>Null-object validator that accepts every body. Phase A default.</summary>
public sealed class NullEntityValidator : IEntityValidator
{
    /// <summary>Singleton instance.</summary>
    public static NullEntityValidator Instance { get; } = new();

    /// <inheritdoc />
    public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        => Task.CompletedTask;
}
