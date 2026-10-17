using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.LocalNodeHost.Data.Entities;

using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>A pack-bound registry record admitted by the canonical record writer.</summary>
public sealed record PackBoundRegistryRecordWritten(RegistryEntity Entity, Guid? AuditId);

/// <summary>The bound form exists but is not an effective published revision.</summary>
public sealed class BoundPropertyFormUnavailableException : Exception
{
    public BoundPropertyFormUnavailableException(string message) : base(message) { }
}

/// <summary>
/// The canonical record committed but its registry metadata index did not. The receipt is preserved so
/// the route never reports this partial success as though no record were created.
/// </summary>
public sealed class RegistryRecordIndexException(EntityId recordId, Guid? auditId, Exception innerException)
    : Exception("The canonical record committed, but its asset-registry index failed.", innerException)
{
    public EntityId RecordId { get; } = recordId;
    public Guid? AuditId { get; } = auditId;
}

/// <summary>
/// Creates the canonical JSON record for an asset-registry type's effective property form, then indexes
/// its registry metadata. The index is never written before records:write authorization and schema
/// validation have admitted and persisted the canonical record.
/// </summary>
public sealed class PackBoundRegistryRecordWriter(
    IFormDefinitionStore forms,
    NodeEntityWriter records,
    IEntityStore recordStore,
    IRegistryEntityRepository registry)
{
    internal const string RecordScheme = "record";
    internal const string RecordAuthority = "asset-registry";

    /// <summary>Creates one explicitly typed, form-bound registry record. The actor is its Party
    /// attribution; the authority carries the distinct grant principal for the single write decision.</summary>
    public ValueTask<PackBoundRegistryRecordWritten> CreateAsync(
        EntityTypeId type,
        FormBindingRef propertyForm,
        string displayName,
        string? scanKey,
        JsonDocument values,
        ActorId actor,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
        => CreateCoreAsync(type, propertyForm, displayName, scanKey, values, actor, authority, null, null, ct);

    internal ValueTask<PackBoundRegistryRecordWritten> CreateWithDecisionAsync(
        RegistryEntityId id, EntityTypeId type, FormBindingRef propertyForm, string displayName, string? scanKey,
        JsonDocument values, ActorId actor, AuthorizationWriteContext authority,
        AuthorizationDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.RequireAllowed();
        if (decision.Request.Target.RecordKind != "record" || decision.Request.Target.RecordId != id.Value)
            throw new ArgumentException("The record decision does not match the prospective registry id.", nameof(decision));
        return CreateCoreAsync(type, propertyForm, displayName, scanKey, values, actor, authority, decision, id, ct);
    }

    private async ValueTask<PackBoundRegistryRecordWritten> CreateCoreAsync(
        EntityTypeId type, FormBindingRef propertyForm, string displayName, string? scanKey,
        JsonDocument values, ActorId actor, AuthorizationWriteContext authority,
        AuthorizationDecision? decision, RegistryEntityId? prospectiveId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyForm);
        ArgumentNullException.ThrowIfNull(values);

        var id = prospectiveId ?? RegistryEntityId.NewId();
        var submittedAt = authority.At;
        async ValueTask<(SchemaId Schema, CreateOptions Options)> PrepareAsync(CancellationToken preparationToken)
        {
            var definition = await forms.GetAsync(
                new DefinitionCoordinates(
                    authority.Tenant,
                    propertyForm.Definition.Value,
                    propertyForm.PinnedVersion.ToString()),
                preparationToken).ConfigureAwait(false);
            if (definition.Status != FormDefinitionStatus.Published)
                throw new BoundPropertyFormUnavailableException("The bound property form is not published.");

            var header = SubmissionBindingHeader.Create(definition, ["en"], submittedAt);
            var binding = new EntityBinding(
                definition.SchemaRef,
                header.DefinitionId,
                header.DefinitionVersion,
                header.EngineVersion,
                header.LocaleChain,
                header.SubmittedAt);
            var options = new CreateOptions(
                RecordScheme,
                RecordAuthority,
                id.Value,
                actor,
                authority.Tenant,
                ValidFrom: submittedAt,
                ExplicitLocalPart: id.Value,
                Binding: binding);

            return (definition.SchemaRef, options);
        }
        var written = decision is null
            ? await records.CreateWithReceiptAsync(values, id.Value, authority, PrepareAsync, ct).ConfigureAwait(false)
            : await records.CreateWithDecisionAsync(values, id.Value, authority, decision, PrepareAsync, ct)
                .ConfigureAwait(false);

        var entity = new RegistryEntity
        {
            Id = id,
            TenantId = authority.Tenant,
            Type = type,
            DisplayName = displayName,
            PropertyForm = propertyForm,
            ScanKey = scanKey,
            CreatedAt = new Instant(submittedAt),
        };
        try
        {
            await registry.UpsertAsync(entity, entity.CreatedAt, actor.Value, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RegistryRecordIndexException(written.Entity, written.AuditId, exception);
        }
        return new PackBoundRegistryRecordWritten(entity, written.AuditId);
    }

    /// <summary>Reads the canonical submitted values for a registry record, if it has one.</summary>
    public async ValueTask<JsonElement?> ReadValuesAsync(
        TenantId tenant,
        RegistryEntity entity,
        CancellationToken ct = default)
    {
        if (entity.TenantId != tenant || entity.PropertyForm is null)
            return null;

        var stored = await recordStore.GetAsync(
            new EntityId(RecordScheme, RecordAuthority, entity.Id.Value),
            default,
            ct).ConfigureAwait(false);
        if (stored is null
            || stored.Tenant != tenant
            || stored.Binding?.DefinitionId != entity.PropertyForm.Definition.Value
            || stored.Binding.DefinitionVersion != entity.PropertyForm.PinnedVersion.ToString())
        {
            return null;
        }

        return stored.Body.RootElement.Clone();
    }
}
