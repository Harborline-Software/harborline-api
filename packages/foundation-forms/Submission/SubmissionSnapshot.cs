using System.Text.Json;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// How (if at all) a submission's visibility / derived-state snapshot is captured
/// (ADR 0140 amendment 2026-07-01 — decision D3).
/// </summary>
/// <remarks>
/// The fat snapshot is NOT a default. Capturing the full projection on every
/// submission is a GDPR data-minimization + storage-limitation violation and
/// doubles PII on the edge tier. Capture is therefore gated
/// (<see cref="SubmissionSnapshotGate"/>) and, when it does fire, prefers the
/// hash-of-DTBS mode over storing a full PII projection.
/// </remarks>
public enum SnapshotCaptureMode
{
    /// <summary>
    /// No snapshot captured — the minimization default for an ordinary submission.
    /// The final values (in the entity store) + the binding header are the whole
    /// record.
    /// </summary>
    None = 0,

    /// <summary>
    /// A full visibility / derived-state projection is captured. Reserved for a form
    /// carrying a SPINE-2 <c>capture-as-shown</c> / compliance-grade tag. Heavier and
    /// governed; never the default.
    /// </summary>
    FullProjection = 1,

    /// <summary>
    /// Only a signed hash of the rendered data-to-be-signed (DTBS) is captured — the
    /// preferred mode for a signed submission. Proves what was shown WITHOUT storing
    /// the projection, so it satisfies the eIDAS/signing need at a fraction of the
    /// stored-PII cost.
    /// </summary>
    SignedDtbsHash = 2,
}

/// <summary>
/// The GDPR-minimal signed artifact for a signed submission (ADR 0140 D3): a hash of
/// the rendered data-to-be-signed plus a signature over that hash. It stores NO
/// cleartext projection — proving "what was signed" without doubling PII.
/// </summary>
/// <param name="HashAlgorithm">The digest algorithm over the DTBS (e.g. <c>"SHA-256"</c>).</param>
/// <param name="DtbsHash">The digest of the canonical DTBS bytes.</param>
/// <param name="SignatureAlgorithm">The signature scheme (e.g. <c>"Ed25519"</c>).</param>
/// <param name="Signature">The signature over <see cref="DtbsHash"/>.</param>
/// <param name="PublicKeyRef">An optional reference to the verifying public key
/// (key id / fingerprint), so a later verifier can resolve the key.</param>
public sealed record SignedDtbsHash(
    string HashAlgorithm,
    ReadOnlyMemory<byte> DtbsHash,
    string SignatureAlgorithm,
    ReadOnlyMemory<byte> Signature,
    string? PublicKeyRef = null);

/// <summary>
/// The conditional, governed visibility / derived-state snapshot artifact for a
/// submission (ADR 0140 D3). Present only when <see cref="SubmissionSnapshotGate"/>
/// fired; the mode records which shape was captured.
/// </summary>
/// <remarks>
/// Exactly one payload member is populated per <see cref="Mode"/>:
/// <see cref="FullProjection"/> for <see cref="SnapshotCaptureMode.FullProjection"/>,
/// <see cref="Signed"/> for <see cref="SnapshotCaptureMode.SignedDtbsHash"/>. A
/// <see cref="SnapshotCaptureMode.None"/> decision produces NO
/// <see cref="SubmissionSnapshot"/> at all (a null, not an empty record).
/// </remarks>
/// <param name="Mode">Which snapshot shape was captured.</param>
/// <param name="CapturedAt">The UTC instant the snapshot was captured.</param>
/// <param name="FullProjection">The full as-shown projection document — populated iff
/// <see cref="Mode"/> is <see cref="SnapshotCaptureMode.FullProjection"/>. To avoid
/// doubling PII on the edge tier, callers capture the at-rest (encrypted) body, not a
/// fresh cleartext copy.</param>
/// <param name="Signed">The signed DTBS-hash artifact — populated iff <see cref="Mode"/>
/// is <see cref="SnapshotCaptureMode.SignedDtbsHash"/>.</param>
public sealed record SubmissionSnapshot(
    SnapshotCaptureMode Mode,
    DateTimeOffset CapturedAt,
    JsonDocument? FullProjection = null,
    SignedDtbsHash? Signed = null);
