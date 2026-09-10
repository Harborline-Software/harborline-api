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
    EntityBodyAdmission admission) : IAuthorizedFormEntityWriter
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
        // The engine validated the submission against this same schema in the registry BEFORE
        // field protection; `body` is the protected form, whose Sensitive fields are ciphertext, so
        // re-running the record-body validator over it would refuse a correct submission. The token
        // therefore comes from the named own-validated path (ticket 151 candidate C).
        return entities.CreateAsync(
            admission.AdmitOwnValidated(
                entities, schema, body, "form instance validated by FormEngine against ISchemaRegistry"),
            options,
            ct);
    }
}
