namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The canonical immutable submission payload contract (ADR 0140 amendment
/// 2026-07-01 — decision D3). Ties together the three parts of a submitted form
/// instance: the <em>final values</em>, the mandatory <see cref="SubmissionBindingHeader"/>,
/// and the conditional, governed <see cref="SubmissionSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Final values only.</b> <see cref="FinalValues"/> is the canonical serialized
/// body of the submission — the final values the actor submitted, including any
/// Compute-action outputs (which are themselves final values). It is <em>not</em> a
/// fat visibility/derived-state snapshot; that is the demoted, conditional
/// <see cref="Snapshot"/>.
/// </para>
/// <para>
/// <b>Immutable once submitted.</b> The type is <c>sealed</c> with read-only members;
/// the constructor takes a defensive copy of the value bytes, so a caller mutating the
/// source buffer after construction cannot alter a submitted payload. In the persisted
/// substrate the same guarantee is enforced structurally: the final values live in the
/// append-only entity store and the header/snapshot ride the hash-chained audit
/// envelope (a durable mutation-layer concern, per the fleet audit-envelope precedent).
/// </para>
/// <para>
/// <b>Persistence split.</b> <c>FormEngine</c> does not serialize the whole
/// record into one blob: the final values are persisted by the entity store (keyed by
/// the header's schema CID + the minted entity id) and the header + conditional
/// snapshot are written onto the <c>Op.Mint</c> audit record. This record is the
/// in-memory contract that binds them; consumers reassembling a complete submission
/// join the two.
/// </para>
/// </remarks>
public sealed class ImmutableSubmissionPayload
{
    private readonly byte[] _finalValues;

    /// <summary>
    /// Builds the payload from its header, canonical final-values bytes, and an
    /// optional captured snapshot. Copies <paramref name="finalValues"/> defensively.
    /// </summary>
    /// <param name="header">The mandatory binding header.</param>
    /// <param name="finalValues">The canonical serialized final-values body.</param>
    /// <param name="snapshot">The conditional snapshot; null for an ordinary
    /// (minimized) submission.</param>
    public ImmutableSubmissionPayload(
        SubmissionBindingHeader header,
        ReadOnlyMemory<byte> finalValues,
        SubmissionSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
        // Defensive copy → immutability against later mutation of the source buffer.
        _finalValues = finalValues.ToArray();
        Snapshot = snapshot;
    }

    /// <summary>The mandatory binding header (schema CID · definition id/version · engine · locale · time).</summary>
    public SubmissionBindingHeader Header { get; }

    /// <summary>The canonical final-values body. Returns a fresh view over the internally-owned copy.</summary>
    public ReadOnlyMemory<byte> FinalValues => _finalValues;

    /// <summary>
    /// The conditional visibility / derived-state snapshot. Null for an ordinary
    /// submission (the minimization default); populated only when
    /// <see cref="SubmissionSnapshotGate"/> fired.
    /// </summary>
    public SubmissionSnapshot? Snapshot { get; }

    /// <summary>True when this payload carries no snapshot — the minimized shape.</summary>
    public bool IsMinimized => Snapshot is null;
}
