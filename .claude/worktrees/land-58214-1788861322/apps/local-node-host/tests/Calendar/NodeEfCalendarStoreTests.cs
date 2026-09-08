using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Calendar;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Calendar;

/// <summary>
/// ONR app-calendar survey 2026-06-24, inc-0 binary gate — the durable EF calendar stores
/// (<see cref="NodeEfCalendarEventStore"/> + <see cref="NodeEfResourceAvailabilityStore"/>)
/// round-trip on the REAL keyed (SQLCipher-encrypted) SQLite store, exercised through the same SC-1
/// registration path the host uses
/// (<see cref="LocalNodeSqlCipherRegistration.AddSqlCipherLocalNodeDbContext"/>). Mirrors
/// <c>MaintenanceStoreTests</c>.
/// </summary>
/// <remarks>
/// On-disk tests (not <c>:memory:</c>): the registration installs the SqlCipher interceptor + the
/// startup encryption guard, which migrates the calendar context against the same keyed file. The
/// round-trip proves (1) the calendar schema is created in the encrypted store, (2) a saved series /
/// availability re-loads — including its occurrence-level edits (EXDATE / overrides) — across a
/// CONTEXT REOPEN (durable, the Roster/Slice-1d restart pattern), and (3) cross-tenant isolation.
/// </remarks>
public sealed class NodeEfCalendarStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Actor = Guid.NewGuid();

    private readonly string _dir;
    private readonly string _dbPath;

    public NodeEfCalendarStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-cal-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");
    }

    private static byte[] FreshRootSeed()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    private static IReadOnlyList<IHarborlineEntityModule> NoModules() => [];

    /// <summary>Boots a host with the SC-1 registration; the encryption guard has migrated the calendar context by the time StartAsync returns.</summary>
    private async Task<IHost> StartKeyedHostAsync(byte[] rootSeed)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        builder.Services.AddSqlCipherLocalNodeDbContext(
            rootSeed: rootSeed,
            databasePath: _dbPath,
            keyDerivation: new SqlCipherKeyDerivation());

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static NodeEfCalendarEventStore EventStore(IHost host)
        => new(host.Services.GetRequiredService<IDbContextFactory<NodeLocalCalendarDbContext>>());

    private static NodeEfResourceAvailabilityStore AvailabilityStore(IHost host)
        => new(host.Services.GetRequiredService<IDbContextFactory<NodeLocalCalendarDbContext>>());

    private static CalendarEvent RecurringMorningStandup()
    {
        // A weekly recurring series with an EXDATE cancellation + a RECURRENCE-ID override — so the
        // round-trip proves the occurrence-level edits survive (de)serialization, not just the master.
        var anchor = new DateOnly(2026, 3, 2); // a Monday
        var ev = CalendarEvent.Create(
            TenantA, "Morning standup", anchor, anchor, Actor,
            timezone: "UTC",
            startTime: new TimeOnly(9, 0),
            endTime: new TimeOnly(9, 30),
            rrule: "FREQ=WEEKLY;BYDAY=MO;COUNT=4");
        ev.CancelOccurrence(anchor.AddDays(7), Actor);                       // EXDATE the 2nd occurrence
        ev.OverrideOccurrence(
            new OccurrenceOverride
            {
                RecurrenceId = anchor.AddDays(14),
                NewStart = anchor.AddDays(14),
                NewTitle = "Standup (moved)",
            },
            Actor);                                                          // RECURRENCE-ID override the 3rd
        return ev;
    }

    [Fact(DisplayName = "inc-0: calendar event round-trips across a context reopen (EXDATE + override intact)")]
    public async Task CalendarEvent_RoundTrips_AcrossContextReopen()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = EventStore(host);

        var saved = RecurringMorningStandup();
        await store.SaveAsync(saved);

        // Fresh store instance (fresh context per call inside) — proves it persisted to the FILE,
        // not a held reference / change-tracker.
        var reloaded = await store.GetAsync(TenantA, saved.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(saved.Title, reloaded!.Title);
        Assert.Equal(saved.Rrule, reloaded.Rrule);
        Assert.Equal(new TimeOnly(9, 0), reloaded.StartTime);
        // The EXDATE cancellation survived.
        Assert.Contains(new DateOnly(2026, 3, 9), reloaded.ExceptionDates);
        // The RECURRENCE-ID override survived (keyed by its original date).
        Assert.True(reloaded.Overrides.ContainsKey(new DateOnly(2026, 3, 16)));
        Assert.Equal("Standup (moved)", reloaded.Overrides[new DateOnly(2026, 3, 16)].NewTitle);

        await host.StopAsync();
    }

    [Fact(DisplayName = "inc-0: calendar event durably survives a host restart")]
    public async Task CalendarEvent_Survives_HostRestart()
    {
        var seed = FreshRootSeed();
        CalendarEventId id;

        using (var host = await StartKeyedHostAsync(seed))
        {
            var ev = RecurringMorningStandup();
            id = ev.Id;
            await EventStore(host).SaveAsync(ev);
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        // A brand-new host over the SAME keyed file — the durable store re-opens and the series is there.
        using (var host = await StartKeyedHostAsync(seed))
        {
            var reloaded = await EventStore(host).GetAsync(TenantA, id);
            Assert.NotNull(reloaded);
            Assert.Equal("Morning standup", reloaded!.Title);
            await host.StopAsync();
        }
    }

    [Fact(DisplayName = "inc-0: calendar event get is cross-tenant isolated")]
    public async Task CalendarEvent_Get_IsCrossTenantIsolated()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = EventStore(host);

        var ev = RecurringMorningStandup(); // TenantA
        await store.SaveAsync(ev);

        // Tenant B asking for Tenant A's event id gets null (composite (tenant, id) key).
        Assert.Null(await store.GetAsync(TenantB, ev.Id));
        Assert.Empty(await store.ListAsync(TenantB));
        Assert.Single(await store.ListAsync(TenantA));

        await host.StopAsync();
    }

    [Fact(DisplayName = "inc-0: re-save replaces the series master (upsert on the composite key)")]
    public async Task CalendarEvent_ReSave_Replaces()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = EventStore(host);

        var ev = RecurringMorningStandup();
        await store.SaveAsync(ev);

        // Same-anchor rename (Start/End unchanged) — allowed while occurrence edits exist; preserves overrides.
        ev.EditMaster("Renamed standup", ev.Start, ev.End, Actor);
        await store.SaveAsync(ev);

        var reloaded = await store.GetAsync(TenantA, ev.Id);
        Assert.Equal("Renamed standup", reloaded!.Title);
        Assert.Single(await store.ListAsync(TenantA)); // upsert, not a second row

        await host.StopAsync();
    }

    [Fact(DisplayName = "inc-0: resource availability round-trips across a context reopen")]
    public async Task ResourceAvailability_RoundTrips_AcrossContextReopen()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = AvailabilityStore(host);

        var doctor = ParticipantRef.Party("party-dr-smith");
        var day = new DateOnly(2026, 3, 4);
        var availability = ResourceAvailability.Create(TenantA, doctor, "UTC")
            .AddWindow(AvailabilityWindow.Create(day, new TimeOnly(9, 0), new TimeOnly(17, 0)));
        await store.SaveAsync(availability);

        var reloaded = await store.GetAsync(TenantA, doctor);
        Assert.NotNull(reloaded);
        Assert.Equal("UTC", reloaded!.Timezone);
        var window = Assert.Single(reloaded.Windows);
        Assert.Equal(new TimeOnly(9, 0), window.StartTime);
        Assert.Equal(new TimeOnly(17, 0), window.EndTime);

        // Cross-tenant isolation + cross-resource isolation.
        Assert.Null(await store.GetAsync(TenantB, doctor));
        Assert.Null(await store.GetAsync(TenantA, ParticipantRef.Asset("room-7")));

        await host.StopAsync();
    }

    [Fact(DisplayName = "inc-0: the calendar context uses a distinct migration-history table")]
    public async Task Calendar_UsesDistinctMigrationsHistoryTable()
    {
        var seed = FreshRootSeed();
        using (var host = await StartKeyedHostAsync(seed))
        {
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        var dek = new SqlCipherKeyDerivation().DeriveSqlCipherKey(
            seed, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);
        using var conn = new SqliteConnection($"Data Source={_dbPath};");
        conn.Open();
        using (var keyCmd = conn.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(dek)}'\";";
            keyCmd.ExecuteNonQuery();
        }
        using var q = conn.CreateCommand();
        q.CommandText =
            "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE '%MigrationsHistory%';";
        var historyTables = new List<string>();
        using (var reader = q.ExecuteReader())
        {
            while (reader.Read())
            {
                historyTables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("__EFMigrationsHistory", historyTables);
        Assert.Contains(NodeLocalCalendarDbContext.MigrationsHistoryTableName, historyTables);
    }

    [Fact(DisplayName = "inc-0: calendar rows are encrypted at rest (raw unkeyed open fails)")]
    public async Task CalendarRows_AreEncryptedAtRest()
    {
        using (var host = await StartKeyedHostAsync(FreshRootSeed()))
        {
            await EventStore(host).SaveAsync(RecurringMorningStandup());
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        // The file does not begin with the plaintext SQLite magic header.
        var bytes = await File.ReadAllBytesAsync(_dbPath);
        var magic = "SQLite format 3\0"u8.ToArray();
        var headerMatches = bytes.Length >= magic.Length
            && bytes.AsSpan(0, magic.Length).SequenceEqual(magic);
        Assert.False(headerMatches, "calendar store begins with plaintext SQLite header (SC-1 violation)");

        // A raw UNKEYED open cannot read the calendar table.
        using var raw = new SqliteConnection($"Data Source={_dbPath};");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM calendar_events;";
        Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
