using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The conditional-snapshot capture gate (ADR 0140 amendment 2026-07-01 — decision
/// D3). Decides whether a submission's visibility / derived-state snapshot is
/// captured and, if so, in which shape — keyed off the signature path and the
/// SPINE-2 <c>capture-as-shown</c> / compliance-grade classification tag.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of the D3 overturn: the fat snapshot is NOT a default.
/// The default is minimization (<see cref="SnapshotCaptureMode.None"/>). Capture
/// fires only for:
/// </para>
/// <list type="number">
///   <item><description>a <b>signed</b> submission → <see cref="SnapshotCaptureMode.SignedDtbsHash"/>
///     (prefer signing a hash of the rendered DTBS over storing the full projection); or</description></item>
///   <item><description>a form carrying a SPINE-2 <b>capture-as-shown</b> /
///     <b>compliance-grade</b> classification tag → <see cref="SnapshotCaptureMode.FullProjection"/>.</description></item>
/// </list>
/// <para>
/// Pure and dependency-free: it reads only the definition's <see cref="AspectOverlay"/>
/// grains, so it behaves identically whether or not the SPINE-2 governance seam is
/// wired, and is trivially unit-testable.
/// </para>
/// </remarks>
public static class SubmissionSnapshotGate
{
    /// <summary>
    /// The open-vocabulary classification system that carries a form's D3 capture
    /// policy. Distinct from <c>"shipyard/data-classification"</c> (pii/phi/pci/cui):
    /// this system's tags express a <em>capture</em> obligation, not a data-sensitivity
    /// class.
    /// </summary>
    public const string CapturePolicySystem = "shipyard/capture-policy";

    /// <summary>The tag code opting a form into an as-shown full-projection capture.</summary>
    public const string CaptureAsShownCode = "capture-as-shown";

    /// <summary>
    /// A compliance-grade tag code — treated identically to <see cref="CaptureAsShownCode"/>
    /// (a form declared compliance-grade must capture what was shown).
    /// </summary>
    public const string ComplianceGradeCode = "compliance-grade";

    /// <summary>
    /// Decides the snapshot capture for a submission.
    /// </summary>
    /// <param name="overlay">The definition overlay (walked for the capture-policy tag
    /// at form / section / field grain).</param>
    /// <param name="isSigned">Whether this submission is signed (an eIDAS / signing
    /// path is in force).</param>
    /// <returns>The capture decision. <see cref="SnapshotDecision.None"/> is the
    /// minimization default.</returns>
    public static SnapshotDecision Decide(HarborlineOverlay overlay, bool isSigned)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        // A signed submission always captures — but prefer signing a hash of the DTBS
        // over storing the full projection (the GDPR-minimal path).
        if (isSigned)
        {
            return new SnapshotDecision(true, SnapshotCaptureMode.SignedDtbsHash, "signed");
        }

        // Otherwise capture only when the form is explicitly tagged compliance-grade /
        // capture-as-shown at any grain.
        if (HasCaptureTag(overlay))
        {
            return new SnapshotDecision(true, SnapshotCaptureMode.FullProjection, "compliance-grade-tag");
        }

        // Minimization default: capture nothing. This is the branch an ordinary
        // submission takes.
        return SnapshotDecision.None;
    }

    /// <summary>
    /// True when the overlay carries a capture-policy tag (<see cref="CaptureAsShownCode"/>
    /// or <see cref="ComplianceGradeCode"/> under <see cref="CapturePolicySystem"/>) at
    /// the form grain, any section grain, or any field grain.
    /// </summary>
    private static bool HasCaptureTag(HarborlineOverlay overlay)
    {
        if (ContainsCaptureTag(overlay.Aspects))
        {
            return true;
        }

        foreach (var section in overlay.Sections)
        {
            if (ContainsCaptureTag(section.Aspects))
            {
                return true;
            }
        }

        foreach (var field in overlay.Fields.Values)
        {
            if (ContainsCaptureTag(field.Aspects))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCaptureTag(AspectOverlay? aspects)
    {
        var tags = aspects?.Classification?.Tags;
        if (tags is null)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (string.Equals(tag.System, CapturePolicySystem, StringComparison.Ordinal)
                && (string.Equals(tag.Code, CaptureAsShownCode, StringComparison.Ordinal)
                    || string.Equals(tag.Code, ComplianceGradeCode, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The outcome of <see cref="SubmissionSnapshotGate.Decide"/>: whether to capture a
/// snapshot, in which mode, and why.
/// </summary>
/// <param name="ShouldCapture">True when a snapshot is to be captured.</param>
/// <param name="Mode">The capture shape (<see cref="SnapshotCaptureMode.None"/> when
/// <paramref name="ShouldCapture"/> is false).</param>
/// <param name="Reason">A short, stable reason token for the decision (audit / debug).</param>
public sealed record SnapshotDecision(bool ShouldCapture, SnapshotCaptureMode Mode, string? Reason)
{
    /// <summary>The minimization default — capture nothing.</summary>
    public static SnapshotDecision None { get; } = new(false, SnapshotCaptureMode.None, null);
}
