using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace Harborline.Api.LocalNodeHost.Health;

internal interface IWindowsEventLogSink : ILoggerProvider, ISupportExternalScope
{
    bool IsAvailable { get; }
    void Open();
}

[ProviderAlias("EventLog")]
internal sealed class FrameworkWindowsEventLogSink : IWindowsEventLogSink
{
    private readonly Func<ILoggerProvider> _providerFactory;
    private ILoggerProvider? _provider;
    private IExternalScopeProvider? _scopeProvider;
    private ILogger? _failureLogger;
    private int _available;
    private int _failureReported;

    public bool IsAvailable => Volatile.Read(ref _available) == 1;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public FrameworkWindowsEventLogSink(IOptions<EventLogSettings> settings)
        : this(() => new EventLogLoggerProvider(settings))
    {
    }

    internal FrameworkWindowsEventLogSink(Func<ILoggerProvider> providerFactory)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    public void Open()
    {
        // CA1416: the framework provider is Windows-only. Elsewhere the sink is simply unavailable, which
        // the health detail reports; the host still starts (ticket 240).
        if (!OperatingSystem.IsWindows())
        {
            Volatile.Write(ref _available, -1);
            return;
        }
        try
        {
            var provider = _providerFactory();
            _provider = provider;
            if (_scopeProvider is not null && provider is ISupportExternalScope scopedProvider)
                scopedProvider.SetScopeProvider(_scopeProvider);
            provider.CreateLogger("Harborline.LocalNodeHost.EventLog");
            Volatile.Write(ref _available, 1);
        }
        catch (Exception exception)
        {
            MarkUnavailable(exception);
        }
    }

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
        if (OperatingSystem.IsWindows() && Volatile.Read(ref _provider) is ISupportExternalScope provider)
            provider.SetScopeProvider(scopeProvider);
    }

    internal void SetFailureLogger(ILogger failureLogger) =>
        _failureLogger = failureLogger ?? throw new ArgumentNullException(nameof(failureLogger));

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            Interlocked.Exchange(ref _provider, null)?.Dispose();
    }

    private sealed class SinkLogger(FrameworkWindowsEventLogSink owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.IsAvailable;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!owner.IsAvailable || !OperatingSystem.IsWindows())
                return;

            try
            {
                Volatile.Read(ref owner._provider)!
                    .CreateLogger(categoryName).Log(logLevel, eventId, state, exception, formatter);
            }
            catch (Exception writeFailure)
            {
                owner.MarkUnavailable(writeFailure);
            }
        }
    }

    private void MarkUnavailable(Exception exception)
    {
        Interlocked.Exchange(ref _available, -1);
        if (Interlocked.Exchange(ref _failureReported, 1) == 0)
        {
            _failureLogger?.LogWarning(
                exception, "Windows Event Log unavailable; logging will continue without that sink.");
        }
    }
}

/// <remarks>
/// The sink is OPTIONAL because <see cref="ResilientWindowsEventLogRegistration.Add"/> registers it on
/// Windows only, while the health chain in Program.cs is composed once for every platform. A required
/// dependency made every /health request on macOS and Linux throw
/// "Unable to resolve service for type 'IWindowsEventLogSink'" inside the health middleware — a 500 on a
/// perfectly healthy host (ticket 302). No sink registered means the platform has no event log to be
/// unavailable, which is Healthy; a registered sink still decides.
/// </remarks>
internal sealed class WindowsEventLogAvailabilityHealthCheck(IWindowsEventLogSink? sink = null) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(sink is null || sink.IsAvailable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("event log unavailable"));
}

internal static class ResilientWindowsEventLogRegistration
{
    internal static void Add(IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
            return;

        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(ILoggerProvider)
                && services[index].ImplementationType == typeof(EventLogLoggerProvider))
                services.RemoveAt(index);
        }

        services.AddSingleton<IWindowsEventLogSink, FrameworkWindowsEventLogSink>();
        services.AddSingleton<ILoggerProvider>(provider =>
            provider.GetRequiredService<IWindowsEventLogSink>());
        // The health check is added onto the host's ONE AddHealthChecks() chain in Program.cs, not here:
        // calling AddHealthChecks() this early moves HealthCheckPublisherHostedService to the front of the
        // hosted-component inventory, which is pinned in order (LocalNodeHostedComponentCatalog).
    }

    internal static IHealthChecksBuilder AddAvailabilityCheck(IHealthChecksBuilder health) =>
        health.AddCheck<WindowsEventLogAvailabilityHealthCheck>("windows-event-log");

    internal static void Open(IServiceProvider services)
    {
        var sink = services.GetService<IWindowsEventLogSink>();
        if (sink is null)
            return;

        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Harborline.LocalNodeHost.EventLog");
        if (sink is FrameworkWindowsEventLogSink frameworkSink)
            frameworkSink.SetFailureLogger(logger);

        try
        {
            sink.Open();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Windows Event Log unavailable; host startup will continue.");
        }
    }
}
