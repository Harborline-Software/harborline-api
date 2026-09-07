using System.Net;

using Harborline.Api.LocalNodeHost.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

[Collection("Harborline process environment")]
public sealed class ComposedHostBootEventLogTests
{
    [Theory]
    [InlineData(EventLogFailurePhase.ProviderConstruction)]
    [InlineData(EventLogFailurePhase.LoggerConstruction)]
    [InlineData(EventLogFailurePhase.FirstWrite)]
    [InlineData(EventLogFailurePhase.LaterWrite)]
    public async Task Every_event_log_failure_phase_is_isolated_and_degrades_health(
        EventLogFailurePhase phase)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var provider = new PhaseFailingLoggerProvider(phase);
        using var sink = new FrameworkWindowsEventLogSink(() =>
            phase == EventLogFailurePhase.ProviderConstruction
                ? throw new InvalidOperationException("provider construction failed")
                : provider);
        var failureEvents = new List<LogLevel>();
        sink.SetFailureLogger(new RecordingLogger(failureEvents));
        var logger = sink.CreateLogger("test.category");

        var escaped = Record.Exception(() =>
        {
            sink.Open();
            logger.LogWarning("first write");
            logger.LogWarning("later write");
        });
        var health = await new WindowsEventLogAvailabilityHealthCheck(sink)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Null(escaped);
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Equal([LogLevel.Warning], failureEvents);
    }

    [Fact]
    public async Task ThrowingEventLogSink_DoesNotStopTheComposedHostAndIsReportedByHealth()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(), $"s240-event-log-composed-host-{Guid.NewGuid():N}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            var baseAddress = await LocalNodeHostRuntime.StartAsync(
                "s240-event-log-sink-token",
                dataDirectory,
                deadline.Token,
                finalServiceRegistration: services => services.Replace(
                    ServiceDescriptor.Singleton<IWindowsEventLogSink>(new ThrowingEventLogSink())));
            using var client = new HttpClient { BaseAddress = baseAddress };

            using var response = await client.GetAsync("/health", deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("event log unavailable", body, StringComparison.Ordinal);
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(dataDirectory, "install-identities"), "*.identity"));
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(dataDirectory, "keys"), "*.dpapi"));
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(deadline.Token);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void HealthyOpen_ConstructsTheEventLogLoggerWithoutWritingASyntheticCriticalEvent()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var provider = new RecordingLoggerProvider();
        using var sink = new FrameworkWindowsEventLogSink(() => provider);

        sink.Open();

        Assert.True(sink.IsAvailable);
        Assert.Equal(["Harborline.LocalNodeHost.EventLog"], provider.Categories);
        Assert.Empty(provider.Events);
    }

    private sealed class ThrowingEventLogSink : IWindowsEventLogSink
    {
        public bool IsAvailable => false;
        public void Open() => throw new InvalidOperationException("test sink cannot open");
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) { }
        public void Dispose() { }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Categories { get; } = [];
        public List<LogLevel> Events { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new RecordingLogger(Events);
        }

        public void Dispose() { }
    }

    private sealed class RecordingLogger(List<LogLevel> events) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => events.Add(logLevel);
    }

    public enum EventLogFailurePhase
    {
        ProviderConstruction,
        LoggerConstruction,
        FirstWrite,
        LaterWrite,
    }

    private sealed class PhaseFailingLoggerProvider(EventLogFailurePhase phase) : ILoggerProvider
    {
        private readonly PhaseFailingLogger _logger = new(phase);
        private int _loggerConstructions;

        public ILogger CreateLogger(string categoryName)
        {
            if (phase == EventLogFailurePhase.LoggerConstruction
                && Interlocked.Increment(ref _loggerConstructions) == 1)
                throw new InvalidOperationException("logger construction failed");

            return _logger;
        }

        public void Dispose() { }
    }

    private sealed class PhaseFailingLogger(EventLogFailurePhase phase) : ILogger
    {
        private int _writes;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var write = Interlocked.Increment(ref _writes);
            if ((phase == EventLogFailurePhase.FirstWrite && write == 1)
                || (phase == EventLogFailurePhase.LaterWrite && write == 2))
                throw new InvalidOperationException($"write {write} failed");
        }
    }
}
