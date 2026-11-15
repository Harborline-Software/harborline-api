using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Time;

namespace Harborline.Api.Foundation.Events;

/// <summary>
/// SQLite-backed <see cref="IEventReader"/>. Drains the
/// <c>domain_events</c> table on demand via
/// <see cref="DrainOnceAsync"/> (driven by the
/// <see cref="EventDispatcherHost"/> polling loop). Each registered
/// handler has its own cursor in <c>event_handler_cursors</c>;
/// failures are recorded in <c>event_handler_failures</c> with a
/// 7-step exponential-ish backoff per
/// <c>cross-cluster-event-bus-design.md</c> §6.
/// </summary>
/// <remarks>
/// On handler failure the cursor is NOT advanced — the handler revisits
/// the same event on the next drain. The reader stops processing
/// further events for that handler in the current cycle so the failure
/// doesn't cascade.
/// </remarks>
[ClockAuthority("Out-of-act dispatcher drain: dates its own cursor-advance and retry rows; no request act is in flight.")]
public sealed class SqliteEventReader : IEventReader
{
    private readonly SqliteConnection _connection;
    private readonly IDomainEventStore _store;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, HandlerRegistration> _handlers = new();

    public SqliteEventReader(
        SqliteConnection connection,
        IDomainEventStore store,
        TimeProvider clock)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _store      = store      ?? throw new ArgumentNullException(nameof(store));
        _clock      = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawDomainEvent>> ReadAsync(
        TenantId tenantId,
        string eventType,
        string? afterEventId,
        int maxBatchSize,
        CancellationToken cancellationToken = default)
    {
        var all = await _store.GetAfterCursorAsync(tenantId, afterEventId, maxBatchSize, cancellationToken)
            .ConfigureAwait(false);
        return all.Where(e => e.EventType == eventType).ToList();
    }

