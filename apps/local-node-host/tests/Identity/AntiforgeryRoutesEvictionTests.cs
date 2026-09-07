using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// A refused antiforgery issuance must EXPIRE the audience cookie it refused on (earlier repository ticket #3329 deep
/// review).
/// </summary>
/// <remarks>
/// <para>
/// Issuance fails whenever the browser presents an audience cookie that no longer resolves — a selected
/// session past its 30-minute idle timeout, or a challenge cookie stranded by a failed select. Refusing
/// alone left the browser WEDGED: the cookies are <c>HttpOnly</c> so the client cannot clear them,
/// <c>HasAnyAuthenticatedAudience</c> keeps refusing the anonymous path while one is presented, and the
/// only routes that delete them (logout, select-on-success) each need a token this route would not issue.
/// </para>
/// <para>
/// The selected cookie's <c>Expires</c> is the ABSOLUTE 8-hour lifetime rather than the 30-minute idle
/// one, so the everyday case — close a laptop, come back after 45 minutes — locked a human out of their
/// own installation for most of a working day, behind a deliberately non-enumerating "check your
/// credentials". These pin the eviction that makes the client's one retry a recovery.
/// </para>
/// </remarks>
public sealed class AntiforgeryRoutesEvictionTests
{
    /// <summary>Reports a fixed issuance outcome and records which audience the route asked about.</summary>
    private sealed class StubPolicy : IWebAntiforgeryPolicy
    {
        private readonly bool _issues;
        internal StubPolicy(bool issues) => _issues = issues;

        internal bool RotateSelectedCalled { get; private set; }
        internal bool IssueAnonymousCalled { get; private set; }

        public Task<bool> IssueAnonymousAsync(HttpContext context)
        {
            IssueAnonymousCalled = true;
            return Task.FromResult(_issues);
        }

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle)
        {
            RotateSelectedCalled = true;
            return Task.FromResult(_issues);
        }

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public void EmitToken(HttpResponse response, string token) { }

        public void ExpireAnonymousBinding(HttpResponse response) { }
    }

    /// <summary>
    /// The minimum DI a minimal-API <c>IResult</c> needs to execute: <c>Results.Json</c> resolves
    /// <c>IOptions&lt;JsonOptions&gt;</c> and a logger factory off <c>RequestServices</c>.
    /// </summary>
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();

    private static DefaultHttpContext ContextWith(params (string Name, string Value)[] cookies)
    {
        var context = new DefaultHttpContext { RequestServices = Services };
        if (cookies.Length > 0)
        {
            context.Request.Headers.Cookie =
                string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>Every Set-Cookie the response carries that expires <paramref name="name"/>.</summary>
    private static bool ExpiresCookie(HttpContext context, string name) =>
        context.Response.Headers.SetCookie.Any(v =>
            v is not null
            && v.StartsWith($"{name}=", StringComparison.Ordinal)
            && (v.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
                || v.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public async Task Refused_selected_rotation_expires_the_dead_selected_and_challenge_cookies()
    {
        var policy = new StubPolicy(issues: false);
        var context = ContextWith(
            (WebSessionCookieNames.Selected, "dead-handle"),
            (WebSessionCookieNames.Challenge, "stranded-handle"));

        var result = await AntiforgeryRoutes.IssueAsync(policy, context);
        await result.ExecuteAsync(context);

        Assert.True(policy.RotateSelectedCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);

        // The whole point: the caller still gets 401, but the browser drops what blocked it, so the
        // client's retry issues anonymously instead of being refused forever.
        Assert.True(
            ExpiresCookie(context, WebSessionCookieNames.Selected),
            "a refused selected rotation must expire the selected cookie");
        Assert.True(
            ExpiresCookie(context, WebSessionCookieNames.Challenge),
            "a stranded challenge also refuses the anonymous path, so it goes too");
    }

    [Fact]
    public async Task Refused_anonymous_issue_expires_a_stranded_challenge_cookie()
    {
        // No selected cookie, so the route takes the anonymous path; a stranded challenge is what makes
        // HasAnyAuthenticatedAudience refuse it.
        var policy = new StubPolicy(issues: false);
        var context = ContextWith((WebSessionCookieNames.Challenge, "stranded-handle"));

        var result = await AntiforgeryRoutes.IssueAsync(policy, context);
        await result.ExecuteAsync(context);

        Assert.True(policy.IssueAnonymousCalled);
        Assert.False(policy.RotateSelectedCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(ExpiresCookie(context, WebSessionCookieNames.Challenge));
    }

    [Fact]
    public async Task A_successful_issue_evicts_nothing()
    {
        // THE fail-closed guard on this change. Eviction must happen only AFTER the authority has
        // refused — never speculatively — or a request that merely raced a live session would clear it.
        var policy = new StubPolicy(issues: true);
        var context = ContextWith((WebSessionCookieNames.Selected, "live-handle"));

        var result = await AntiforgeryRoutes.IssueAsync(policy, context);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.False(ExpiresCookie(context, WebSessionCookieNames.Selected));
        Assert.False(ExpiresCookie(context, WebSessionCookieNames.Challenge));
    }

    [Fact]
    public async Task The_installation_cookie_is_never_evicted_by_this_route()
    {
        // Installation is not session state, and a refused antiforgery issuance is not the place to
        // decide its fate. Pinned so a future widening of the eviction is a deliberate act.
        var policy = new StubPolicy(issues: false);
        var context = ContextWith(
            (WebSessionCookieNames.Selected, "dead-handle"),
            (WebSessionCookieNames.Installation, "install-handle"));

        var result = await AntiforgeryRoutes.IssueAsync(policy, context);
        await result.ExecuteAsync(context);

        Assert.False(ExpiresCookie(context, WebSessionCookieNames.Installation));
    }
}
