using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.PasswordHashing.DependencyInjection;
using Harborline.Api.Foundation.Session;
using Harborline.Api.Foundation.Session.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Acceptance proof of the S11 login lockout over the REAL web-session login route (real Kestrel
/// loopback, real <see cref="NodeWebSessionAuthority"/> with real Argon2id hasher, real
/// <see cref="WebLoginRateLimiter"/> on a settable clock): repeated failed logins engage a
/// temporary lockout keyed per (username, source); while locked even the CORRECT password gets the
/// SAME non-enumerating 401 body; after the lockout expires login succeeds again; the engage /
/// release transitions are audited as <c>Auth.LoginLockout</c> / <c>Auth.LoginLockoutReleased</c>.
/// </summary>
public sealed class WebLoginLockoutTests
{
    private const string FounderUser = "founder";
    private const string FounderPassword = "correct horse battery staple";

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SingleTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class AllowingV1AuthorityGate : IInstallationIdentityV1AuthorityGate
    {
        public Task<InstallationIdentityV1MutationAdmission> CheckV1MutationAdmissionAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallationIdentityV1MutationAdmission(true, null));

        public Task<InstallationIdentityV1MutationAdmission> CheckLegacyBearerAdmissionAsync(
            InstallationIdentityLegacyBearerAudience audience,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallationIdentityV1MutationAdmission(true, null));
    }

    /// <summary>Captures every structured <c>{AuditEventType}</c> value logged (#3362 pattern).</summary>
    private sealed class AuditCapture : ILoggerProvider
    {
        private readonly List<string> _eventTypes = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<string> EventTypes
        {
            get { lock (_gate) { return [.. _eventTypes]; } }
        }

        internal int CountOf(string eventType) =>
            EventTypes.Count(e => string.Equals(e, eventType, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose() { }

        private void Record(string eventType)
        {
            lock (_gate) { _eventTypes.Add(eventType); }
        }

        private sealed class Sink(AuditCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    return;
                }
                foreach (var pair in values)
                {
                    if (string.Equals(pair.Key, "AuditEventType", StringComparison.Ordinal)
                        && pair.Value is string eventType)
                    {
                        owner.Record(eventType);
                    }
                }
            }
        }
    }

    private sealed record Harness(
        SharedHostedWebApp App,
        HttpClient Client,
        AuditCapture Audit,
        MutableTimeProvider Clock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }

    private static string HashOf(string password) =>
        new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions()))
            .HashPassword(NodeWebUser.Instance, password);

    private static async Task<Harness> StartAsync(NodeWebLoginLockoutOptions lockout)
    {
        var outer = new ServiceCollection();
        var audit = new AuditCapture();
        outer.AddLogging(b => b.AddProvider(audit));

        var team = new TeamContext(
            new TeamId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            "Test Team",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        outer.AddSingleton<IActiveTeamAccessor>(new SingleTeamAccessor(team));
        outer.AddSingleton(new NodeCallerSessionToken("web-login-lockout-test-token"));

        outer.Configure<NodeWebClientOptions>(o =>
        {
            o.Enabled = true;
            o.FounderUsername = FounderUser;
            o.FounderPasswordHash = HashOf(FounderPassword);
            o.Lockout = lockout;
        });
        outer.AddHarborlinePasswordHashing<NodeWebUser>();
        outer.AddHarborlineSessionEstablishment();
        outer.AddSingleton<IInstallationIdentityV1AuthorityGate>(new AllowingV1AuthorityGate());
        outer.AddSingleton<INodeWebSessionAuthority, NodeWebSessionAuthority>();

        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-06T12:00:00Z"));
        outer.AddFrozenKernelClock(clock);
        // The limiter rides the settable clock so the test can cross the lockout expiry; wired
        // exactly like production (same options instance, same DI-shaped construction).
        outer.AddSingleton(sp => new WebLoginRateLimiter(
            sp.GetRequiredService<IOptions<NodeWebClientOptions>>(),
            clock,
            sp.GetRequiredService<ILogger<WebLoginRateLimiter>>()));

        var outerProvider = outer.BuildServiceProvider();
        var app = new SharedHostedWebApp(
            outerProvider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            outerProvider.GetRequiredService<TimeProvider>());
        app.MapHealthCheckIfAbsent();

        var authority = outerProvider.GetRequiredService<INodeWebSessionAuthority>();
        var limiter = outerProvider.GetRequiredService<WebLoginRateLimiter>();
        app.MapApiRoutes(a => WebSessionRoutes.Map(a, authority, limiter));

        await app.StartAsync(CancellationToken.None);
        var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(app.SelectedUrl!),
        };
        return new Harness(app, client, audit, clock);
    }

    private static HttpRequestMessage Login(string username, string password) =>
        new(HttpMethod.Post, WebSessionRoutes.LoginPath)
        {
            Content = JsonContent.Create(new { username, password }),
        };

    [Fact(DisplayName =
        "S11: repeated failed logins engage the lockout; the locked response is the SAME 401 body " +
        "even for the correct password; expiry releases it and login succeeds — all audited")]
    public async Task Lockout_Engages_IsUniform_AndReleases()
    {
        await using var h = await StartAsync(new NodeWebLoginLockoutOptions
        {
            MaxFailures = 3,
            Window = TimeSpan.FromMinutes(15),
            BaseLockoutDuration = TimeSpan.FromMinutes(1),
            MaxLockoutDuration = TimeSpan.FromHours(1),
        });

        // Three wrong passwords → lockout engages on the third.
        string? failedBody = null;
        for (var i = 0; i < 3; i++)
        {
            var resp = await h.Client.SendAsync(Login(FounderUser, "not the password"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            failedBody = await resp.Content.ReadAsStringAsync();
        }
        Assert.Equal(1, h.Audit.CountOf(AuditEventTypes.LoginLockout));

        // Locked out: even the CORRECT password gets the IDENTICAL non-enumerating 401 body — the
        // response neither confirms the account exists nor reveals the lockout state, and the error
        // code is the same stable localizable code ("login_failed"), never a new English string.
        var locked = await h.Client.SendAsync(Login(FounderUser, FounderPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(failedBody, await locked.Content.ReadAsStringAsync());

        // A DIFFERENT username from the same source is NOT locked (per-(username, source) key) —
        // and it still 401s on its own merits, indistinguishably.
        var other = await h.Client.SendAsync(Login("someone-else", "whatever"));
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Equal(failedBody, await other.Content.ReadAsStringAsync());

        // The lockout expires → the release is audited and the correct password logs in.
        h.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var success = await h.Client.SendAsync(Login(FounderUser, FounderPassword));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, h.Audit.CountOf(AuditEventTypes.LoginLockoutReleased));

        // Success RESET the pair: three fresh failures are needed to re-engage (two do not).
        await h.Client.SendAsync(Login(FounderUser, "wrong again"));
        await h.Client.SendAsync(Login(FounderUser, "wrong again"));
        Assert.Equal(1, h.Audit.CountOf(AuditEventTypes.LoginLockout));
        await h.Client.SendAsync(Login(FounderUser, "wrong again"));
        Assert.Equal(2, h.Audit.CountOf(AuditEventTypes.LoginLockout));
    }
}
