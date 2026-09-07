using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Submission;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// The result of a form save that surfaces the engine's own submit instant alongside the new instance
/// id (ADR 0101 Rev 3.1 Wave 2b / F-CLOCK). The engine stamps the persisted instance + its audit record
/// at <see cref="SubmittedAt"/>; returning it lets a post-submit projection record its side record at
/// the SAME instant rather than taking a fresh clock reading, so the "one act, two artifacts" pair
/// shares one timestamp and replays deterministically.
/// </summary>
/// <param name="InstanceId">The persisted form-instance entity the engine created.</param>
/// <param name="SubmittedAt">The engine's submit instant — the deterministic clock a projection records at.</param>
/// <param name="ProjectionSkips">
/// The binding-declared captures a post-submit projection could NOT land (F3, Wave 3a). Empty on the
/// common path (no projection, or everything landed); non-empty when a declared side record was skipped
/// (audited server-side) so the route can honestly tell the user — never silent success. Defaults to an
/// empty list so a pre-Wave-3a receipt is unchanged.
/// </param>
public readonly record struct FormSubmitReceipt(
    EntityId InstanceId,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<FormSubmitProjectionSkip>? ProjectionSkips = null)
{
    /// <summary>The reported skips, never null (empty when nothing was skipped).</summary>
    public IReadOnlyList<FormSubmitProjectionSkip> Skips =>
        ProjectionSkips ?? Array.Empty<FormSubmitProjectionSkip>();
}
