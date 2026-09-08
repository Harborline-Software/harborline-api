using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Calendar;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Calendar;

/// <summary>
/// Calendar-productization #149 slice C1 binary gate — the durable EF owned-calendar store
/// (<see cref="NodeEfCalendarStore"/>) round-trips on the REAL keyed (SQLCipher-encrypted) SQLite store
/// through the same SC-1 registration path the host uses, and the new <c>calendars</c> migration applies
/// to BOTH a fresh db and an existing (dogfood-shaped) db that predates the migration. Mirrors
/// <c>NodeEfCalendarStoreTests</c>.
/// </summary>
public sealed class NodeEfCalendarCollectionStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Owner = Guid.NewGuid();

    private readonly string _dir;
    private readonly string _dbPath;

    public NodeEfCalendarCollectionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-cal-collection-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Boots a host with the SC-1 registration; the encryption guard migrates the calendar context (incl. the new calendars table).</summary>
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

    private static NodeEfCalendarStore CalendarStore(IHost host)
        => new(host.Services.GetRequiredService<IDbContextFactory<NodeLocalCalendarDbContext>>());

    private static NodeEfCalendarEventStore EventStore(IHost host)
        => new(host.Services.GetRequiredService<IDbContextFactory<NodeLocalCalendarDbContext>>());

    [Fact(DisplayName = "C1: an owned calendar round-trips across a context reopen (all fields intact)")]
    public async Task Calendar_RoundTrips_AcrossContextReopen()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = CalendarStore(host);

        var room = ParticipantRef.Asset("room-7");
        var saved = OwnedCalendar.Create(TenantA, "Exam Room 3", CalendarKind.Resource, Owner,
            resourceRef: room, colorToken: "calendar-accent-2");
        await store.SaveAsync(saved);

        var reloaded = await store.GetAsync(TenantA, saved.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("Exam Room 3", reloaded!.Name);
        Assert.Equal(CalendarKind.Resource, reloaded.Kind);
        Assert.Equal("calendar-accent-2", reloaded.ColorToken);
        Assert.Equal("room-7", reloaded.ResourceRef!.Value);
        Assert.Equal(ParticipantKind.Asset, reloaded.ResourceRef.Kind);

        await host.StopAsync();
    }

    [Fact(DisplayName = "C1: an owned calendar durably survives a host restart")]
    public async Task Calendar_Survives_HostRestart()
    {
        var seed = FreshRootSeed();
        CalendarId id;

        using (var host = await StartKeyedHostAsync(seed))
        {
            var cal = OwnedCalendar.CreateDefault(TenantA, Owner);
            id = cal.Id;
            await CalendarStore(host).SaveAsync(cal);
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        using (var host = await StartKeyedHostAsync(seed))
        {
            var reloaded = await CalendarStore(host).GetAsync(TenantA, id);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.IsDefault);
            Assert.Equal(OwnedCalendar.DefaultNameKey, reloaded.Name);
            await host.StopAsync();
        }
    }

    [Fact(DisplayName = "C1: owned-calendar get/list are cross-tenant isolated")]
    public async Task Calendar_CrossTenantIsolated()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = CalendarStore(host);

        var cal = OwnedCalendar.CreateDefault(TenantA, Owner); // TenantA
        await store.SaveAsync(cal);

        Assert.Null(await store.GetAsync(TenantB, cal.Id));
        Assert.Empty(await store.ListAsync(TenantB));
        Assert.Single(await store.ListAsync(TenantA));

        await host.StopAsync();
    }

    [Fact(DisplayName = "C1: owned-calendar list returns the default first")]
    public async Task Calendar_List_DefaultFirst()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var store = CalendarStore(host);

        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(OwnedCalendar.Create(TenantA, "Team", CalendarKind.Team, Owner, createdAt: now));
        await store.SaveAsync(OwnedCalendar.CreateDefault(TenantA, Owner, createdAt: now.AddMinutes(1)));

        var list = await store.ListAsync(TenantA);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsDefault);

        await host.StopAsync();
    }

    [Fact(DisplayName = "C1: calendar rows are encrypted at rest (raw unkeyed open of the calendars table fails)")]
    public async Task CalendarRows_AreEncryptedAtRest()
    {
        using (var host = await StartKeyedHostAsync(FreshRootSeed()))
        {
            await CalendarStore(host).SaveAsync(OwnedCalendar.CreateDefault(TenantA, Owner));
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        using var raw = new SqliteConnection($"Data Source={_dbPath};");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM calendars;";
        Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
    }

    [Fact(DisplayName = "C1: the calendars migration applies to an EXISTING (dogfood-shaped) db that predates it")]
    public async Task CalendarsMigration_AppliesToExistingDb()
    {
        var seed = FreshRootSeed();
        CalendarEventId eventId;

        // (1) Boot once so the db exists + has calendar_events (dogfood-shaped data) + a calendars table.
        using (var host = await StartKeyedHostAsync(seed))
        {
            var ev = CalendarEvent.Create(
                TenantA, "Rent due", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1), Owner,
                rrule: "FREQ=MONTHLY;BYMONTHDAY=1", timezone: "UTC");
            eventId = ev.Id;
            await EventStore(host).SaveAsync(ev);
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        // (2) Simulate a db migrated BEFORE the calendars migration existed: drop the calendars table AND
        //     delete its applied-migration history row. The calendar_events table + CalendarInitial history
        //     stay (the dogfood-shaped data must survive the upgrade).
        var dek = new SqlCipherKeyDerivation().DeriveSqlCipherKey(
            seed, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);
        using (var conn = new SqliteConnection($"Data Source={_dbPath};"))
        {
            conn.Open();
            using (var keyCmd = conn.CreateCommand())
            {
                keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(dek)}'\";";
                keyCmd.ExecuteNonQuery();
            }
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText =
                    "DROP TABLE IF EXISTS calendars; " +
                    $"DELETE FROM {NodeLocalCalendarDbContext.MigrationsHistoryTableName} " +
                    "WHERE MigrationId LIKE '%CalendarCollectionInitial';";
                drop.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();

        // (3) Reboot — MigrateAsync re-applies CalendarCollectionInitial onto the existing db: the calendars
        //     table is recreated AND the pre-existing calendar_event survives untouched.
        using (var host = await StartKeyedHostAsync(seed))
        {
            // The migrated calendars table is usable.
            var cal = OwnedCalendar.CreateDefault(TenantA, Owner);
            await CalendarStore(host).SaveAsync(cal);
            Assert.Single(await CalendarStore(host).ListAsync(TenantA));

            // The dogfood-shaped calendar_event survived the upgrade.
            var survived = await EventStore(host).GetAsync(TenantA, eventId);
            Assert.NotNull(survived);
            Assert.Equal("Rent due", survived!.Title);

            await host.StopAsync();
        }
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
