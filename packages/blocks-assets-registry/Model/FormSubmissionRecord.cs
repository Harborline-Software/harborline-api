using Harborline.Api.Foundation.MultiTenancy;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A generic "this form submission was filled into this record" link (#144 runtime form-fill) — the
/// side record the runtime form-fill surface writes so a record's detail panel can list the forms
/// submitted against it and reopen each read-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed off the generic <see cref="RegistryEntityId"/> (A4)</b>, exactly like
/// <see cref="ConditionAssessment"/> — never a concrete AssetId / EquipmentId — so both new registry
/// entities and (later) promoted Equipment/Asset resolve into ONE submission history through this ref.
/// </para>
/// <para>
/// It is deliberately thin: it carries only the LINKAGE (which entity, which persisted form instance,
/// which form definition, at which instant, by which actor). It never copies the submitted values — the
/// governed submission entity created by the forms engine is the source of truth for those, and the
/// Harborline App reopens it read-only through the existing bound view (<c>GET /forms/{formId}?instance=</c>).
/// This is the "one act, two artifacts" link WITHOUT a domain-typed payload (contrast the
/// condition-rating projection, which additionally captures a typed grade).
/// </para>
/// <para>
/// Implements <see cref="IMustHaveTenant"/>; the store rejects the system / default
/// <see cref="TenantId"/> sentinel. <see cref="SubmittedAt"/> is supplied explicitly (the submit clock,
/// A5c) — history queries never read ambient now.
/// </para>
/// </remarks>
public sealed record FormSubmissionRecord : IMustHaveTenant
{
    /// <summary>Stable, deterministic id for this link (derived from instance + entity — idempotent replay).</summary>
    public required FormSubmissionRecordId Id { get; init; }

    /// <summary>Owning tenant. Required; sentinel rejected by the store.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The generic entity the submission was filled into (A4). Never a concrete AssetId / EquipmentId.</summary>
    public required RegistryEntityId Entity { get; init; }

    /// <summary>The persisted form-instance entity id (opaque) the engine created — reopens the bound view.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The form definition id the submission was filled against (opaque, content-addressed).</summary>
    public required string FormId { get; init; }

    /// <summary>The instant the submission was recorded (the submit clock; supplied, never ambient).</summary>
    public required Instant SubmittedAt { get; init; }

    /// <summary>Opaque reference to the submitting actor (party / actor). First-slice string.</summary>
    public string? AssessorRef { get; init; }
}
