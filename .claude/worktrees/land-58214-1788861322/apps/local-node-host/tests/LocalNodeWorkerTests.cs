using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Gossip;

namespace Harborline.Api.LocalNodeHost.Tests;

/// <summary>
/// Integration-style tests for <see cref="LocalNodeWorker"/>. We exercise the
/// worker against a <see cref="HostApplicationBuilder"/>-composed service
/// provider using the real <c>AddHarborlineKernelRuntime</c> +
/// <c>AddHarborlineMultiTeam</c> extensions, then start the host and observe
/// lifecycle transitions + log capture.
/// </summary>
/// <remarks>
/// Wave 6.3.E.2: gossip is now team-scoped. The test host registers an empty
/// per-team service collection (no <see cref="Harborline.Api.Kernel.Sync.Gossip.IGossipDaemon"/>)
/// and pre-materializes a single test team ahead of <c>host.StartAsync</c>;
/// the worker's null-guard path logs a warning and proceeds with plugin
/// lifecycle only. Lifecycle log messages + node-state transitions remain
/// unchanged from the Wave 2.1 baseline so the original assertions still hold.
/// </remarks>
public sealed class LocalNodeWorkerTests
{
    private static readonly TeamId TestTeamId = new(new Guid("11111111-1111-1111-1111-111111111111"));

    /// <summary>
    /// Build a minimal test host that composes the real kernel runtime (plugin
    /// registry + NodeHost) plus the Wave 6.3.E.2 multi-team surface + the
    /// <see cref="LocalNodeWorker"/>. We swap <see cref="ILoggerFactory"/>
    /// with a capture factory so tests can assert on lifecycle log messages
    /// without pulling in a third-party logging framework.
    /// </summary>
    private static IHost BuildHost(
        IEnumerable<ILocalNodePlugin> plugins,
        CaptureLoggerProvider capture)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Services.AddTestKernelClock();

        // Wave 6.3.E.2: the real composition root calls
        // AddHarborlineDefaultTeamRegistrar + AddHarborlineTeamStoreActivator, which
        // reach into SQLCipher + the filesystem. For unit tests we stay on
        // AddHarborlineMultiTeam (no registrar = empty per-team provider), which
        // exercises the same ITeamContextFactory + IActiveTeamAccessor surface
        // the worker depends on without requiring a real keystore + DB path.
        builder.Services.AddHarborlineKernelRuntime();
        builder.Services.AddHarborlineMultiTeam();

        // Inject the test plugin set as a concrete IEnumerable<ILocalNodePlugin>.
        foreach (var plugin in plugins)
        {
            builder.Services.AddSingleton(plugin);
        }

