using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebAntiforgeryPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "SES-06A")]
    public async Task Anonymous_State_Is_BrowserBound_And_OneTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issueContext = new DefaultHttpContext();

        Assert.True(await fixture.Policy.IssueAnonymousAsync(issueContext));
        var token = issueContext.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString();
        var binding = ReadCookie(
            issueContext.Response.Headers.SetCookie.ToString(),
            WebSessionCookieNames.AnonymousAntiforgery);

        Assert.False(await fixture.Policy.ConsumeAnonymousAsync(Request(token, "other-browser")));
        Assert.True(await fixture.Policy.ConsumeAnonymousAsync(Request(token, binding)));
        Assert.False(await fixture.Policy.ConsumeAnonymousAsync(Request(token, binding)));
    }

    [Fact]
    [Trait("PlanCard", "SES-06A")]
    public async Task Prelogin_State_Refuses_When_Authenticated_Audience_Is_Present()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issueContext = new DefaultHttpContext();
        issueContext.Request.Headers.Cookie = $"{WebSessionCookieNames.Challenge}=challenge";

        Assert.False(await fixture.Policy.IssueAnonymousAsync(issueContext));
        Assert.Equal(0, issueContext.Response.Headers[WebAntiforgeryPolicy.HeaderName].Count);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.AntiforgeryStates.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    [Trait("PlanCard", "SES-06A")]
    public async Task Prelogin_Token_Refuses_After_Challenge_Cookie_Appears()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issueContext = new DefaultHttpContext();
        Assert.True(await fixture.Policy.IssueAnonymousAsync(issueContext));
        var token = issueContext.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString();
        var binding = ReadCookie(
            issueContext.Response.Headers.SetCookie.ToString(),
            WebSessionCookieNames.AnonymousAntiforgery);
        var transition = Request(token, binding);
        transition.Request.Headers.Cookie =
            $"{WebSessionCookieNames.AnonymousAntiforgery}={binding}; " +
            $"{WebSessionCookieNames.Challenge}=challenge";

        Assert.False(await fixture.Policy.ConsumeAnonymousAsync(transition));
    }

    private static DefaultHttpContext Request(string token, string binding)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[WebAntiforgeryPolicy.HeaderName] = token;
        context.Request.Headers.Cookie =
            $"{WebSessionCookieNames.AnonymousAntiforgery}={binding}";
        return context;
    }

    private static string ReadCookie(string setCookie, string name)
    {
        var pair = setCookie.Split(';', StringSplitOptions.TrimEntries)[0];
        Assert.StartsWith(name + "=", pair, StringComparison.Ordinal);
        return pair[(name.Length + 1)..];
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path;

        private Fixture(
            string path,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory factory,
            WebAntiforgeryPolicy policy)
        {
            _path = path;
            Factory = factory;
            Policy = policy;
        }

        public WebAccountAccessChallengeIssuerTests.SessionContextFactory Factory { get; }

        public WebAntiforgeryPolicy Policy { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"web-antiforgery-policy-{Guid.NewGuid():N}.db");
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(path);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }
            var clock = new FixedTimeProvider();
            var store = new WebAntiforgeryStateStore(factory, clock);
            return new Fixture(
                path,
                factory,
                new WebAntiforgeryPolicy(
                    factory,
                    new WebSelectedSessionStore(factory),
                    store,
                    clock));
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
