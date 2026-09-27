using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>Periodically retries related-binding denial audit records whose first append did not complete.</summary>
public sealed class LayoutDenialDrainDaemon : BackgroundService
{
    /// <summary>The default interval between durable outbox recovery sweeps.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly NodeEfLayoutDenialOutbox _outbox;
    private readonly LayoutDenialAppender _appender;
    private readonly TimeProvider _time;
    private readonly ILogger<LayoutDenialDrainDaemon> _logger;
    private readonly TimeSpan _interval;

    /// <summary>Constructs the recovery loop over the denial outbox and signed appender.</summary>
    public LayoutDenialDrainDaemon(
        NodeEfLayoutDenialOutbox outbox,
        LayoutDenialAppender appender,
        TimeProvider time,
        ILogger<LayoutDenialDrainDaemon> logger,
        TimeSpan? interval = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _appender = appender ?? throw new ArgumentNullException(nameof(appender));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval is { } value && value > TimeSpan.Zero ? value : DefaultInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        do
        {
            try
            {
                await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Layout denial outbox drain failed; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Appends every unresolved denial, oldest first, and returns after every attempt finishes.</summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        foreach (var (entry, _) in await _outbox.ListUnresolvedAsync(ct).ConfigureAwait(false))
            await _appender.AppendAsync(entry).ConfigureAwait(false);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
