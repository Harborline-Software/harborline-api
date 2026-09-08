namespace Harborline.Api.Foundation.Lifecycle;

/// <summary>
/// Tombstone soft-deletion. A soft-deleted row is excluded from default lists AND blocks
/// further state transitions ("gone, but the row survives for audit"). It remains readable
/// for audit/history but is not a valid target for new operations.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mental model (ADR 0108):</strong> "done / removed, kept for the record."
/// Contrast <see cref="IArchivable"/> ("put away, can take back out"), which is restorable
/// and does NOT block transitions. An entity MAY implement BOTH markers; the two operations
/// are independent.
/// </para>
/// <para>
/// <strong>Transition-block is enforced at the service layer (ADR 0108 O-2).</strong> The
/// "blocks further state transitions" invariant is a RUNTIME-STATE guard — a write handler
/// rejects a transition whose target has a non-null <see cref="DeletedAt"/> — backed by
/// parity tests. It is deliberately NOT a Roslyn analyzer: an analyzer catches call-shape
/// violations, not runtime state. A future analyzer that catches "the service forgot to
/// call the guard" is belt-and-suspenders for a later ADR (F3), not part of this contract.
/// Restore is NOT offered by this contract (a tombstone is terminal for write purposes);
/// the row stays readable for audit/history.
/// </para>
/// <para>
/// <strong>Default-list exclusion is the load-bearing invariant (ADR 0108).</strong> A
/// repository's default list query MUST exclude rows with a non-null
/// <see cref="DeletedAt"/> unless an explicit <c>includeDeleted</c> flag is passed — a
/// second <c>WHERE</c> predicate at the SAME repository seam as ADR 0092 tenant-keying.
/// </para>
/// <para>
/// <strong>Timestamp type is BCL by design (ADR 0108 F1 + O-1).</strong>
/// <see cref="DeletedAt"/> is a BCL <see cref="DateTimeOffset"/>? so that
/// <c>foundation-lifecycle</c> stays dependency-free. A NodaTime-backed cluster (e.g.
/// Project, whose storage is <c>Instant?</c>) MUST convert <c>Instant? ↔ DateTimeOffset?</c>
/// explicitly at the boundary; the write path must NOT assume the marker type and the
/// storage type are identical.
/// </para>
/// </remarks>
public interface ISoftDeletable
{
    /// <summary>
    /// When the row was soft-deleted (tombstoned), or <see langword="null"/> while it is
    /// live. A non-null value excludes the row from default lists, blocks further state
    /// transitions, and is terminal for write purposes (no restore by this contract); the
    /// row remains readable for audit/history.
    /// </summary>
    DateTimeOffset? DeletedAt { get; }
}
