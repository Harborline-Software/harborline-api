namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// Node-local-authoritative durable row for ONE <c>CalendarEvent</c> series master (the calendar
/// thin-slice durable store; ONR app-calendar survey 2026-06-24, inc-0). The row carries the
/// queryable scope/key columns (<see cref="TenantId"/> + <see cref="Id"/>) plus the full event
/// aggregate — its EXDATE set, RECURRENCE-ID overrides, participations + padding + visibility —
/// serialized as the block's <c>CalendarEventSnapshot</c> JSON in <see cref="SnapshotJson"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON-on-master (not child tables).</b> The block already round-trips a <c>CalendarEvent</c>
/// through a single <c>CalendarEventSnapshot</c> JSON document (that is exactly what the in-memory
/// store persists). Reusing that one snapshot shape onto a single <c>snapshot_json</c> column is the
/// lighter migration AND keeps the durable store byte-identical to the in-memory round-trip — a
/// saved series re-loads with its occurrence-level edits intact. The thin read slice never filters on
/// an occurrence-level field, so child tables would buy nothing; revisit only if a query needs to
/// WHERE on an EXDATE/override (survey open-question #1, recommendation: JSON-on-master for inc-0).
/// </para>
/// <para>
/// <b>Node-exclusive, NOT a shared <c>IHarborlineEntityModule</c>.</b> Like the comms / roster doctypes,
/// the calendar store is node-only with no Bridge EF persistence, mapped by its own
/// <see cref="NodeLocalCalendarDbContext"/> (a separate <c>DbContext</c> from
/// <c>LocalNodeDbContext</c>), so the council C2 both-provider parity arch-test does not see it. It
/// lives in the SAME SQLCipher-encrypted <c>local-node.db</c> file (SC-1 — encrypted at rest), keyed
/// through the same connection interceptor.
/// </para>
/// </remarks>
public sealed class NodeCalendarEventRow
{
    /// <summary>The event id (string form of the <c>CalendarEventId</c> Guid) — part of the composite key.</summary>
    public required string Id { get; set; }

    /// <summary>The owning tenant (string form of the active-team-derived <c>TenantId</c>) — the scope column.</summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// The full <c>CalendarEventSnapshot</c> serialized as JSON — the master fields + EXDATE set +
    /// RECURRENCE-ID overrides + participations + padding + visibility. The store (de)serializes this
    /// with the SAME options the in-memory store uses, so the aggregate round-trips identically.
    /// </summary>
    public required string SnapshotJson { get; set; }
}
