using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Events;

/// <summary>
/// SQLite-backed implementation of <see cref="IDomainEventStore"/>.
/// Append-only; idempotent on
/// <c>(<see cref="DomainEventEnvelope{TPayload}.TenantId"/>,
/// <see cref="DomainEventEnvelope{TPayload}.IdempotencyKey"/>)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>cross-cluster-event-bus-design.md</c> §1 storage shape +
/// the <see cref="ApplyMigrationsAsync"/> migration script
/// (<c>Sql/001-create-domain-events.sql</c> embedded resource).
/// </para>
/// <para>
/// Connection lifetime is owned by the caller — the store does NOT
/// dispose the supplied <see cref="SqliteConnection"/>. The
/// composition root opens the connection (typically a long-lived
/// per-tenant connection) and passes it in.
/// </para>
/// </remarks>
public sealed class SqliteDomainEventStore : IDomainEventStore
{
    private const string PageByPositionSql = """
        SELECT e.event_id, e.event_type, e.schema_version, e.occurred_at, e.recorded_at_utc,
               e.tenant_id, e.originating_replica_id, e.idempotency_key,
               e.causation_id, e.correlation_id, e.producer_cluster, e.payload_json, p.position
        FROM event_tenant_positions t INDEXED BY idx_event_tenant_positions_tenant_position
        CROSS JOIN event_positions p ON p.position = t.position
        CROSS JOIN domain_events e ON e.event_id = p.event_id
        WHERE t.tenant_id = $tenant_id AND t.position > $position
        ORDER BY t.position LIMIT $limit;
        """;
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _jsonOptions;

