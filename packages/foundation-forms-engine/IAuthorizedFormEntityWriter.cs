using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>Decision-bearing form entity-write boundary.</summary>
public interface IAuthorizedFormEntityWriter
{
    /// <summary>Mints the form instance with the exact decision that admitted the submission.</summary>
    Task<EntityId> CreateAsync(
        FormDefinitionId form,
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationDecision decision,
        CancellationToken ct = default);
}

internal sealed class AuthorizedFormEntityWriter(
    IEntityMutationStore entities,
    IEntityValidator validator) : IAuthorizedFormEntityWriter
{
    public Task<EntityId> CreateAsync(
        FormDefinitionId form,
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.RequireAllowedReaction(
            AuthorizationOperation.Parse(Permission.FormsAuthor),
            options.Tenant,
            "forms",
            form.Value);
        return CreateValidatedAsync(schema, body, options, ct);
    }

    private async Task<EntityId> CreateValidatedAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        CancellationToken ct)
    {
        await validator.ValidateAsync(schema, body, ct).ConfigureAwait(false);
        return await entities.CreateAsync(schema, body, options, ct).ConfigureAwait(false);
    }
}
