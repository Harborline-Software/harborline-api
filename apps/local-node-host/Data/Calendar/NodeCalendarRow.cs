namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// Node-local-authoritative durable row for ONE owned <c>Calendar</c> collection (calendar
/// productization #149, slice C1). The row carries the queryable scope/key columns
/// (<see cref="TenantId"/> + <see cref="Id"/>) plus the full calendar aggregate — name, kind, colour
/// token, owner, resource ref, default flag, audit — serialized as the block's <c>CalendarSnapshot</c>
/// JSON in <see cref="SnapshotJson"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON-on-master.</b> Mirrors <see cref="NodeCalendarEventRow"/>: the block already round-trips a
/// <c>Calendar</c> through a single <c>CalendarSnapshot</c> JSON document, so reusing that snapshot onto
/// a single <c>snapshot_json</c> column keeps the durable store byte-identical to the in-memory
/// round-trip and is the lighter migration. The thin read slice lists by tenant only; no query filters
/// on an inner field, so a normalized column set would buy nothing here.
/// </para>
/// <para>
/// <b>Node-exclusive.</b> Like <see cref="NodeCalendarEventRow"/>, the calendar-collection store is
/// node-only (no Bridge EF persistence), mapped by <see cref="NodeLocalCalendarDbContext"/> — so the
/// council C2 both-provider parity arch-test does not see it. It lives in the SAME SQLCipher-encrypted
/// <c>local-node.db</c> file (SC-1), keyed through the same connection interceptor.
/// </para>
/// </remarks>
public sealed class NodeCalendarRow
{
    /// <summary>The calendar id (string form of the <c>CalendarId</c> Guid) — part of the composite key.</summary>
    public required string Id { get; set; }

    /// <summary>The owning tenant (string form of the active-team-derived <c>TenantId</c>) — the scope column.</summary>
    public required string TenantId { get; set; }

    /// <summary>The full <c>CalendarSnapshot</c> serialized as JSON, (de)serialized with the SAME options the in-memory store uses.</summary>
    public required string SnapshotJson { get; set; }
}
