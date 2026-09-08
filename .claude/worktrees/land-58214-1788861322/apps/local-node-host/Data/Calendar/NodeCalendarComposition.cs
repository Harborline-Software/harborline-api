using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Calendar.DependencyInjection;
using Harborline.Api.Blocks.Calendar.Services;

namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// Single source of truth for the node-side CALENDAR composition (ONR app-calendar survey
/// 2026-06-24, inc-0). Registers the calendar block (<c>AddBlocksCalendar</c> — currently ZERO
/// consumers on main) and OVERRIDES its in-memory <see cref="ICalendarEventStore"/> /
/// <see cref="IResourceAvailabilityStore"/> with the durable <c>NodeEf</c> stores over the recoverable
/// <see cref="NodeLocalCalendarDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Override posture.</b> <c>AddBlocksCalendar</c> registers every calendar service with
/// <c>TryAdd</c>, so a host can pre-register a durable store to win — exactly the
/// <c>NodeEfGrantStore : IGrantStore</c> override pattern the KG slice used. This composition calls
/// <c>AddBlocksCalendar</c> first (which registers the in-memory stores via <c>TryAdd</c>) then
/// replaces the two store registrations with the <c>NodeEf</c> ones via <c>AddSingleton</c> (a later
/// non-<c>TryAdd</c> registration is the last-wins resolution EF/DI uses for the single-service
/// <c>GetRequiredService</c> the free/busy + query services resolve). The query / free-busy / booking
/// services from the block are kept as-is — they consume the (now durable) stores through the
/// interface.
/// </para>
/// <para>
/// The caller is responsible for having already registered
/// <c>IDbContextFactory&lt;NodeLocalCalendarDbContext&gt;</c> (via
/// <c>AddSqlCipherLocalNodeDbContext</c> / <c>...WithStoreDek</c>), so the durable stores can resolve
/// the SQLCipher-keyed context factory.
/// </para>
/// </remarks>
public static class NodeCalendarComposition
{
    /// <summary>
    /// Registers the calendar block + overrides its in-memory stores with the durable <c>NodeEf</c>
    /// stores. Idempotent in the sense that <c>AddBlocksCalendar</c> is <c>TryAdd</c>-based; the two
    /// store overrides are plain <c>AddSingleton</c> (last-wins).
    /// </summary>
    public static IServiceCollection AddNodeCalendar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the block (in-memory stores via TryAdd + the query / free-busy / booking services).
        services.AddBlocksCalendar();

        // Override the two stores with the durable NodeEf implementations over the recoverable
        // SQLCipher local-node.db. A plain AddSingleton AFTER AddBlocksCalendar is the last
        // registration, so GetRequiredService<ICalendarEventStore> / <IResourceAvailabilityStore>
        // (the way FreeBusyService + CalendarParticipantCalendarQuery consume them) resolve the
        // durable store.
        services.AddSingleton<ICalendarEventStore, NodeEfCalendarEventStore>();
        services.AddSingleton<IResourceAvailabilityStore, NodeEfResourceAvailabilityStore>();

        // C1 (calendar productization #149) — override the block's in-memory owned-calendar store with the
        // durable NodeEf one over the SAME recoverable SQLCipher local-node.db, exactly as for the event
        // store above.
        services.AddSingleton<ICalendarStore, NodeEfCalendarStore>();

        return services;
    }
}
