namespace Harborline.Api.Foundation.Lifecycle;

/// <summary>
/// Recoverable archival. An archived row is excluded from default lists but remains
/// fully resolvable for referential history; restore is permitted; archival does NOT
/// block already-valid references to the row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mental model (ADR 0108):</strong> "put away, can take back out." Contrast
/// <see cref="ISoftDeletable"/> ("done / removed, kept for the record"), whose tombstone
/// additionally blocks further state transitions and is not restorable by contract. An
/// entity MAY implement BOTH markers; the two operations are independent (e.g. a project
/// can be archived <em>or</em> soft-deleted, and the storage / write paths for each are
/// distinct).
/// </para>
/// <para>
/// <strong>Default-list exclusion is the load-bearing invariant (ADR 0108).</strong> A
/// repository's default list query MUST exclude rows with a non-null
/// <see cref="ArchivedAt"/> unless an explicit <c>includeArchived</c> flag is passed. That
/// predicate lives at the SAME repository seam as ADR 0092 tenant-keying — it is a second
/// <c>WHERE</c> predicate on an already-canonical boundary, not a new mechanism. Archival
/// does not invalidate existing references: an archived row stays resolvable by id and via
/// existing references (referential history intact).
/// </para>
/// <para>
/// <strong>Timestamp type is BCL by design (ADR 0108 F1 + O-1).</strong>
/// <see cref="ArchivedAt"/> is a BCL <see cref="DateTimeOffset"/>? so that
/// <c>foundation-lifecycle</c> stays dependency-free (no NodaTime, no EF Core). Cluster
/// storage is NOT uniform — some clusters store NodaTime <c>Instant?</c> (e.g. Project),
/// others already store <see cref="DateTimeOffset"/>? (e.g. WorkOrder, Property). A
/// NodaTime-backed adapter MUST convert <c>Instant? ↔ DateTimeOffset?</c> explicitly at the
/// boundary (mechanical and lossless: <c>Instant</c> → UTC <see cref="DateTimeOffset"/>);
/// the write path must NOT assume the marker type and the storage type are identical.
/// </para>
/// </remarks>
public interface IArchivable
{
    /// <summary>
    /// When the row was archived, or <see langword="null"/> while it is active.
    /// A non-null value excludes the row from default lists but keeps it resolvable
    /// for referential history; restore (setting this back to <see langword="null"/>)
    /// is permitted by this contract.
    /// </summary>
    DateTimeOffset? ArchivedAt { get; }
}