        // Capture logger wiring. We deliberately do NOT call AddLogging() from
        // Microsoft.Extensions.Logging so the assembly dependency stays at
        // Logging.Abstractions only — the capture logger types below implement
        // the abstraction directly.
        builder.Services.AddSingleton(capture);
        builder.Services.AddSingleton<ILoggerFactory, CaptureLoggerFactory>();
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(CaptureLogger<>));

        builder.Services.AddHostedService<LocalNodeWorker>();

        return builder.Build();
    }

    /// <summary>
    /// Pre-materialize the test team and set it active. Mirrors what
    /// <c>MultiTeamBootstrapHostedService</c> does at startup in the real
    /// composition root; doing it explicitly from the test keeps the asserts
    /// on <c>NodeHost</c> state transitions free of race conditions against
    /// two competing hosted services.
    /// </summary>
    private static async Task MaterializeTestTeamAsync(IHost host)
    {
        var factory = host.Services.GetRequiredService<ITeamContextFactory>();
        var accessor = host.Services.GetRequiredService<IActiveTeamAccessor>();
        await factory.GetOrCreateAsync(TestTeamId, "Test Team", CancellationToken.None);
        await accessor.SetActiveAsync(TestTeamId, CancellationToken.None);
    }

    [Fact]
    public async Task Worker_starts_and_loads_plugins()
    {
        var capture = new CaptureLoggerProvider();
        var log = new List<string>();
        var plugins = new ILocalNodePlugin[]
        {
            new RecordingPlugin("plugin.a", Array.Empty<string>(), log),
            new RecordingPlugin("plugin.b", new[] { "plugin.a" }, log),
        };

        using var host = BuildHost(plugins, capture);
        await MaterializeTestTeamAsync(host);
        var registry = host.Services.GetRequiredService<IPluginRegistry>();
        var nodeHost = host.Services.GetRequiredService<INodeHost>();

        await host.StartAsync();

        // Give ExecuteAsync a moment to schedule past the await points.
        await WaitForStateAsync(nodeHost, NodeState.Running);

        Assert.Equal(2, registry.LoadedPlugins.Count);
        Assert.Contains("load:plugin.a", log);
        Assert.Contains("load:plugin.b", log);

        await host.StopAsync();
    }

    [Fact]
    public async Task Worker_starts_even_with_zero_plugins()
    {
        var capture = new CaptureLoggerProvider();
        using var host = BuildHost(Array.Empty<ILocalNodePlugin>(), capture);
        await MaterializeTestTeamAsync(host);
        var registry = host.Services.GetRequiredService<IPluginRegistry>();
        var nodeHost = host.Services.GetRequiredService<INodeHost>();

        await host.StartAsync();
        await WaitForStateAsync(nodeHost, NodeState.Running);

        Assert.Empty(registry.LoadedPlugins);
        Assert.Equal(NodeState.Running, nodeHost.State);

        await host.StopAsync();
    }

    [Fact]
    public async Task Worker_transitions_NodeHost_Stopped_Starting_Running_on_start()
    {
        var capture = new CaptureLoggerProvider();
        using var host = BuildHost(Array.Empty<ILocalNodePlugin>(), capture);
        await MaterializeTestTeamAsync(host);
        var nodeHost = host.Services.GetRequiredService<INodeHost>();

        // Before StartAsync is called, the host is freshly Stopped.
        Assert.Equal(NodeState.Stopped, nodeHost.State);

        await host.StartAsync();
        await WaitForStateAsync(nodeHost, NodeState.Running);

        Assert.Equal(NodeState.Running, nodeHost.State);

        await host.StopAsync();
    }

    [Fact]
    public async Task Worker_transitions_NodeHost_Running_Stopping_Stopped_on_cancellation()
    {
        var capture = new CaptureLoggerProvider();
        var log = new List<string>();
        var plugins = new ILocalNodePlugin[]
        {
            new RecordingPlugin("plugin.a", Array.Empty<string>(), log),
        };

        using var host = BuildHost(plugins, capture);
        await MaterializeTestTeamAsync(host);
        var registry = host.Services.GetRequiredService<IPluginRegistry>();
        var nodeHost = host.Services.GetRequiredService<INodeHost>();

        await host.StartAsync();
        await WaitForStateAsync(nodeHost, NodeState.Running);

        await host.StopAsync();

        Assert.Equal(NodeState.Stopped, nodeHost.State);
        Assert.Empty(registry.LoadedPlugins); // unloaded
        Assert.Contains("load:plugin.a", log);
        Assert.Contains("unload:plugin.a", log);
    }

    [Fact]
    public async Task Worker_logs_expected_lifecycle_events()
    {
        var capture = new CaptureLoggerProvider();
        using var host = BuildHost(Array.Empty<ILocalNodePlugin>(), capture);
        await MaterializeTestTeamAsync(host);
        var nodeHost = host.Services.GetRequiredService<INodeHost>();

        await host.StartAsync();
        await WaitForStateAsync(nodeHost, NodeState.Running);
        await host.StopAsync();

        var messages = capture.Messages.ToList();
        Assert.Contains(messages, m => m.Contains("Harborline local-node host starting", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Loaded 0 plugin(s)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Harborline local-node host stopping", StringComparison.Ordinal));
    }

    // ── MAJOR-1 (cerebrum [2026-06-21] verdict): a FAULTED daemon-rebind is DETECTABLE, not a silent success. ──
    //
    // The rebind runs OFF the join's SetActiveAsync call stack (OnActiveTeamChanged spins it onto a Task), and the
    // join route returns 200 {joined:true} BEFORE it settles. So if the rebind FAULTS (the production failure mode
    // is the BLOCKER-1 EADDRINUSE, but ANY StartGossipForTeamAsync throw is the same shape) the node is left
    // active-team=B-fault with NO running daemon — and the user was told the join succeeded. Before this fix the
    // fault was swallowed to a log line: NOTHING queryable reflected it. This test FORCES a rebind fault (a team
    // whose IGossipDaemon resolution throws) and proves the worker now records it as a LOUD, queryable signal
    // (LastRebind.IsHealthy=false, carrying the joined team id) that the sync-status surface projects.
    [Fact]
    public async Task Worker_records_a_FAULTED_rebind_as_detectable_not_silent()
    {
        var capture = new CaptureLoggerProvider();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddTestKernelClock();
        builder.Services.AddHarborlineKernelRuntime();
        // A registrar that registers a THROWING IGossipDaemon for the fault team only — so the boot team has no
        // daemon (clean boot, the empty-provider path) and the SWITCH to the fault team makes the rebind throw.
        builder.Services.AddHarborlineMultiTeam((services, teamId, _) =>
        {
            if (teamId.Equals(FaultTeamId))
            {
                services.AddSingleton<IGossipDaemon>(_ =>
                    throw new InvalidOperationException("forced rebind fault (modelling EADDRINUSE on the fixed port)"));
            }
            // The boot team registers nothing → no IGossipDaemon → clean boot without gossip.
        });
        builder.Services.AddSingleton(capture);
        builder.Services.AddSingleton<ILoggerFactory, CaptureLoggerFactory>();
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(CaptureLogger<>));
        builder.Services.AddHostedService<LocalNodeWorker>();
        builder.Services.AddSingleton(sp => (LocalNodeWorker)sp.GetServices<IHostedService>()
            .First(s => s is LocalNodeWorker));

        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ITeamContextFactory>();
        var accessor = host.Services.GetRequiredService<IActiveTeamAccessor>();

        // Boot on the (daemon-less) boot team — clean boot, no rebind yet → LastRebind healthy.
        await factory.GetOrCreateAsync(BootTeamId, "Boot Team", CancellationToken.None);
        await accessor.SetActiveAsync(BootTeamId, CancellationToken.None);
        await host.StartAsync();
        var worker = host.Services.GetRequiredService<LocalNodeWorker>();
        await WaitForStateAsync(host.Services.GetRequiredService<INodeHost>(), NodeState.Running);
        Assert.True(worker.LastRebind.IsHealthy, "no rebind has run yet — boot is healthy.");

        // Switch to the FAULT team → the rebind's StartGossipForTeamAsync throws on the daemon resolution.
        await factory.GetOrCreateAsync(FaultTeamId, "Fault Team", CancellationToken.None);
        await accessor.SetActiveAsync(FaultTeamId, CancellationToken.None);

        // THE PROOF: the off-stack rebind fault is RECORDED + queryable — not swallowed. The active-team switch
        // still held (the join's adoption is preserved), but the rebind is flagged INCOMPLETE.
        await WaitUntilRebindFaultedAsync(worker);
        Assert.False(worker.LastRebind.IsHealthy, "the faulted rebind must be detectable (LastRebind not healthy).");
        Assert.Equal(FaultTeamId, worker.LastRebind.FaultedTeamId);
        Assert.False(string.IsNullOrWhiteSpace(worker.LastRebind.FaultSummary));
        // The host did NOT crash on the rebind fault (a rebind fault must not brick the host loop).
        Assert.Equal(NodeState.Running, host.Services.GetRequiredService<INodeHost>().State);

        await host.StopAsync();
    }

    private static readonly TeamId BootTeamId = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly TeamId FaultTeamId = new(new Guid("33333333-3333-3333-3333-333333333333"));

    private static async Task WaitUntilRebindFaultedAsync(LocalNodeWorker worker, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (worker.LastRebind.IsHealthy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.False(worker.LastRebind.IsHealthy);
    }

    // ---- Helpers --------------------------------------------------------

    /// <summary>
    /// Spin (with a short delay) until the node host transitions to <paramref name="expected"/>
    /// or a 5s watchdog trips. The worker's ExecuteAsync is asynchronous relative to
    /// <c>IHost.StartAsync</c> — the latter returns once hosted services are scheduled,
    /// not once they finish their boot work — so we need a bounded poll.
    /// </summary>
    private static async Task WaitForStateAsync(INodeHost host, NodeState expected, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (host.State != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(expected, host.State);
    }

    /// <summary>Recording plugin — appends to a shared log so tests can assert ordering.</summary>
    private sealed class RecordingPlugin : ILocalNodePlugin
    {
        private readonly List<string> _log;

        public RecordingPlugin(string id, IReadOnlyCollection<string> deps, List<string> log)
        {
            Id = id;
            Dependencies = deps;
            _log = log;
        }

        public string Id { get; }
        public string Version => "1.0.0";
        public IReadOnlyCollection<string> Dependencies { get; }

        public Task OnLoadAsync(IPluginContext context, CancellationToken ct)
        {
            lock (_log) { _log.Add($"load:{Id}"); }
            return Task.CompletedTask;
        }

        public Task OnUnloadAsync(CancellationToken ct)
        {
            lock (_log) { _log.Add($"unload:{Id}"); }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal logger implementation that appends every formatted log message to
    /// a shared list. Keeps the test assembly dependency-free beyond
    /// Microsoft.Extensions.Logging.Abstractions.
    /// </summary>
    private sealed class CaptureLoggerProvider
    {
        private readonly List<string> _messages = new();
        private readonly object _gate = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) { return _messages.ToArray(); } }
        }

        public void Append(string category, LogLevel level, string message)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }
        }
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly CaptureLoggerProvider _provider;
        public CaptureLoggerFactory(CaptureLoggerProvider provider) => _provider = provider;
        public void AddProvider(ILoggerProvider provider) { /* not used by tests */ }
        public ILogger CreateLogger(string categoryName) => new CaptureLoggerImpl(categoryName, _provider);
        public void Dispose() { }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        private readonly CaptureLoggerImpl _inner;
        public CaptureLogger(CaptureLoggerProvider provider)
            => _inner = new CaptureLoggerImpl(typeof(T).FullName ?? typeof(T).Name, provider);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class CaptureLoggerImpl : ILogger
    {
        private readonly string _category;
        private readonly CaptureLoggerProvider _provider;
        public CaptureLoggerImpl(string category, CaptureLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null) return;
            _provider.Append(_category, logLevel, formatter(state, exception));
        }
    }
}
