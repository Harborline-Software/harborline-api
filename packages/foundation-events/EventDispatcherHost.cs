using Microsoft.Extensions.Hosting;

namespace Harborline.Api.Foundation.Events;

/// <summary>
/// Background service that drives <see cref="SqliteEventReader"/>
/// on a polling interval per
/// <c>cross-cluster-event-bus-design.md</c> §5 + §6. Each tick
/// invokes <see cref="SqliteEventReader.DrainOnceAsync"/> for the
/// each tenant with events. Handler-side failures are caught + recorded
/// inside the reader; the host loop continues regardless.
/// </summary>
public sealed class EventDispatcherHost : BackgroundService
{
    private readonly SqliteEventReader _reader;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;

    public EventDispatcherHost(
        SqliteEventReader reader,
        TimeSpan? pollInterval = null,
        int batchSize = 100)
    {
        _reader       = reader ?? throw new ArgumentNullException(nameof(reader));
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
            await _reader.DrainOnceAsync(tenant, _batchSize, cancellationToken).ConfigureAwait(false);
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
                System.Diagnostics.Debug.WriteLine(
                    $"EventDispatcherHost drain failure: {ex}");
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
