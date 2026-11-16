using Harborline.Api.Foundation.Assets.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TenantId = Harborline.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.Foundation.Events.Tests;

public sealed class SqliteEventReaderTests
{
    [Fact]
    public async Task Page_query_seeks_tenant_and_position_without_sorting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var store = new SqliteDomainEventStore(connection, new TestClock());
        await store.ApplyMigrationsAsync();
        var query = (string)typeof(SqliteDomainEventStore)
            .GetField("PageByPositionSql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + query;
        cmd.Parameters.AddWithValue("$tenant_id", "tenant-a");
        cmd.Parameters.AddWithValue("$position", 100000L);
        cmd.Parameters.AddWithValue("$limit", 100);
        var plan = new List<string>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) plan.Add(rows.GetString(3));
        Assert.Contains(plan, detail => detail.Contains("idx_event_tenant_positions_tenant_position", StringComparison.Ordinal)
            && detail.Contains("tenant_id=? AND position>?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
    }

    // Codex P2 (r4578966615): the tick's tenant list must cost one index seek
    // per tenant, not a pass over event history (CWE-1067; SQLite min/max
    // optimisation, sqlite.org/optoverview.html#minmax).
    [Fact]
    public async Task Tenant_scan_seeks_tenant_index_instead_of_scanning_events()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var store = new SqliteDomainEventStore(connection, new TestClock());
        await store.ApplyMigrationsAsync();
        var query = (string)typeof(SqliteEventReader)
            .GetField("TenantScanSql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + query;
        var plan = new List<string>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) plan.Add(rows.GetString(3));
        Assert.Contains(plan, detail => detail.Contains("SEARCH domain_events", StringComparison.Ordinal)
            && detail.Contains("idx_domain_events_tenant_eventid", StringComparison.Ordinal));
        // The recursive step seeks past the previous tenant; without it the step walks the whole index.
        Assert.Contains(plan, detail => detail.Contains("idx_domain_events_tenant_eventid (tenant_id>?)", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("SCAN domain_events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tenant_scan_returns_each_interleaved_tenant_once_in_order()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        foreach (var (id, tenant) in new[] { ("e1", "tenant-c"), ("e2", "tenant-a"), ("e3", "tenant-b"), ("e4", "tenant-a"), ("e5", "tenant-c"), ("e6", "tenant-b") })
            await AppendAsync(store, clock, id, tenant);
        var reader = new SqliteEventReader(connection, store, clock);
        var tenants = await reader.GetTenantsAsync(CancellationToken.None);
        Assert.Equal(new[] { "tenant-a", "tenant-b", "tenant-c" }, tenants.Select(t => t.Value));
    }

    [Fact]
    public async Task Tenant_scan_of_no_events_is_empty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        Assert.Empty(await reader.GetTenantsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Migration_indexes_existing_positions_per_tenant_and_new_appends()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var store = new SqliteDomainEventStore(connection, new TestClock());
        await store.ApplyMigrationsAsync();
        var clock = new TestClock();
        foreach (var (id, tenant) in new[] { ("z", "tenant-a"), ("b", "tenant-b"), ("a", "tenant-a") })
            await store.AppendAsync(new DomainEventEnvelope<string>
            {
                EventId = id, EventType = "Test.Happened", SchemaVersion = 1,
                OccurredAt = clock.GetUtcNow(), TenantId = new TenantId(tenant), OriginatingReplicaId = new ReplicaId("replica"),
                IdempotencyKey = id, Payload = "payload"
            });
        await using (var oldSchema = connection.CreateCommand())
        {
            oldSchema.CommandText = "DROP TRIGGER event_positions_assign_tenant; DROP TABLE event_tenant_positions;";
            await oldSchema.ExecuteNonQueryAsync();
        }
        await store.ApplyMigrationsAsync();
        await store.ApplyMigrationsAsync();
        var page = await store.GetAfterPositionAsync(new TenantId("tenant-a"), 1, 10);
        Assert.Equal(new[] { "a" }, page.Select(e => e.EventId));
        Assert.Equal(3, Assert.Single(page).Position);
        await store.AppendAsync(new DomainEventEnvelope<string>
        {
            EventId = "0", EventType = "Test.Happened", SchemaVersion = 1,
            OccurredAt = clock.GetUtcNow(), TenantId = new TenantId("tenant-b"), OriginatingReplicaId = new ReplicaId("replica"),
            IdempotencyKey = "0", Payload = "payload"
        });
        var next = await store.GetAfterPositionAsync(new TenantId("tenant-b"), 2, 10);
        Assert.Equal("0", Assert.Single(next).EventId);
        Assert.True(next[0].Position > 3);
    }

    [Fact]
    public void Solution_lists_foundation_events_tests()
    {
        var solution = Path.GetFullPath("../../../../../../Harborline.Api.slnx", AppContext.BaseDirectory);
        var xml = System.Xml.Linq.XDocument.Load(solution);
        Assert.Contains(xml.Descendants("Project"), project =>
            (string?)project.Attribute("Path") == "packages/foundation-events/tests/Harborline.Foundation.Events.Tests.csproj");
    }

    [Fact]
    public async Task Failure_waits_until_recorded_retry_before_handler_runs_and_cursor_advances()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var tenant = new TenantId("tenant-a");
        var handler = new FailOnceHandler();
        await reader.RegisterHandlerAsync("handler", null, handler);
        await store.AppendAsync(new DomainEventEnvelope<string>
        {
            EventId = "event-a", EventType = "Test.Happened", SchemaVersion = 1,
            OccurredAt = clock.GetUtcNow(), TenantId = tenant, OriginatingReplicaId = new ReplicaId("replica"),
            IdempotencyKey = "event-a", Payload = "payload"
        });

        Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        clock.Now = clock.Now.AddSeconds(29);
        Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        Assert.Equal(1, handler.Calls);
        await using (var cursor = connection.CreateCommand())
        {
            cursor.CommandText = "SELECT COUNT(*) FROM event_handler_positions";
            Assert.Equal(0L, await cursor.ExecuteScalarAsync());
        }
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(1, await reader.DrainOnceAsync(tenant, 10));
        Assert.Equal(2, handler.Calls);
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT position FROM event_handler_positions WHERE handler_id = 'handler' AND tenant_id = 'tenant-a'";
        Assert.Equal(1L, await check.ExecuteScalarAsync());
        check.CommandText = "SELECT resolved_at FROM event_handler_failures";
        Assert.Equal("2026-01-01T00:00:30.0000000+00:00", await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EventDispatcherHost_eighth_failure_logs_hold_once_at_error()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var logger = new ListLogger();
        var reader = new SqliteEventReader(connection, store, clock, logger);
        await reader.RegisterHandlerAsync("held", null, new AlwaysFailHandler());
        await AppendAsync(store, clock, "event-a", "tenant-a");
        var host = new EventDispatcherHost(reader, logger);
        foreach (var seconds in new[] { 0, 30, 120, 600, 3600, 21600, 86400, 259200, 86400 })
        {
            clock.Now = clock.Now.AddSeconds(seconds);
            await host.DrainOnceAsync();
        }
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("held", entry.Message);
        Assert.Contains("tenant-a", entry.Message);
    }

    [Fact]
    public async Task Eighth_failure_holds_handler_visibly_and_blocks_later_events()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var tenant = new TenantId("tenant-a");
        var handler = new AlwaysFailHandler();
        await reader.RegisterHandlerAsync("held", null, handler);
        foreach (var id in new[] { "event-a", "event-b" })
            await store.AppendAsync(new DomainEventEnvelope<string>
            {
                EventId = id, EventType = "Test.Happened", SchemaVersion = 1,
                OccurredAt = clock.GetUtcNow(), TenantId = tenant, OriginatingReplicaId = new ReplicaId("replica"),
                IdempotencyKey = id, Payload = "payload"
            });
        await DriveToHoldAsync(reader, clock, tenant);
        await using (var waits = connection.CreateCommand())
        {
            waits.CommandText = "SELECT attempt_number, failed_at, next_retry_at FROM event_handler_failures ORDER BY attempt_number";
            var recorded = new List<(long Attempt, TimeSpan? Wait)>();
            await using var rows = await waits.ExecuteReaderAsync();
            while (await rows.ReadAsync())
                recorded.Add((rows.GetInt64(0), rows.IsDBNull(2) ? null
                    : DateTimeOffset.Parse(rows.GetString(2), System.Globalization.CultureInfo.InvariantCulture)
                      - DateTimeOffset.Parse(rows.GetString(1), System.Globalization.CultureInfo.InvariantCulture)));
            // T-1227 Log, 2026-11-16 (control main): "seven waits, 30 s / 2 m / 10 m /
            // 1 h / 6 h / 24 h / 72 h", and the eighth failure holds (no retry time).
            Assert.Equal(new (long, TimeSpan?)[]
            {
                (1, TimeSpan.FromSeconds(30)), (2, TimeSpan.FromMinutes(2)), (3, TimeSpan.FromMinutes(10)),
                (4, TimeSpan.FromHours(1)), (5, TimeSpan.FromHours(6)), (6, TimeSpan.FromHours(24)),
                (7, TimeSpan.FromHours(72)), (8, null),
            }, recorded);
        }
        clock.Now = clock.Now.AddDays(10);
        Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        Assert.Equal(8, handler.Calls);
        var held = Assert.Single(await reader.ReadHeldHandlersAsync(tenant));
        Assert.Equal("held", held.HandlerId);
        Assert.Equal("tenant-a", held.TenantId.Value);
        Assert.Equal("event-a", held.EventId);
        Assert.Equal(8, held.AttemptCount);
        Assert.Empty(await reader.ReadHeldHandlersAsync(new TenantId("tenant-b")));
    }

    // T-1227 Log, 2026-11-16 (control main), from cross-cluster-event-bus-design.md §6:
    // the first failure, then retries after 30 s, 2 m, 10 m, 1 h, 6 h, 24 h and 72 h.
    private static async Task DriveToHoldAsync(SqliteEventReader reader, TestClock clock, TenantId tenant)
    {
        foreach (var seconds in new[] { 0, 30, 120, 600, 3600, 21600, 86400, 259200 })
        {
            clock.Now = clock.Now.AddSeconds(seconds);
            Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        }
    }

    [Fact]
    public async Task Released_hold_counts_attempts_again_from_one()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var tenant = new TenantId("tenant-a");
        var handler = new AlwaysFailHandler();
        await reader.RegisterHandlerAsync("held", null, handler);
        await AppendAsync(store, clock, "event-a", "tenant-a");
        await DriveToHoldAsync(reader, clock, tenant);
        await using (var release = connection.CreateCommand())
        {
            release.CommandText = "UPDATE event_handler_failures SET resolved_at = '2026-01-10T00:00:00.0000000+00:00'";
            await release.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        Assert.Equal(9, handler.Calls);
        Assert.Empty(await reader.ReadHeldHandlersAsync(tenant));
        await using var latest = connection.CreateCommand();
        latest.CommandText = "SELECT attempt_number FROM event_handler_failures WHERE resolved_at IS NULL";
        Assert.Equal(1L, await latest.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Tick_drains_later_tenant_and_logs_when_earlier_tenant_read_throws()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var handler = new RecordingHandler();
        await reader.RegisterHandlerAsync("handler", null, handler);
        await using (var unreadable = connection.CreateCommand())
        {
            unreadable.CommandText = """
                INSERT INTO domain_events(event_id,event_type,schema_version,occurred_at,recorded_at_utc,tenant_id,originating_replica_id,idempotency_key,producer_cluster,payload_json)
                VALUES ('event-a','Test.Happened',1,'not-a-time','2026-01-01T00:00:00Z','tenant-a','replica','event-a','test','{}');
                """;
            await unreadable.ExecuteNonQueryAsync();
        }
        await AppendAsync(store, clock, "event-b", "tenant-b");
        var logger = new ListLogger();
        await new EventDispatcherHost(reader, logger).DrainOnceAsync();
        Assert.Equal(new[] { "event-b" }, handler.Seen);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<FormatException>(entry.Exception);
        Assert.Contains("tenant-a", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tick_lets_cancellation_through_instead_of_moving_to_next_tenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        using var cancellation = new CancellationTokenSource();
        var handler = new CancellingHandler(cancellation);
        await reader.RegisterHandlerAsync("handler", null, handler);
        await AppendAsync(store, clock, "event-a", "tenant-a");
        await AppendAsync(store, clock, "event-b", "tenant-b");
        var logger = new ListLogger();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new EventDispatcherHost(reader, logger).DrainOnceAsync(cancellation.Token));
        Assert.Equal(new[] { "tenant-a" }, handler.Seen);
        Assert.Empty(logger.Entries);
    }

    // Failure episodes (CWE-779; OWASP Logging Cheat Sheet): one Error with the
    // exception when a tenant's drain starts failing, Debug while it repeats,
    // one Information on recovery.
    [Fact]
    public async Task EventDispatcherHost_scan_failure_logs_one_error_across_polls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        var reader = new SqliteEventReader(connection, store, clock);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new ListLogger { OnLog = count => { if (count == 3) observed.TrySetResult(); } };
        using var host = new EventDispatcherHost(reader, logger, TimeSpan.FromMilliseconds(10));
        await host.StartAsync(CancellationToken.None);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { LogLevel.Error, LogLevel.Debug, LogLevel.Debug }, logger.Entries.Select(entry => entry.Level));
    }

    [Fact]
    public async Task EventDispatcherHost_scan_logs_changed_exception_and_recovery()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        var reader = new SqliteEventReader(connection, store, clock);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new ListLogger
        {
            OnLog = count =>
            {
                if (count == 2) connection.Close();
                if (count == 3)
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE domain_events (tenant_id TEXT); CREATE INDEX idx_domain_events_tenant_eventid ON domain_events (tenant_id);";
                    command.ExecuteNonQuery();
                }
                if (count == 4) observed.TrySetResult();
            }
        };
        using var host = new EventDispatcherHost(reader, logger, TimeSpan.FromMilliseconds(10));
        await host.StartAsync(CancellationToken.None);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { LogLevel.Error, LogLevel.Debug, LogLevel.Error, LogLevel.Information }, logger.Entries.Select(entry => entry.Level));
        Assert.IsType<SqliteException>(logger.Entries[0].Exception);
        Assert.IsType<InvalidOperationException>(logger.Entries[2].Exception);
        Assert.Contains("1 failed polls", logger.Entries[3].Message);
    }

    [Fact]
    public async Task Two_failing_polls_log_one_error()
    {
        var (connection, reader) = await UnreadableTenantAsync();
        await using var _ = connection;
        var logger = new ListLogger();
        var host = new EventDispatcherHost(reader, logger);
        await host.DrainOnceAsync();
        await host.DrainOnceAsync();
        Assert.Equal(new[] { LogLevel.Error, LogLevel.Debug }, logger.Entries.Select(e => e.Level));
        Assert.IsType<FormatException>(logger.Entries[0].Exception);
        Assert.Null(logger.Entries[1].Exception);
    }

    [Fact]
    public async Task Failure_recovery_failure_logs_two_errors_and_one_information()
    {
        var (connection, reader) = await UnreadableTenantAsync();
        await using var _ = connection;
        var logger = new ListLogger();
        var host = new EventDispatcherHost(reader, logger);
        await host.DrainOnceAsync();
        await host.DrainOnceAsync();
        await ExecAsync(connection, "UPDATE domain_events SET occurred_at = '2026-01-01T00:00:00.0000000+00:00' WHERE event_id = 'event-a'");
        await host.DrainOnceAsync();
        await InsertUnreadableAsync(connection, "event-c", "tenant-a");
        await host.DrainOnceAsync();
        Assert.Equal(new[] { LogLevel.Error, LogLevel.Debug, LogLevel.Information, LogLevel.Error }, logger.Entries.Select(e => e.Level));
        Assert.Contains("recovered after 2 failed polls", logger.Entries[2].Message, StringComparison.Ordinal);
        Assert.Contains("tenant-a", logger.Entries[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exception_type_change_logs_a_second_error()
    {
        var (connection, reader) = await UnreadableTenantAsync();
        await using var _ = connection;
        var logger = new ListLogger();
        var host = new EventDispatcherHost(reader, logger);
        await host.DrainOnceAsync();
        await host.DrainOnceAsync();
        await ExecAsync(connection, "DROP TABLE event_handler_positions");
        await host.DrainOnceAsync();
        Assert.Equal(new[] { LogLevel.Error, LogLevel.Debug, LogLevel.Error }, logger.Entries.Select(e => e.Level));
        Assert.IsType<FormatException>(logger.Entries[0].Exception);
        Assert.IsType<SqliteException>(logger.Entries[2].Exception);
    }

    private static async Task<(SqliteConnection, SqliteEventReader)> UnreadableTenantAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        await reader.RegisterHandlerAsync("handler", null, new RecordingHandler());
        await InsertUnreadableAsync(connection, "event-a", "tenant-a");
        return (connection, reader);
    }

    private static Task InsertUnreadableAsync(SqliteConnection connection, string id, string tenant)
        => ExecAsync(connection, $"""
            INSERT INTO domain_events(event_id,event_type,schema_version,occurred_at,recorded_at_utc,tenant_id,originating_replica_id,idempotency_key,producer_cluster,payload_json)
            VALUES ('{id}','Test.Happened',1,'not-a-time','2026-01-01T00:00:00Z','{tenant}','replica','{id}','test','{"{}"}');
            """);

    private static async Task ExecAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public void Store_position_is_not_public_on_raw_events()
        => Assert.Null(typeof(RawDomainEvent).GetProperty("Position"));

    // Fable P3-c: a page-by-position read is useless outside the package once
    // Position is internal (CWE-200, as for P3-4).
    [Fact]
    public void Store_page_by_position_is_not_public()
    {
        Assert.Null(typeof(IDomainEventStore).GetMethod("GetAfterPositionAsync"));
        Assert.Null(typeof(SqliteDomainEventStore).GetMethod("GetAfterPositionAsync"));
    }

    [Fact]
    public async Task Failed_position_migration_rolls_back_so_a_later_append_commits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"t1227-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using (var conflicting = connection.CreateCommand())
            {
                conflicting.CommandText = "CREATE TABLE event_handler_positions (handler_id TEXT NOT NULL);";
                await conflicting.ExecuteNonQueryAsync();
            }
            var clock = new TestClock();
            var store = new SqliteDomainEventStore(connection, clock);
            await Assert.ThrowsAsync<SqliteException>(() => store.ApplyMigrationsAsync());
            await AppendAsync(store, clock, "event-a", "tenant-a");
            await using var other = new SqliteConnection(connectionString);
            await other.OpenAsync();
            await using var count = other.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM domain_events";
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rerun_migrations_consume_no_positions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        foreach (var id in new[] { "event-a", "event-b", "event-c" })
            await AppendAsync(store, clock, id, "tenant-a");
        await store.ApplyMigrationsAsync();
        await store.ApplyMigrationsAsync();
        await AppendAsync(store, clock, "event-d", "tenant-a");
        var page = await store.GetAfterPositionAsync(new TenantId("tenant-a"), 3, 10);
        Assert.Equal(4L, Assert.Single(page).Position);
    }

    private static Task<string> AppendAsync(SqliteDomainEventStore store, TestClock clock, string id, string tenant)
        => store.AppendAsync(new DomainEventEnvelope<string>
        {
            EventId = id, EventType = "Test.Happened", SchemaVersion = 1,
            OccurredAt = clock.GetUtcNow(), TenantId = new TenantId(tenant), OriginatingReplicaId = new ReplicaId("replica"),
            IdempotencyKey = id, Payload = "payload"
        });

    private sealed class CancellingHandler(CancellationTokenSource cancellation) : IEventHandler<RawDomainEvent>
    {
        public List<string> Seen { get; } = [];
        public Task HandleAsync(DomainEventEnvelope<RawDomainEvent> envelope, CancellationToken cancellationToken = default)
        {
            Seen.Add(envelope.TenantId.Value);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class ListLogger : ILogger<EventDispatcherHost>, ILogger<SqliteEventReader>
    {
        public Action<int>? OnLog { get; init; }
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception, formatter(state, exception)));
            OnLog?.Invoke(Entries.Count);
        }
    }

    [Fact]
    public async Task Tick_drains_each_tenant_even_when_one_handler_fails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var handler = new TenantHandler();
        await reader.RegisterHandlerAsync("handler", null, handler);
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
            await store.AppendAsync(new DomainEventEnvelope<string>
            {
                EventId = tenant, EventType = "Test.Happened", SchemaVersion = 1,
                OccurredAt = clock.GetUtcNow(), TenantId = new TenantId(tenant), OriginatingReplicaId = new ReplicaId("replica"),
                IdempotencyKey = tenant, Payload = "payload"
            });
        var host = new EventDispatcherHost(reader, NullLogger<EventDispatcherHost>.Instance);
        await host.DrainOnceAsync();
        Assert.Contains("tenant-a", handler.Seen);
        Assert.Contains("tenant-b", handler.Seen);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id FROM event_handler_positions WHERE handler_id = 'handler'";
        Assert.Equal("tenant-b", await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Later_commit_with_lower_id_is_delivered_after_cursor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var tenant = new TenantId("tenant-a");
        var handler = new RecordingHandler();
        await reader.RegisterHandlerAsync("handler", null, handler);
        foreach (var id in new[] { "z-event", "a-event" })
        {
            await store.AppendAsync(new DomainEventEnvelope<string>
            {
                EventId = id, EventType = "Test.Happened", SchemaVersion = 1,
                OccurredAt = clock.GetUtcNow(), TenantId = tenant, OriginatingReplicaId = new ReplicaId("replica"),
                IdempotencyKey = id, Payload = "payload"
            });
            await reader.DrainOnceAsync(tenant, 10);
        }
        Assert.Equal(new[] { "z-event", "a-event" }, handler.Seen);
    }

    private sealed class RecordingHandler : IEventHandler<RawDomainEvent>
    {
        public List<string> Seen { get; } = [];
        public Task HandleAsync(DomainEventEnvelope<RawDomainEvent> envelope, CancellationToken cancellationToken = default)
        {
            Seen.Add(envelope.EventId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Migration_preserves_existing_id_order_and_handler_cursor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        foreach (var name in new[] { "001-create-domain-events.sql", "002-create-handler-cursors.sql" })
        {
            using var stream = typeof(SqliteDomainEventStore).Assembly.GetManifestResourceStream("Harborline.Api.Foundation.Events.Sql." + name)!;
            using var text = new StreamReader(stream);
            await using var create = connection.CreateCommand();
            create.CommandText = await text.ReadToEndAsync();
            await create.ExecuteNonQueryAsync();
        }
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO domain_events(event_id,event_type,schema_version,occurred_at,recorded_at_utc,tenant_id,originating_replica_id,idempotency_key,producer_cluster,payload_json)
                VALUES ('z','Test.Happened',1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','tenant-a','replica','z','test','{}'),
                       ('a','Test.Happened',1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','tenant-a','replica','a','test','{}'),
                       ('y','Test.Happened',1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','tenant-b','replica','y','test','{}'),
                       ('b','Test.Happened',1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','tenant-b','replica','b','test','{}');
                INSERT INTO event_handler_cursors(handler_id,tenant_id,last_handled_event_id)
                VALUES ('handler','tenant-a','a'), ('handler','tenant-b','b');
                """;
            await seed.ExecuteNonQueryAsync();
        }
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        await store.ApplyMigrationsAsync();
        var tenant = new TenantId("tenant-a");
        var page = await store.GetAfterPositionAsync(tenant, 0, 10);
        Assert.Equal(new[] { "a", "z" }, page.Select(e => e.EventId));
        Assert.True(page[0].Position < page[1].Position);
        var translated = new List<string>();
        await using (var cursors = connection.CreateCommand())
        {
            cursors.CommandText = "SELECT tenant_id || ':' || position FROM event_handler_positions WHERE handler_id = 'handler' ORDER BY tenant_id";
            await using var rows = await cursors.ExecuteReaderAsync();
            while (await rows.ReadAsync()) translated.Add(rows.GetString(0));
        }
        Assert.Equal(new[] { "tenant-a:1", "tenant-b:2" }, translated);
        var reader = new SqliteEventReader(connection, store, clock);
        var handler = new RecordingHandler();
        await reader.RegisterHandlerAsync("handler", null, handler);
        await reader.DrainOnceAsync(tenant, 10);
        await reader.DrainOnceAsync(new TenantId("tenant-b"), 10);
        Assert.Equal(new[] { "z", "y" }, handler.Seen);
    }

    [Fact]
    public async Task Handler_runs_before_its_position_advances_and_cursors_are_independent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new TestClock();
        var store = new SqliteDomainEventStore(connection, clock);
        await store.ApplyMigrationsAsync();
        var reader = new SqliteEventReader(connection, store, clock);
        var handler = new ObservingHandler(connection);
        var other = new RecordingHandler();
        await reader.RegisterHandlerAsync("observer", null, handler);
        await reader.RegisterHandlerAsync("other", null, other);
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
            await store.AppendAsync(new DomainEventEnvelope<string>
            {
                EventId = tenant, EventType = "Test.Happened", SchemaVersion = 1,
                OccurredAt = clock.GetUtcNow(), TenantId = new TenantId(tenant), OriginatingReplicaId = new ReplicaId("replica"),
                IdempotencyKey = tenant, Payload = "payload"
            });
        await reader.DrainOnceAsync(new TenantId("tenant-a"), 10);
        await reader.DrainOnceAsync(new TenantId("tenant-b"), 10);
        Assert.Equal(new[] { "tenant-a", "tenant-b" }, other.Seen);
        Assert.Equal(2, handler.PriorPositions.Count);
        Assert.All(handler.PriorPositions, position => Assert.Equal(0L, position));
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event_handler_positions";
        Assert.Equal(4L, await cmd.ExecuteScalarAsync());
    }

    private sealed class ObservingHandler(SqliteConnection connection) : IEventHandler<RawDomainEvent>
    {
        public List<long> PriorPositions { get; } = [];
        public async Task HandleAsync(DomainEventEnvelope<RawDomainEvent> envelope, CancellationToken cancellationToken = default)
        {
            Assert.True(envelope.Payload.Position > 0);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE((SELECT position FROM event_handler_positions WHERE handler_id = 'observer' AND tenant_id = $tenant), 0)";
            cmd.Parameters.AddWithValue("$tenant", envelope.TenantId.Value);
            PriorPositions.Add((long)(await cmd.ExecuteScalarAsync(cancellationToken))!);
        }
    }

    private sealed class TenantHandler : IEventHandler<RawDomainEvent>
    {
        public List<string> Seen { get; } = [];
        public Task HandleAsync(DomainEventEnvelope<RawDomainEvent> envelope, CancellationToken cancellationToken = default)
        {
            Seen.Add(envelope.TenantId.Value);
            if (envelope.TenantId.Value == "tenant-a") throw new InvalidOperationException("failure");
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailHandler : IEventHandler<RawDomainEvent>
    {
        public int Calls { get; private set; }
        public Task HandleAsync(DomainEventEnvelope<RawDomainEvent> envelope, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("failure");
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailOnceHandler : IEventHandler<RawDomainEvent>
    {
        public int Calls { get; private set; }
        public Task HandleAsync(DomainEventEnvelope<RawDomainEvent> envelope, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1) throw new InvalidOperationException("failure");
            return Task.CompletedTask;
        }
    }
}
