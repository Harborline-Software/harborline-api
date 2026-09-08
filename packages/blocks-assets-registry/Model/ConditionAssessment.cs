using Harborline.Api.Foundation.MultiTenancy;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A typed condition-assessment record for a maintainable registry entity (annex §3.8, D-M) —
/// the "one act, two artifacts" side-record that a condition-rating inspection field writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed off the generic <see cref="RegistryEntityId"/> (A4)</b> — never a concrete
/// <c>AssetId</c> or <c>EquipmentId</c>. This is the single Wave-1 shape decision that lets both
/// new registry entities and (later) promoted Equipment/Asset resolve into ONE condition history,
/// keeping the promotion wave a bridge rather than a rewrite. It generalizes the shipped
/// equipment-only <c>Harborline.Api.Blocks.Inspections.EquipmentConditionAssessment</c>.
/// </para>
/// <para>
/// <b>Wave 1 scope (A3):</b> this is the record + the binding contract
/// (<see cref="ConditionRatingFieldBinding"/>) only. The live capture — the submission →
/// side-record projection seam and the condition-rating field kind — is a named Wave 2 design
/// task; nothing here projects a form submission into this record.
/// </para>
/// <para>
/// Implements <see cref="IMustHaveTenant"/>; the store rejects the system / default
/// <see cref="TenantId"/> sentinel. <see cref="ObservedAt"/> is supplied explicitly (as-of clock,
/// A5c) — history queries never read ambient now.
/// </para>
/// </remarks>
public sealed record ConditionAssessment : IMustHaveTenant
{
    /// <summary>Stable id for this assessment.</summary>
    public required ConditionAssessmentId Id { get; init; }

    /// <summary>Owning tenant. Required; sentinel rejected by the store.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The generic entity assessed (A4). Never a concrete AssetId / EquipmentId.</summary>
    public required RegistryEntityId Entity { get; init; }

    /// <summary>The condition grade observed (per-type configurable scale).</summary>
    public required ConditionRating Rating { get; init; }

    /// <summary>The instant the condition was observed (as-of clock; supplied, never ambient).</summary>
    public required Instant ObservedAt { get; init; }

    /// <summary>Opaque reference to the assessor (party / actor). First-slice string.</summary>
    public string? AssessorRef { get; init; }

    /// <summary>
    /// The binding that produced this record when captured through an inspection form — the
    /// Wave-2 provenance link back to the submission + field. Null for a directly-recorded
    /// assessment (Wave 1 has no projector, so this is set only by callers that already know it).
    /// </summary>
    public ConditionRatingFieldBinding? SourceBinding { get; init; }

    /// <summary>Optional projected remaining useful life in years; informs replacement planning.</summary>
    public int? ExpectedRemainingLifeYears { get; init; }

    /// <summary>Free-text observations captured at assessment time.</summary>
    public string? Observations { get; init; }

    /// <summary>Optional photo blob references (opaque strings first-slice).</summary>
    public IReadOnlyList<string> PhotoBlobRefs { get; init; } = Array.Empty<string>();
}
