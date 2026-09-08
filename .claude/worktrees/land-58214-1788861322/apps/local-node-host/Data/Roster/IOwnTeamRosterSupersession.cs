namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// A narrow seam for a join-time SELF-SUPERSESSION of a node's OWN-team roster records in the synced roster
/// doctype (cerebrum [2026-06-21] DECISIVE cross-machine verify — BLOCKER-2). The two-sided wire-enrollment
/// joiner (<see cref="Harborline.Api.LocalNodeHost.Enrollment.NodeWireEnrollmentClient"/>) calls this BEFORE the
/// irreversible in-memory adopt of the admitter's team, FAIL-CLOSED (#1304 F1): the node's superseded own-team
/// genesis is retracted from the synced doctype first, and only on success does the node adopt — so the node's own
/// genesis can never poison the synced roster's dual-genesis injection guard, and a supersession FAULT aborts the
/// join cleanly (no partial adopt, no silent dual-genesis poison) instead of half-completing.
/// Implemented by <see cref="RosterCrdtProjection"/>.
/// </summary>
/// <remarks>
/// <para>
/// The interface is intentionally a single method so the joiner client closes over THIS seam, not the concrete
/// projection — keeping the foundation-side joiner protocol testable with a stub and free of the host's CRDT /
/// EF machinery. It is OPTIONAL on the client (null = no synced roster doctype to supersede, e.g. a minimal DI
/// test) so a node wired without a roster projection still enrolls.
/// </para>
/// </remarks>
public interface IOwnTeamRosterSupersession
{
    /// <summary>
    /// Supersede (retract + durably purge) every record bearing this node's OWN (superseded) genesis team id in the
    /// synced roster doctype — matched by <paramref name="ownTeamId"/> (a teamId-equality filter, NOT a per-record
    /// signer/author predicate) — so only the adopted (admitter's) team's records survive convergence. Removing
    /// records for the node's OWN superseded team is legitimate self-supersession; the foreign-dual-genesis
    /// injection guard in <see cref="Harborline.Api.Foundation.IdentityAtlas.MemberRoster.FromSyncedRecords"/> is left
    /// intact (a second genesis for the JOINED team — a different team id, a foreign signer — is still rejected).
    /// </summary>
    /// <param name="ownTeamId">This node's OWN (pre-enrollment) genesis team id — the records to supersede.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of own-team records superseded.</returns>
    Task<int> SupersedeOwnTeamRecordsAsync(System.Guid ownTeamId, CancellationToken ct);
}
