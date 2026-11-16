using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.Foundation.Events;

/// <summary>
/// Background service that drives <see cref="SqliteEventReader"/>
/// on a polling interval per
/// <c>cross-cluster-event-bus-design.md</c> §5 + §6. Each tick
/// invokes <see cref="SqliteEventReader.DrainOnceAsync"/> for the
/// each tenant with events. Handler-side failures are caught + recorded
/// inside the reader. A tenant whose drain itself throws stays at its
/// cursor while the tick moves on to the next tenant: the first failure
/// logs Error, repeats of the same exception type log Debug, and the
/// first success logs Information. Cancellation of the tick's token is
/// never swallowed.
/// </summary>
public sealed class EventDispatcherHost : BackgroundService
{
    private readonly SqliteEventReader _reader;
    private readonly ILogger<EventDispatcherHost> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;
    // Tenants whose drain is failing, keyed by tenant id; an entry leaves on
    // recovery. ponytail: not thread-safe; ExecuteAsync drains from one loop.
    private readonly Dictionary<string, (Type Type, int Polls)> _failing = new(StringComparer.Ordinal);

    public EventDispatcherHost(
        SqliteEventReader reader,
        ILogger<EventDispatcherHost> logger,
        TimeSpan? pollInterval = null,
        int batchSize = 100)
    {
        _reader       = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger       = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Must be > 0.");
        _batchSize = batchSize;
    }

    /// <summary>Drain one batch for every tenant with stored events.</summary>
    public async Task DrainOnceAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _reader.GetTenantsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tenant in tenants)
        {
            try
            {
                await _reader.DrainOnceAsync(tenant, _batchSize, cancellationToken).ConfigureAwait(false);
                if (_failing.Remove(tenant.Value, out var ended))
                    _logger.LogInformation("Domain event drain for tenant {TenantId} recovered after {FailedPolls} failed polls.", tenant.Value, ended.Polls);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // ADR-0172 decision 8: one tenant's failure blocks that tenant
                // visibly, never the tenants after it. One Error per failure
                // episode, not per poll (CWE-779).
                if (_failing.TryGetValue(tenant.Value, out var episode) && episode.Type == ex.GetType())
                {
                    _failing[tenant.Value] = (episode.Type, episode.Polls + 1);
                    _logger.LogDebug("Domain event drain for tenant {TenantId} still failing with {ExceptionType} ({FailedPolls} polls).", tenant.Value, episode.Type.Name, episode.Polls + 1);
                }
                else
                {
                    _failing[tenant.Value] = (ex.GetType(), 1);
                    _logger.LogError(ex, "Domain event drain failed for tenant {TenantId}; it stays at its cursor.", tenant.Value);
                }
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown — surface to the host so it exits cleanly.
                break;
            }
            catch (Exception ex)
            {
                // The drain itself failed (not a handler failure — those are
                // caught inside DrainOnceAsync + recorded to
                // event_handler_failures). Log + sleep to avoid hot-looping.
                _logger.LogError(ex, "Domain event drain failed; it retries next interval.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