    public SqliteDomainEventStore(SqliteConnection connection, TimeProvider? clock = null)
    {
        _connection  = connection ?? throw new ArgumentNullException(nameof(connection));
        _clock       = clock ?? throw new ArgumentNullException(nameof(clock));
        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// Apply the foundation-events migrations, including event positions. Idempotent — safe to call on every host start. The
    /// composition root invokes this once at startup, or
    /// <c>AddFoundationEvents()</c> (PR 6) does it during DI build.
    /// </summary>
    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var resourceName in new[]
                 {
                     "001-create-domain-events.sql",
                     "002-create-handler-cursors.sql",
                 })
        {
            var sql = LoadEmbeddedSql(resourceName);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Disposing the transaction uncommitted rolls back, so a failed
        // statement never leaves the shared connection inside a transaction.
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var positions = _connection.CreateCommand();
        positions.Transaction = tx;
        positions.CommandText = """
            CREATE TABLE IF NOT EXISTS event_positions (
                position INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE REFERENCES domain_events(event_id)
            );
            CREATE TABLE IF NOT EXISTS event_tenant_positions (
                tenant_id TEXT NOT NULL,
                position INTEGER NOT NULL PRIMARY KEY REFERENCES event_positions(position)
            );
            CREATE INDEX IF NOT EXISTS idx_event_tenant_positions_tenant_position
                ON event_tenant_positions(tenant_id, position);
            CREATE TRIGGER IF NOT EXISTS event_positions_assign_tenant AFTER INSERT ON event_positions
            BEGIN
                INSERT INTO event_tenant_positions(tenant_id, position)
                    SELECT tenant_id, NEW.position FROM domain_events WHERE event_id = NEW.event_id;
            END;
            INSERT OR IGNORE INTO event_positions(event_id)
                SELECT event_id FROM domain_events
                WHERE NOT EXISTS (SELECT 1 FROM event_positions p WHERE p.event_id = domain_events.event_id)
                ORDER BY event_id;
            INSERT OR IGNORE INTO event_tenant_positions(tenant_id, position)
                SELECT e.tenant_id, p.position FROM event_positions p JOIN domain_events e ON e.event_id = p.event_id
                WHERE NOT EXISTS (SELECT 1 FROM event_tenant_positions t WHERE t.position = p.position);
            CREATE TRIGGER IF NOT EXISTS domain_events_assign_position AFTER INSERT ON domain_events
            BEGIN
                INSERT INTO event_positions(event_id) VALUES (NEW.event_id);
            END;
            CREATE TABLE IF NOT EXISTS event_handler_positions (
                handler_id TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                PRIMARY KEY (handler_id, tenant_id)
            );
            INSERT OR IGNORE INTO event_handler_positions(handler_id, tenant_id, position)
                SELECT c.handler_id, c.tenant_id, p.position
                FROM event_handler_cursors c JOIN event_positions p ON p.event_id = c.last_handled_event_id;
            """;
        await positions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> AppendAsync<TPayload>(
        DomainEventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var producerCluster = DeriveProducerCluster(envelope.EventType);
        var payloadJson     = JsonSerializer.Serialize(envelope.Payload, _jsonOptions);
        var recordedAtUtc   = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO domain_events (
                event_id, event_type, schema_version, occurred_at, recorded_at_utc,
                tenant_id, originating_replica_id, idempotency_key,
                causation_id, correlation_id, producer_cluster, payload_json
            ) VALUES (
                $event_id, $event_type, $schema_version, $occurred_at, $recorded_at_utc,
                $tenant_id, $originating_replica_id, $idempotency_key,
                $causation_id, $correlation_id, $producer_cluster, $payload_json
            )
            ON CONFLICT(tenant_id, idempotency_key) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$event_id",               envelope.EventId);
        cmd.Parameters.AddWithValue("$event_type",             envelope.EventType);
        cmd.Parameters.AddWithValue("$schema_version",         envelope.SchemaVersion);
        cmd.Parameters.AddWithValue("$occurred_at",            envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$recorded_at_utc",        recordedAtUtc);
        cmd.Parameters.AddWithValue("$tenant_id",              envelope.TenantId.Value);
        cmd.Parameters.AddWithValue("$originating_replica_id", envelope.OriginatingReplicaId.Value);
        cmd.Parameters.AddWithValue("$idempotency_key",        envelope.IdempotencyKey);
        cmd.Parameters.AddWithValue("$causation_id",           (object?)envelope.CausationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$correlation_id",         (object?)envelope.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$producer_cluster",       producerCluster);
        cmd.Parameters.AddWithValue("$payload_json",           payloadJson);

        var inserted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
            return envelope.EventId;

        // Dedup happened. Look up the existing event_id for the
        // matching (tenant_id, idempotency_key) and return it.
        await using var lookup = _connection.CreateCommand();
        lookup.CommandText = """
            SELECT event_id FROM domain_events
            WHERE tenant_id = $tenant_id AND idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        lookup.Parameters.AddWithValue("$tenant_id",       envelope.TenantId.Value);
        lookup.Parameters.AddWithValue("$idempotency_key", envelope.IdempotencyKey);
        var existing = (string?)await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return existing ?? throw new InvalidOperationException(
            "Idempotency-key dedup happened but no row found on lookup; "
            + "indicates a concurrent DELETE (forbidden per §4 append-only).");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawDomainEvent>> GetAfterCursorAsync(
        TenantId tenantId,
        string? afterEventId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Must be > 0.");

        long position = 0;
        if (afterEventId is not null)
        {
            await using var lookup = _connection.CreateCommand();
            lookup.CommandText = "SELECT p.position FROM event_positions p JOIN domain_events e ON e.event_id = p.event_id WHERE e.tenant_id = $tenant AND e.event_id = $id";
            lookup.Parameters.AddWithValue("$tenant", tenantId.Value);
            lookup.Parameters.AddWithValue("$id", afterEventId);
            var found = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (found is null) throw new ArgumentException("Cursor event not found for tenant.", nameof(afterEventId));
            position = Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }
        return await GetAfterPositionAsync(tenantId, position, batchSize, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawDomainEvent>> GetAfterPositionAsync(
        TenantId tenantId, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = PageByPositionSql;
        cmd.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        cmd.Parameters.AddWithValue("$position", afterPosition);
        cmd.Parameters.AddWithValue("$limit", batchSize);
        var results = new List<RawDomainEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadRow(reader));
        return results;
    }

    /// <inheritdoc />
    public async Task<RawDomainEvent?> FindByIdempotencyKeyAsync(
        TenantId tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, event_type, schema_version, occurred_at, recorded_at_utc,
                   tenant_id, originating_replica_id, idempotency_key,
                   causation_id, correlation_id, producer_cluster, payload_json,
                   (SELECT position FROM event_positions WHERE event_id = domain_events.event_id)
            FROM domain_events
            WHERE tenant_id = $tenant_id AND idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$tenant_id",       tenantId.Value);
        cmd.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadRow(reader);
    }

    private static string DeriveProducerCluster(string eventType)
    {
        var dotIdx = eventType.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx <= 0)
            throw new ArgumentException(
                $"EventType '{eventType}' is not cluster-qualified. "
                + "Per cross-cluster-event-bus-design.md §2: format is "
                + "<ClusterName-titlecase>.<PascalCaseVerbPastTense>.",
                nameof(eventType));
        return eventType[..dotIdx].ToLowerInvariant();
    }

    private static RawDomainEvent ReadRow(SqliteDataReader reader)
        // Bypass the TenantId / ReplicaId public-ctor reserved-prefix
        // guard via the with-init pattern — the values came from our
        // own append-only store and are trusted by construction. Same
        // pattern as the sentinel factories in
        // foundation/Assets/Common/{TenantId,ReplicaId}.cs.
        => new()
        {
            Position             = reader.GetInt64(12),
            EventId              = reader.GetString(0),
            EventType            = reader.GetString(1),
            SchemaVersion        = reader.GetInt32(2),
            OccurredAt           = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            RecordedAtUtc        = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            TenantId             = new TenantId { Value = reader.GetString(5) },
            OriginatingReplicaId = new ReplicaId { Value = reader.GetString(6) },
            IdempotencyKey       = reader.GetString(7),
            CausationId          = reader.IsDBNull(8) ? null : reader.GetString(8),
            CorrelationId        = reader.IsDBNull(9) ? null : reader.GetString(9),
            ProducerCluster      = reader.GetString(10),
            PayloadJson          = reader.GetString(11),
        };

    private static string LoadEmbeddedSql(string fileName)
    {
        var assembly = typeof(SqliteDomainEventStore).Assembly;
        var resourceName = $"Harborline.Api.Foundation.Events.Sql.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration '{fileName}' not found. "
                + "Verify <EmbeddedResource Include=\"Sql\\*.sql\" /> in csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
