using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebAntiforgeryStateStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "SES-02D")]
    public async Task Token_Is_DigestOnly_At_Rest_And_Consumes_Exactly_Once()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issue = await fixture.Store.RotateAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            "coordination-1",
            Now.AddMinutes(5));
        Assert.NotNull(issue);

        await using (var context = fixture.Factory.CreateDbContext())
        {
            var persisted = await context.AntiforgeryStates.AsNoTracking().SingleAsync();
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issue.Token))),
                persisted.TokenDigest);
            Assert.DoesNotContain(issue.Token, persisted.TokenDigest, StringComparison.Ordinal);
        }

        Assert.True(await fixture.Store.ConsumeAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            issue.Token));
        Assert.False(await fixture.Store.ConsumeAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            issue.Token));
    }

    [Fact]
    [Trait("PlanCard", "SES-02D")]
    public async Task CrossSubject_And_CrossAudience_Tokens_Refuse_Without_Consuming()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issue = await fixture.Store.RotateAsync(
            WebCookieAudience.SelectedSession,
            "account-1",
            "selected-1",
            "coordination-1",
            Now.AddMinutes(10));
        Assert.NotNull(issue);

        Assert.False(await fixture.Store.ConsumeAsync(
            WebCookieAudience.SelectedSession,
            "account-1",
            "selected-2",
            issue.Token));
        Assert.False(await fixture.Store.ConsumeAsync(
            WebCookieAudience.InstallationSession,
            "account-1",
            "selected-1",
            issue.Token));
        Assert.True(await fixture.Store.ConsumeAsync(
            WebCookieAudience.SelectedSession,
            "account-1",
            "selected-1",
            issue.Token));
    }

    [Fact]
    [Trait("PlanCard", "SES-02D")]
    public async Task Rotation_Revokes_Predecessor_And_Survives_Restart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Store.RotateAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            "coordination-1",
            Now.AddMinutes(5));
        var second = await fixture.Store.RotateAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            "coordination-2",
            Now.AddMinutes(5));
        Assert.NotNull(first);
        Assert.NotNull(second);
        var restarted = new WebAntiforgeryStateStore(fixture.Factory, fixture.Clock);

        Assert.False(await restarted.ConsumeAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            first.Token));
        Assert.True(await restarted.ConsumeAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            second.Token));
    }

    [Fact]
    [Trait("PlanCard", "SES-02D")]
    public async Task Expired_Token_Refuses_At_Exact_Boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issue = await fixture.Store.RotateAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            "coordination-1",
            Now.AddMinutes(5));
        Assert.NotNull(issue);
        fixture.Clock.UtcNow = Now.AddMinutes(5);

        Assert.False(await fixture.Store.ConsumeAsync(
            WebCookieAudience.AccountChallenge,
            "account-1",
            "challenge-1",
            issue.Token));
    }

    [Fact]
    [Trait("PlanCard", "SES-02D")]
    public async Task Concurrent_Rotation_Unique_Race_Refuses_Without_Throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"web-antiforgery-race-{Guid.NewGuid():N}.db");
        try
        {
            var interceptor = new ConcurrentRotationWinnerInterceptor();
            var factory = new InterceptingSessionContextFactory(path, interceptor);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }
            var store = new WebAntiforgeryStateStore(
                factory,
                new MutableTimeProvider { UtcNow = Now });

            var refused = await store.RotateAsync(
                WebCookieAudience.AccountChallenge,
                "account-1",
                "challenge-1",
                "coordination-loser",
                Now.AddMinutes(5));

            Assert.Null(refused);
            Assert.True(interceptor.Injected);
            await using var verification = factory.CreateDbContext();
            Assert.Empty(await verification.AntiforgeryStates.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path;

        private Fixture(
            string path,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory factory,
            MutableTimeProvider clock)
        {
            _path = path;
            Factory = factory;
            Clock = clock;
            Store = new WebAntiforgeryStateStore(factory, clock);
        }

        public WebAccountAccessChallengeIssuerTests.SessionContextFactory Factory { get; }

        public MutableTimeProvider Clock { get; }

        public WebAntiforgeryStateStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"web-antiforgery-{Guid.NewGuid():N}.db");
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(path);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }
            return new Fixture(path, factory, new MutableTimeProvider { UtcNow = Now });
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class InterceptingSessionContextFactory(
        string databasePath,
        SaveChangesInterceptor interceptor)
        : IDbContextFactory<NodeLocalWebSessionDbContext>
    {
        public NodeLocalWebSessionDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
                .AddInterceptors(interceptor)
                .Options;
            return new NodeLocalWebSessionDbContext(options);
        }
    }

    private sealed class ConcurrentRotationWinnerInterceptor : SaveChangesInterceptor
    {
        private int _injected;

        public bool Injected => Volatile.Read(ref _injected) != 0;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not NodeLocalWebSessionDbContext context ||
                !context.ChangeTracker.Entries<WebAntiforgeryStateRecord>()
                    .Any(entry => entry.State == EntityState.Added) ||
                Interlocked.Exchange(ref _injected, 1) != 0)
            {
                return result;
            }

            var winner = WebAntiforgeryStateStore.CreateState(
                WebCookieAudience.AccountChallenge,
                "account-1",
                "challenge-1",
                "coordination-winner",
                Now,
                Now.AddMinutes(5));
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO web_antiforgery_states (
                    antiforgery_state_id,
                    audience,
                    account_id,
                    subject_correlation_id,
                    token_digest,
                    coordination_correlation_id,
                    issued_at_utc,
                    absolute_expires_at_utc,
                    consumed_at_utc,
                    revoked_at_utc,
                    owner_version)
                VALUES (
                    {winner.State.AntiforgeryStateId},
                    {winner.State.Audience.ToString()},
                    {winner.State.AccountId},
                    {winner.State.SubjectCorrelationId},
                    {winner.State.TokenDigest},
                    {winner.State.CoordinationCorrelationId},
                    {winner.State.IssuedAtUtc.ToUnixTimeMilliseconds()},
                    {winner.State.AbsoluteExpiresAtUtc.ToUnixTimeMilliseconds()},
                    NULL,
                    NULL,
                    {winner.State.OwnerVersion})
                """, cancellationToken);
            return result;
        }
    }
}
