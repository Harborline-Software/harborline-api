namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// Node-local-authoritative durable row for ONE resource's <c>ResourceAvailability</c> (the bookable
/// supply per resource per tenant; ONR app-calendar survey 2026-06-24, inc-0). free/busy needs
/// BOTH events AND availability, so the durable store persists both. The row carries the queryable
/// scope/key columns (<see cref="TenantId"/> + the resource discriminator/value) plus the full
/// availability record — its windows + per-resource exception dates/spans — serialized as the block's
/// <c>ResourceAvailabilitySnapshot</c> JSON in <see cref="SnapshotJson"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite key <c>(TenantId, ResourceKind, ResourceValue)</c></b> — one availability record per
/// resource (re-saving replaces it), matching the in-memory store's keying. The kind is part of the
/// key so a Party id and an Asset id that happen to share a string value never collide.
/// </para>
/// <para>
/// Same JSON-on-master + node-exclusive + encrypted-at-rest discipline as
/// <see cref="NodeCalendarEventRow"/>.
/// </para>
/// </remarks>
public sealed class NodeResourceAvailabilityRow
{
    /// <summary>The owning tenant (string form of the active-team-derived <c>TenantId</c>) — part of the composite key.</summary>
    public required string TenantId { get; set; }

    /// <summary>The resource-ref discriminator (0 = Party, 1 = Asset; the <c>ParticipantKind</c>) — part of the composite key.</summary>
    public required int ResourceKind { get; set; }

    /// <summary>The resource-ref id value (a PartyId.Value / AssetId.Value) — part of the composite key.</summary>
    public required string ResourceValue { get; set; }

    /// <summary>
    /// The full <c>ResourceAvailabilitySnapshot</c> serialized as JSON — the resource ref + tz +
    /// windows + exception dates/spans. (De)serialized with the SAME options the in-memory store uses.
    /// </summary>
    public required string SnapshotJson { get; set; }
}
