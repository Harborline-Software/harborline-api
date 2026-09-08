using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Kernel.Runtime.Teams;

/// <summary>
/// Default <see cref="ITeamContextFactory"/>. Thread-safe dictionary keyed by
/// <see cref="TeamId"/>; each GetOrCreate builds a fresh <see cref="ServiceCollection"/>-backed
/// child provider populated by the configured <see cref="TeamServiceRegistrar"/>.
/// Per ADR 0032.
/// </summary>
/// <remarks>
/// Wave 6.1 ships the factory shape with a no-op default registrar. Wave 6.3
/// replaces the default with a real per-team service wiring via
/// <c>AddHarborlineMultiTeam(registrar)</c> in the composition root.
/// </remarks>
public sealed class TeamContextFactory : ITeamContextFactory, IAsyncDisposable
{
    /// <summary>No-op default registrar used when the composition root does not
    /// supply one (Wave 6.1 baseline). Produces an empty service provider.</summary>
    public static readonly TeamServiceRegistrar DefaultRegistrar = static (_, _, _) => { };

    private readonly TeamServiceRegistrar _registrar;
    private readonly IServiceProvider _outerProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<TeamId, TeamContext> _contexts = new();
    private readonly Dictionary<TeamId, Task<TeamContext>> _pending = new();
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>Create a factory with the process root clock and the default registrar.</summary>
    /// <param name="timeProvider">Process root clock.</param>
    public TeamContextFactory(TimeProvider timeProvider)
        : this(null, null, timeProvider)
    {
    }

    /// <summary>Create a factory with a registrar and the process root clock.</summary>
    /// <param name="registrar">Per-team service registrar. Pass <c>null</c> to use
    /// <see cref="DefaultRegistrar"/> (no services — the Wave 6.1 baseline).</param>
    /// <param name="timeProvider">Process root clock.</param>
    public TeamContextFactory(TeamServiceRegistrar registrar, TimeProvider timeProvider)
        : this(registrar, null, timeProvider)
    {
    }

    /// <summary>Create a factory with its complete production composition.</summary>
    /// <param name="registrar">Per-team service registrar.</param>
    /// <param name="outerProvider">The install-level (outer) <see cref="IServiceProvider"/>
    /// threaded into every per-team registrar invocation — the container-bridge seam
    /// (ONR survey 2026-06-19) that lets a per-team service resolve an install-level
    /// singleton (the synced-doctype delta router). Pass <c>null</c> for the
    /// Wave 6.1 baseline / tests that don't exercise the bridge; an empty provider
    /// is substituted so the registrar always receives a non-null provider.</param>
    /// <param name="timeProvider">Process root clock.</param>
    public TeamContextFactory(
        TeamServiceRegistrar? registrar,
        IServiceProvider? outerProvider,
        TimeProvider timeProvider)
    {
        _registrar = registrar ?? DefaultRegistrar;
        // An empty provider keeps the delegate contract non-null even when no outer
        // provider is supplied. Resolving the bridge against it throws (no router
        // registered) — correct: a factory built without an outer provider is not
        // expected to bridge to install-level doctypes.
        _outerProvider = outerProvider ?? new ServiceCollection().BuildServiceProvider();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<TeamContext> Active
    {
        get
        {
            lock (_gate)
            {
                return _contexts.Values.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task<TeamContext> GetOrCreateAsync(TeamId teamId, string displayName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        Task<TeamContext> creation;
        lock (_gate)
        {
            if (_contexts.TryGetValue(teamId, out var existing))
            {
                return Task.FromResult(existing);
            }

            if (_pending.TryGetValue(teamId, out var inFlight))
            {
                return inFlight;
            }

            // Initiate creation under the lock so concurrent callers coalesce on the same Task.
            creation = CreateAsync(teamId, displayName, ct);
            _pending[teamId] = creation;
        }

        return creation;
    }

    private async Task<TeamContext> CreateAsync(TeamId teamId, string displayName, CancellationToken ct)
    {
        // Yield so concurrent callers can observe _pending under the lock before
        // we complete synchronously. Cheap on a thread-pool scheduler.
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var services = new ServiceCollection();
        services.AddSingleton(_timeProvider);
        _registrar(services, teamId, _outerProvider);
        var provider = services.BuildServiceProvider();
        var context = new TeamContext(teamId, displayName, provider, _timeProvider);

        lock (_gate)
        {
            _contexts[teamId] = context;
            _pending.Remove(teamId);
        }
        return context;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(TeamId teamId, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        TeamContext? toDispose;
        lock (_gate)
        {
            if (!_contexts.TryGetValue(teamId, out toDispose))
            {
                return;
            }
            _contexts.Remove(teamId);
        }

        await toDispose.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Disposes all live team contexts. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TeamContext[] snapshot;
        lock (_gate)
        {
            snapshot = _contexts.Values.ToArray();
            _contexts.Clear();
            _pending.Clear();
        }

        foreach (var ctx in snapshot)
        {
            await ctx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TeamContextFactory));
        }
    }
}
