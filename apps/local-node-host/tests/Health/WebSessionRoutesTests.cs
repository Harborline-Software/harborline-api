using System.IO;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>Direct route proofs for legacy login reservation cleanup and refusal classification.</summary>
public sealed class WebSessionRoutesTests
{
    [Fact(DisplayName = "An abandoned legacy login releases its pending rate-limit reservation")]
    public async Task Login_Releases_Pending_Reservation_When_Authority_Throws()
    {
        var authority = new ThrowOnceAuthority();
        var limiter = BuildLimiter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(
            authority,
            limiter,
            new WebSessionRoutes.LoginRequest("founder", "password")));

        var retry = await InvokeAsync(
            authority,
            limiter,
            new WebSessionRoutes.LoginRequest("founder", "password"));
        Assert.Equal(StatusCodes.Status200OK, retry);
    }

    [Fact(DisplayName = "Typed legacy provisioning refusals do not consume the login budget")]
    public async Task Login_Does_Not_Count_Provisioning_Refusal()
    {
        var authority = new ProvisioningRefusingAuthority();
        var limiter = BuildLimiter();

        var first = await InvokeAsync(
            authority,
            limiter,
            new WebSessionRoutes.LoginRequest("founder", "password"));
        var second = await InvokeAsync(
            authority,
            limiter,
            new WebSessionRoutes.LoginRequest("founder", "password"));

        Assert.Equal(StatusCodes.Status401Unauthorized, first);
        Assert.Equal(StatusCodes.Status401Unauthorized, second);
        Assert.Equal(2, authority.Calls);
    }

    private static WebLoginRateLimiter BuildLimiter() =>
        new(
            Options.Create(new NodeWebClientOptions
            {
                Lockout = new NodeWebLoginLockoutOptions { MaxFailures = 1 },
            }),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance);

    private static async Task<int> InvokeAsync(
        INodeWebSessionAuthority authority,
        WebLoginRateLimiter limiter,
        WebSessionRoutes.LoginRequest request)
    {
        await using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Response.Body = responseBody;

        var result = await WebSessionRoutes.LoginAsync(authority, limiter, request, context);
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private sealed class ThrowOnceAuthority : INodeWebSessionAuthority
    {
        private int _calls;

        public bool IsEnabled => true;

        public Task<bool> TryAuthenticateAsync(HttpContext context) => Task.FromResult(false);

        public Task<WebLoginAttemptResult> LoginAsync(
            string? username,
            string? password,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new OperationCanceledException(ct);
            }

            return Task.FromResult(new WebLoginAttemptResult(
                new WebLoginResult("token", "user", "Founder", DateTimeOffset.UtcNow.AddMinutes(5)),
                null));
        }

        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) => Task.FromResult(false);

        public void IssueSessionCookie(HttpContext context, WebLoginResult login) { }

        public void ClearSessionCookie(HttpContext context) { }

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            Task.FromResult<WebSessionSummary?>(null);
    }

    private sealed class ProvisioningRefusingAuthority : INodeWebSessionAuthority
    {
        public int Calls { get; private set; }

        public bool IsEnabled => true;

        public Task<bool> TryAuthenticateAsync(HttpContext context) => Task.FromResult(false);

        public Task<WebLoginAttemptResult> LoginAsync(
            string? username,
            string? password,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new WebLoginAttemptResult(
                null,
                WebLoginFailureReason.ProvisioningRefused));
        }

        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) => Task.FromResult(false);

        public void IssueSessionCookie(HttpContext context, WebLoginResult login) { }

        public void ClearSessionCookie(HttpContext context) { }

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            Task.FromResult<WebSessionSummary?>(null);
    }
}