    /// <inheritdoc />
    public Task RegisterHandlerAsync(
        string handlerId,
        string? eventType,
        IEventHandler<RawDomainEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        ArgumentNullException.ThrowIfNull(handler);

        var added = _handlers.TryAdd(handlerId, new HandlerRegistration(handlerId, eventType, handler));
        if (!added)
            throw new InvalidOperationException(
                $"Handler '{handlerId}' is already registered. "
                + "Per cross-cluster-event-bus-design.md §5: handler ids are "
                + "stable + unique per process.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drain one batch of events to all registered handlers. The
    /// <see cref="EventDispatcherHost"/> calls this on its polling
    /// interval. Returns the count of successfully-handled
    /// invocations across all handlers.
    /// </summary>
    public async Task<int> DrainOnceAsync(
        TenantId tenantId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Must be > 0.");

        var totalProcessed = 0;
        foreach (var reg in _handlers.Values)
        {
            var cursor = await GetCursorAsync(reg.HandlerId, tenantId, cancellationToken).ConfigureAwait(false);
            var events = await _store.GetAfterPositionAsync(tenantId, cursor, batchSize, cancellationToken)
                .ConfigureAwait(false);
            foreach (var evt in events)
            {
                if (!await IsRetryDueAsync(reg.HandlerId, tenantId, evt.EventId, cancellationToken).ConfigureAwait(false))
                    break;

                if (reg.EventTypeFilter is not null && evt.EventType != reg.EventTypeFilter)
                {
                    // Filter mismatch — advance cursor past this event
                    // so the handler doesn't keep re-evaluating it.
                    await SetCursorAsync(
                            reg.HandlerId, tenantId, evt.EventId, evt.Position, _clock.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var envelope = ProjectToEnvelope(evt);
                    await reg.Handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                    await SetCursorAsync(
                            reg.HandlerId, tenantId, evt.EventId, evt.Position, _clock.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    totalProcessed++;
                }
                catch (Exception ex)
                {
                    await RecordFailureAsync(
                            reg.HandlerId, evt, tenantId, ex, _clock.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    // Cursor NOT advanced. Stop this handler's cycle —
                    // it revisits the same event on the next drain.
                    break;
                }
            }
        }
        return totalProcessed;
    }

    internal async Task<IReadOnlyList<TenantId>> GetTenantsAsync(CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tenant_id FROM domain_events ORDER BY tenant_id";
        var tenants = new List<TenantId>();
        await using var rows = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rows.ReadAsync(ct).ConfigureAwait(false))
            tenants.Add(new TenantId { Value = rows.GetString(0) });
        return tenants;
    }

    /// <summary>Read exhausted handler failures requiring intervention.</summary>
    public async Task<IReadOnlyList<HeldEventHandler>> ReadHeldHandlersAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT handler_id, tenant_id, event_id, attempt_number FROM event_handler_failures
            WHERE resolved_at IS NULL AND next_retry_at IS NULL;
            """;
        var held = new List<HeldEventHandler>();
        await using var rows = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            held.Add(new HeldEventHandler(rows.GetString(0), new TenantId { Value = rows.GetString(1) }, rows.GetString(2), rows.GetInt32(3)));
        return held;
    }

    private async Task<bool> IsRetryDueAsync(string handlerId, TenantId tenantId, string eventId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT next_retry_at FROM event_handler_failures
            WHERE handler_id = $handler_id AND tenant_id = $tenant_id AND event_id = $event_id
              AND resolved_at IS NULL
            ORDER BY attempt_number DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$handler_id", handlerId);
        cmd.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        cmd.Parameters.AddWithValue("$event_id", eventId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return true;
        return !await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) && DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture) <= _clock.GetUtcNow();
    }

    private async Task<long> GetCursorAsync(
        string handlerId, TenantId tenantId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT position FROM event_handler_positions
            WHERE handler_id = $handler_id AND tenant_id = $tenant_id;
            """;
        cmd.Parameters.AddWithValue("$handler_id", handlerId);
        cmd.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task SetCursorAsync(
        string handlerId,
        TenantId tenantId,
        string eventId,
        long position,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_handler_cursors
                (handler_id, tenant_id, last_handled_event_id, last_handled_at)
            VALUES ($handler_id, $tenant_id, $event_id, $now)
            ON CONFLICT(handler_id, tenant_id) DO UPDATE
                SET last_handled_event_id = $event_id,
                    last_handled_at = $now;
            INSERT INTO event_handler_positions(handler_id, tenant_id, position)
            VALUES ($handler_id, $tenant_id, $position)
            ON CONFLICT(handler_id, tenant_id) DO UPDATE SET position = $position;
            """;
        cmd.Parameters.AddWithValue("$handler_id", handlerId);
        cmd.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        cmd.Parameters.AddWithValue("$event_id", eventId);
        cmd.Parameters.AddWithValue("$position", position);
        cmd.Parameters.AddWithValue("$now", at.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(
        string handlerId, RawDomainEvent evt, TenantId tenantId,
        Exception ex, DateTimeOffset at, CancellationToken ct)
    {
        var attempt = await GetAttemptCountAsync(handlerId, tenantId, evt.EventId, ct).ConfigureAwait(false) + 1;
        var nextRetry = ComputeNextRetry(attempt, at);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_handler_failures
                (id, handler_id, event_id, tenant_id, attempt_number, failed_at, error_message, next_retry_at)
            VALUES
                ($id, $handler_id, $event_id, $tenant_id, $attempt, $failed_at, $error_message, $next_retry_at);
            """;
        cmd.Parameters.AddWithValue("$id", EventId.New(at));
        cmd.Parameters.AddWithValue("$handler_id", handlerId);
        cmd.Parameters.AddWithValue("$event_id", evt.EventId);
        cmd.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        cmd.Parameters.AddWithValue("$attempt", attempt);
        cmd.Parameters.AddWithValue("$failed_at", at.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$error_message", ex.Message);
        cmd.Parameters.AddWithValue("$next_retry_at",
            (object?)nextRetry?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> GetAttemptCountAsync(string handlerId, TenantId tenantId, string eventId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM event_handler_failures
            WHERE handler_id = $handler_id AND tenant_id = $tenant_id AND event_id = $event_id;
            """;
        cmd.Parameters.AddWithValue("$handler_id", handlerId);
        cmd.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        cmd.Parameters.AddWithValue("$event_id", eventId);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Backoff schedule per <c>cross-cluster-event-bus-design.md</c>
    /// §6: 30s / 2m / 10m / 1h / 6h / 24h. Returns
    /// <c>null</c> after 7 attempts (retry exhausted; manual
    /// intervention).
    /// </summary>
    internal static DateTimeOffset? ComputeNextRetry(int attempt, DateTimeOffset now)
        => attempt switch
        {
            1 => now.AddSeconds(30),
            2 => now.AddMinutes(2),
            3 => now.AddMinutes(10),
            4 => now.AddHours(1),
            5 => now.AddHours(6),
            6 => now.AddHours(24),
            _ => null,
        };

    /// <summary>
    /// Project the untyped <see cref="RawDomainEvent"/> back to a
    /// strongly-typed <see cref="DomainEventEnvelope{TPayload}"/>
    /// where <c>TPayload</c> is <see cref="RawDomainEvent"/> itself.
    /// Handlers that need the typed payload deserialize
    /// <see cref="RawDomainEvent.PayloadJson"/> themselves.
    /// </summary>
    private static DomainEventEnvelope<RawDomainEvent> ProjectToEnvelope(RawDomainEvent evt)
        => new()
        {
            EventId              = evt.EventId,
            EventType            = evt.EventType,
            SchemaVersion        = evt.SchemaVersion,
            OccurredAt           = evt.OccurredAt,
            TenantId             = evt.TenantId,
            OriginatingReplicaId = evt.OriginatingReplicaId,
            IdempotencyKey       = evt.IdempotencyKey,
            CausationId          = evt.CausationId,
            CorrelationId        = evt.CorrelationId,
            Payload              = evt,
        };

    private sealed record HandlerRegistration(
        string HandlerId,
        string? EventTypeFilter,
        IEventHandler<RawDomainEvent> Handler);
}

/// <summary>An exhausted event handler awaiting intervention.</summary>
public sealed record HeldEventHandler(string HandlerId, TenantId TenantId, string EventId, int AttemptCount);
