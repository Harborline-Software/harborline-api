using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialTestKit;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-731, owner ruling of 2026-09-25 on a failed outbox write — DES-0052 layout-eng-31 (host half). By
/// default nothing the caller sees changes and the loss is alerted. With audit-degraded mode on, node audit
/// health alone decides: while it is bad, every related-binding resolution is refused identically, and the
/// refusal clears by itself once health returns. Before, during and after degradation, a missing and a
/// denied target give the same response in shape, status and timing.
/// </summary>
[Collection(LayoutTimingParityCollection.Name)]
public sealed class LayoutAuditDegradedModeTests(ITestOutputHelper output)
{
    private static readonly ActorId Stranger = new("stranger-731");

    public enum Trigger { GateLogDown, OutboxDown }

    [Fact(DisplayName = "layout-eng-31, layout-run-5: by default a failed outbox write keeps the response unchanged and identical for missing and denied, and raises a critical alert outside the gate log")]
    public async Task ByDefaultAFailedOutboxWriteChangesNothingTheCallerSeesAndAlerts()
    {
        var log = new CapturingLogger();
        await using var h = await Harness.CreateAsync(log);
        var host = h.Host(TimeSpan.Zero, degradedMode: false);
        h.Pipeline.Db.Fault = true;

        var missing = await h.ObserveAsync(host, ownerExists: false);
        var denied = await h.ObserveAsync(host, ownerExists: true);

        Assert.Equal(missing, denied);
        Assert.DoesNotContain(LayoutAuditDegradedException.Code, denied);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Critical
            && entry.Event == LayoutDenialGateLog.OutboxWriteFailedEvent);
        h.Pipeline.Db.Fault = false;
        Assert.Equal(HealthStatus.Degraded, (await h.Pipeline.HealthAsync()).Status);
    }

    [Fact(DisplayName = "layout-run-5: an audit-health probe that cannot read the outbox logs the exception once when health turns bad, not on every probe, and one line on recovery")]
    public async Task AFailingAuditHealthProbeLogsOnceWithTheCause()
    {
        var log = new CapturingLogger();
        using var db = new LocalNodeDb();
        var outbox = new NodeEfLayoutDenialOutbox(db, new TypedLogger<NodeEfLayoutDenialOutbox>(log));
        db.Fault = true;

        for (var i = 0; i < 3; i++) Assert.False(await outbox.IsAuditHealthyAsync());

        var failure = Assert.Single(log.Entries, entry => entry.Event == NodeEfLayoutDenialOutbox.AuditHealthProbeFailedEvent);
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.Equal("the local store is down", failure.Exception?.Message);

        db.Fault = false;
        Assert.True(await outbox.IsAuditHealthyAsync());
        Assert.True(await outbox.IsAuditHealthyAsync());
        Assert.Single(log.Entries, entry => entry.Event == NodeEfLayoutDenialOutbox.AuditHealthProbeRecoveredEvent);
    }

    [Trait(LayoutTimingParityCollection.LaneTrait, LayoutTimingParityCollection.PerfLane)]
    [Theory(DisplayName = "layout-eng-31: in audit-degraded mode, missing and denied give the same response in shape and status, and meet the timing gate's bounds under the test conditions, before, during and after degradation, and the refusal clears on recovery")]
    [InlineData(Trigger.GateLogDown)]
    [InlineData(Trigger.OutboxDown)]
    public async Task MissingAndDeniedMatchBeforeDuringAndAfterDegradation(Trigger trigger)
    {
        await using var h = await Harness.CreateAsync(new CapturingLogger());
        h.Pipeline.Db.Delay = TimeSpan.FromMilliseconds(5);

        // Before: a healthy node, the mode on, nothing refused.
        await AssertPhaseAsync(h, expectRefused: false, $"{trigger} before");

        // Degrade the node's audit health, never through a lookup's own outcome.
        if (trigger == Trigger.GateLogDown)
        {
            ((SwitchableTrail)h.Pipeline.Trail).Down = true;
            await h.ObserveAsync(h.Host(TimeSpan.Zero, degradedMode: false), ownerExists: true);
            await h.Pipeline.Appender.IdleAsync(); // the append failed; the denial waits in the outbox
        }
        else
        {
            h.Pipeline.Db.Fault = true;
        }
        Assert.False(await h.Pipeline.Outbox.IsAuditHealthyAsync());

        // During: every related-binding resolution refused, identically.
        await AssertPhaseAsync(h, expectRefused: true, $"{trigger} during");

        // After: health restored, the refusal clears by itself.
        ((SwitchableTrail)h.Pipeline.Trail).Down = false;
        h.Pipeline.Db.Fault = false;
        await h.Pipeline.Appender.DrainAsync();
        Assert.True(await h.Pipeline.Outbox.IsAuditHealthyAsync());
        await AssertPhaseAsync(h, expectRefused: false, $"{trigger} after");
    }

    private async Task AssertPhaseAsync(Harness h, bool expectRefused, string phase)
    {
        var host = h.Host(TimeSpan.Zero, degradedMode: true);
        var missing = await h.ObserveAsync(host, ownerExists: false);
        var denied = await h.ObserveAsync(host, ownerExists: true);
        Assert.Equal(missing, denied);
        Assert.Equal(expectRefused, denied.Contains(LayoutAuditDegradedException.Code, StringComparison.Ordinal));
        await h.Pipeline.Appender.IdleAsync();
        await TimingParity.AssertSameAsync(
            floor => ownerExists => h.ObserveAsync(h.Host(floor, degradedMode: true), ownerExists),
            h.Pipeline.Appender.IdleAsync, phase, output);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AuthorizationGate _gate;
        private readonly ILogger _logger;
        private int _request;

        private Harness(ServiceProvider provider, ILogger logger)
        {
            _provider = provider;
            _logger = logger;
            _gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>());
        }

        public Pipeline Pipeline { get; } = new(new SwitchableTrail());

        public static async Task<Harness> CreateAsync(ILogger logger)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(Tenant, At, AuthorizationSeedProfile.Production);
            return new Harness(provider, logger);
        }

        public LayoutSurfaceHost Host(TimeSpan floor, bool degradedMode) => new(_gate, Pipeline.Outbox, Pipeline.Appender,
            Pipeline.Alarms, new SystemTimeAt(), _logger,
            new LayoutSurfaceOptions { ResponseFloor = floor, AuditDegradedMode = degradedMode });

        /// <summary>What the caller observes: the resolution, or the refusal's type, code and message.</summary>
        public async Task<string> ObserveAsync(LayoutSurfaceHost host, bool ownerExists)
        {
            var owner = new LayoutRelatedRecord(Owner, new LayoutBindingScope(null, null,
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["name"] = JsonValue.Create("Contoso") }));
            try
            {
                var resolution = await host.ResolveAsync(Surface(), new OutcomeSources(LayoutRelatedResult.Absent),
                    new Dictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>>(StringComparer.Ordinal)
                    {
                        ["invoice.owner"] = _ => ownerExists ? owner : null,
                    },
                    LayoutBindingScope.Root(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)),
                    Tenant, Stranger, $"request-{Interlocked.Increment(ref _request)}");
                return JsonSerializer.Serialize(new
                {
                    blocks = resolution.Blocks.Select(block => new { block.BlockId, block.Kind, block.BindingKind, block.Name, Value = block.Value?.ToJsonString(), block.RowId }),
                    resolution.Refusals,
                    resolution.Hidden,
                });
            }
            catch (LayoutAuditDegradedException refused)
            {
                return $"{refused.GetType().Name}|{LayoutAuditDegradedException.Code}|{refused.Message}";
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Pipeline.Appender.IdleAsync();
            await _provider.DisposeAsync();
            Pipeline.Dispose();
        }
    }

    private sealed class SystemTimeAt : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, EventId Event, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, eventId, exception));
        }
    }
}
