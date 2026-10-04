using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;

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
    IWritePipelineObserver? pipelineObserver = null) : IAuthorizedFormEntityWriter
{
    private IEntityMutationStore Entities => entities;

    public async Task<EntityId> CreateAsync(
        FormDefinitionId form,
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return (await WritePipeline.RunAsync(
            new SubmissionCreate(this, form, schema, body, options, decision), pipelineObserver, ct)
            .ConfigureAwait(false)).GetValueOrDefault();
    }

    /// <summary>
    /// ck-10 S3 (DES-0029, ADR 0038): a form submission record as its six stages, under the engine's carried
    /// decision. Validate admits what the engine already validated: the engine checks the plaintext against the
    /// form schema and its rules, then encrypts protected fields, so the body here may be ciphertext the schema
    /// cannot judge. The store's own validator still runs at commit.
    /// </summary>
    private sealed class SubmissionCreate(
        AuthorizedFormEntityWriter writer,
        FormDefinitionId form,
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationDecision decision)
        : KernelWrite<CreateOptions, CreateOptions, CreateOptions, EntityId?>
    {
        private EntityId created;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            decision.RequireAllowedReaction(
                AuthorizationOperation.Parse(Permission.FormsAuthor), options.Tenant, "forms", form.Value);
            return ValueTask.CompletedTask;
        }

        /// <summary>A new record: there is no prior state to read.</summary>
        protected override ValueTask<CreateOptions?> BindAsync(CancellationToken ct) =>
            ValueTask.FromResult<CreateOptions?>(options);

        protected override ValueTask<CreateOptions> MutateAsync(CreateOptions bound, CancellationToken ct) =>
            ValueTask.FromResult(bound);

        protected override ValueTask<CreateOptions> ValidateAsync(CreateOptions bound, CreateOptions mutation, CancellationToken ct) =>
            ValueTask.FromResult(mutation);

        protected override async ValueTask CommitAsync(CreateOptions validated, CancellationToken ct) =>
            created = await writer.Entities.CreateAsync(schema, body, validated, ct).ConfigureAwait(false);

        protected override ValueTask<EntityId?> ReactAsync(CreateOptions validated, CancellationToken ct) =>
            ValueTask.FromResult<EntityId?>(created);
    }
}
