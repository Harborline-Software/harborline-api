using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// The <b>binding contract</b> (documented types only) by which a Wave-2 condition-rating
/// inspection field declares that filling it writes a <see cref="ConditionAssessment"/> for a
/// specific entity (annex §3.8, D-M; ADR 0101 Rev 3.1 / .NET-architect finding <b>A3</b>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wave 1 builds the CONTRACT, not the capture.</b> This type documents <i>what</i> a
/// condition-rating field marks so the Wave-2 governed post-submit projector (the
/// submission → side-record seam) knows how to write the typed record. Wave 1 ships NO projector,
/// NO field-kind plugin, and NO cross-aggregate write path — those are the named Wave 2 design
/// task. The forms substrate has no "field kind" extension point today (A3); introducing it is a
/// Wave-2 concern, not a Wave-1 one.
/// </para>
/// <para>
/// The contract deliberately carries the <i>entity-ref resolution</i> — how the projector, given
/// a submission, resolves which <see cref="RegistryEntityId"/> the rating is for — because that
/// is the cross-aggregate seam A3 flagged. Wave 2 chooses a concrete
/// <see cref="EntityRefSource"/> policy; Wave 1 only pins the shape.
/// </para>
/// </remarks>
/// <param name="FormDefinition">The inspection form definition that carries the condition-rating field.</param>
/// <param name="FieldPointer">
/// JSON-pointer-style path to the condition-rating field within the form's schema (e.g.
/// <c>/sections/kitchen/water_heater_condition</c>). The forms substrate keys fields by path.
/// </param>
/// <param name="EntityRefSource">How the Wave-2 projector resolves the target entity for the rating.</param>
/// <param name="EntityRefKey">
/// The key the <see cref="EntityRefSource"/> reads (a submission field pointer for
/// <see cref="ConditionEntityRefSource.SubmissionField"/>, or the visit-case key name for
/// <see cref="ConditionEntityRefSource.VisitCase"/>). Ignored for
/// <see cref="ConditionEntityRefSource.Explicit"/>.
/// </param>
/// <param name="ScaleMax">The condition scale maximum this field rates on (per-type configurable).</param>
public sealed record ConditionRatingFieldBinding(
    FormDefinitionId FormDefinition,
    string FieldPointer,
    ConditionEntityRefSource EntityRefSource,
    string? EntityRefKey = null,
    int ScaleMax = ConditionRating.DefaultScaleMax);

/// <summary>
/// How a Wave-2 projector resolves the <see cref="RegistryEntityId"/> a condition rating targets
/// (documented options only — Wave 1 pins the shape, Wave 2 implements one).
/// </summary>
public enum ConditionEntityRefSource
{
    /// <summary>The entity ref is read from another field in the same submission.</summary>
    SubmissionField = 0,

    /// <summary>The entity ref is the visit/case context the submission is pinned to.</summary>
    VisitCase = 1,

    /// <summary>The entity ref is supplied explicitly by the caller constructing the assessment.</summary>
    Explicit = 2,
}
