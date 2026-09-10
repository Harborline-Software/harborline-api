using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;

// This file lives in the TEST assembly only. It is the tests' own fixture minting path for the
// ticket-151 write token: production code can obtain a ValidatedBody only from
// EntityBodyAdmission, and only after the gate's allowed decision. A test that is exercising
// something other than validation says so by writing through these helpers.
namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>Test-only validator that accepts every body (the old NullEntityValidator, moved here).</summary>
internal sealed class AcceptingEntityValidator : IEntityValidator
{
    internal static readonly AcceptingEntityValidator Instance = new();

    public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Test-only stand-in for an allowed gate decision.</summary>
internal sealed class TestWriteAdmission : IWriteAdmission
{
    internal static readonly TestWriteAdmission Allowed = new(true);
    internal static readonly TestWriteAdmission Denied = new(false);

    private TestWriteAdmission(bool allowed) => IsAllowed = allowed;

    public bool IsAllowed { get; }
}

/// <summary>The tests' minting path.</summary>
internal static class TestEntityWritePipeline
{
    /// <summary>An admission over a validator that accepts everything.</summary>
    internal static readonly EntityBodyAdmission Accepting = new(AcceptingEntityValidator.Instance);

    /// <summary>Mints a token without exercising validation — for tests about something else.</summary>
    internal static ValidatedBody Validated(SchemaId schema, JsonDocument body) =>
        Accepting.AdmitAsync(TestWriteAdmission.Allowed, schema, body).GetAwaiter().GetResult();
}

/// <summary>
/// Restores the pre-token call shapes for existing tests. Resolved only when no instance overload
/// matches, so a test that already carries a token still binds to the real store method.
/// </summary>
internal static class TestOnlyEntityStoreWriteExtensions
{
    internal static Task<EntityId> CreateAsync(
        this IEntityMutationStore store,
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        CancellationToken ct = default) =>
        store.CreateAsync(TestEntityWritePipeline.Validated(schema, body), options, ct);

    internal static async Task<VersionId> UpdateAsync(
        this IEntityMutationStore store,
        EntityId id,
        JsonDocument body,
        UpdateOptions options,
        CancellationToken ct = default)
    {
        var stored = await store.GetAsync(id, default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Entity '{id}' not found.");
        return await store.UpdateAsync(id, TestEntityWritePipeline.Validated(stored.Schema, body), options, ct)
            .ConfigureAwait(false);
    }
}
