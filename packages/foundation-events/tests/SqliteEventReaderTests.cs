using Harborline.Api.Foundation.Assets.Common;
using Microsoft.Data.Sqlite;
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
            cursor.CommandText = "SELECT COUNT(*) FROM event_handler_cursors";
            Assert.Equal(0L, await cursor.ExecuteScalarAsync());
        }
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(1, await reader.DrainOnceAsync(tenant, 10));
        Assert.Equal(2, handler.Calls);
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM event_handler_cursors";
        Assert.Equal(1L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Seventh_failure_holds_handler_visibly_and_blocks_later_events()
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
        foreach (var seconds in new[] { 0, 30, 120, 600, 3600, 21600, 86400 })
        {
            clock.Now = clock.Now.AddSeconds(seconds);
            Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        }
        clock.Now = clock.Now.AddDays(10);
        Assert.Equal(0, await reader.DrainOnceAsync(tenant, 10));
        Assert.Equal(7, handler.Calls);
        var held = Assert.Single(await reader.ReadHeldHandlersAsync());
        Assert.Equal("held", held.HandlerId);
        Assert.Equal("tenant-a", held.TenantId.Value);
        Assert.Equal("event-a", held.EventId);
        Assert.Equal(7, held.AttemptCount);
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
        var host = new EventDispatcherHost(reader);
        await host.DrainOnceAsync();
        Assert.Contains("tenant-a", handler.Seen);
        Assert.Contains("tenant-b", handler.Seen);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id FROM event_handler_cursors WHERE handler_id = 'handler'";
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
                       ('a','Test.Happened',1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','tenant-a','replica','a','test','{}');
                INSERT INTO event_handler_cursors(handler_id,tenant_id,last_handled_event_id) VALUES ('handler','tenant-a','a');
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
        var reader = new SqliteEventReader(connection, store, clock);
        var handler = new RecordingHandler();
        await reader.RegisterHandlerAsync("handler", null, handler);
        await reader.DrainOnceAsync(tenant, 10);
        Assert.Equal(new[] { "z" }, handler.Seen);
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
