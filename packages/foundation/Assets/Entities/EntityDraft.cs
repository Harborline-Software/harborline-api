using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// A single pending entity creation, bundling schema, body, and options for use with
/// <see cref="IEntityMutationStore.CreateBatchAsync"/>.
/// </summary>
/// <param name="Body">The validated initial body and the schema it was validated against.</param>
/// <param name="Options">Creation options (nonce, issuer, tenant, etc.).</param>
public sealed record EntityDraft(
    ValidatedBody Body,
    CreateOptions Options)
{
    /// <summary>The schema the new entity conforms to.</summary>
    public SchemaId Schema => Body.Schema;
}
