namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// The durable capacity epoch for one <c>(tenant, resource)</c> pair — the counter the platform's
/// <c>ICalendarEventStore.SaveIfCapacityUnchangedAsync</c> compares against (T-659; DES-0025
/// <c>booking-eng-24</c>; ADR 0095 ruling 8).
/// </summary>
/// <remarks>
/// <para>
/// The number itself means nothing. It only answers "is the occupancy this claim derived its
/// capacity from still the current one?" — it moves on every write or removal of an event that
/// occupies the resource, so a save conditional on an older value is a stale claim and is refused.
/// </para>
/// <para>
/// <b>Why a row and not a lock.</b> A lock serializes claims inside one host process, which is the
/// ceiling T-659 exists to remove. This row lives in the same <c>local-node.db</c> file as the
/// events, so the conditional <c>UPDATE … WHERE epoch = @expected</c> that bumps it is an atomic
/// compare-and-set against every writer of that file, in this process or another.
/// </para>
/// </remarks>
public sealed class NodeCalendarCapacityEpochRow
{
    /// <summary>The owning tenant — part of the composite key.</summary>
    public required string TenantId { get; set; }

    /// <summary>The resource, as <c>Kind:Value</c> of the block's <c>ParticipantRef</c> — part of the composite key.</summary>
    public required string Resource { get; set; }

    /// <summary>The epoch. Absent is zero; every occupying write or removal adds one.</summary>
    public required long Epoch { get; set; }
}
