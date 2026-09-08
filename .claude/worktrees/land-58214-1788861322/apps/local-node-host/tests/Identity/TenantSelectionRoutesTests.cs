using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class TenantSelectionRoutesTests
{
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 7, 18, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Success_Rotates_Challenge_To_Selected_Cookie_Without_Returning_Handle()
    {
        var authority = new RecordingAuthority(
            new WebTenantSelectionResult(
                "selected-secret",
                "selected-antiforgery",
                "tenant-1",
                "Tenant One",
                ExpiresAt));

        var response = await InvokeAsync(authority, "challenge-secret", "tenant-1");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("challenge-secret", authority.ChallengeHandle);
        Assert.Equal("tenant-1", authority.RequestedTenantId);
        Assert.Contains("\"tenantId\":\"tenant-1\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"Tenant One\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-secret", response.Body, StringComparison.Ordinal);
        Assert.Contains("__Host-hl-selected=selected-secret", response.SetCookie, StringComparison.Ordinal);
        Assert.Contains("__Host-hl-challenge=", response.SetCookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal("selected-antiforgery", response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Missing_Challenge_Refuses_Without_Setting_Or_Deleting_Any_Cookie()
    {
        var authority = new RecordingAuthority(null);

        var response = await InvokeAsync(authority, challengeHandle: null, requestedTenantId: "tenant-1");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.ChallengeHandle);
        Assert.Empty(response.SetCookie);
        Assert.Contains("selection_failed", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Omitted_Tenant_Is_Passed_To_The_Authority_For_ExactlyOne_Classification()
    {
        var authority = new RecordingAuthority(null);

        _ = await InvokeAsync(authority, "challenge-secret", requestedTenantId: null);

        Assert.Equal("challenge-secret", authority.ChallengeHandle);
        Assert.Null(authority.RequestedTenantId);
    }

    [Fact]
    [Trait("PlanCard", "SES-06A")]
    public async Task Missing_Or_Replayed_Antiforgery_Refuses_Before_Selection()
    {
        var authority = new RecordingAuthority(null);
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptChallenge = false };

        var response = await InvokeAsync(
            authority,
            "challenge-secret",
            "tenant-1",
            antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("antiforgery_failed", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.ChallengeHandle);
    }

    private static async Task<(
        int StatusCode,
        string Body,
        string SetCookie,
        string CacheControl,
        string Antiforgery)> InvokeAsync(
        IWebTenantSelectionAuthority authority,
        string? challengeHandle,
        string? requestedTenantId,
        RecordingAntiforgeryPolicy? antiforgery = null)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        antiforgery ??= new RecordingAntiforgeryPolicy();
        if (challengeHandle is not null)
        {
            context.Request.Headers.Cookie = $"__Host-hl-challenge={challengeHandle}";
        }
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var result = await TenantSelectionRoutes.SelectAsync(
            authority,
            antiforgery,
            new TenantSelectionRoutes.SelectRequest(requestedTenantId),
            context);
        await result.ExecuteAsync(context);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return (
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.SetCookie.ToString(),
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString());
    }

    private sealed class RecordingAuthority(WebTenantSelectionResult? result)
        : IWebTenantSelectionAuthority
    {
        public string? ChallengeHandle { get; private set; }

        public string? RequestedTenantId { get; private set; }

        public Task<WebTenantSelectionResult?> SelectAsync(
            string? challengeHandle,
            string? requestedTenantId,
            CancellationToken cancellationToken = default)
        {
            ChallengeHandle = challengeHandle;
            RequestedTenantId = requestedTenantId;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public bool AcceptChallenge { get; init; } = true;

        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(AcceptChallenge);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
            throw new NotSupportedException();

        public void EmitToken(HttpResponse response, string token) =>
            response.Headers[WebAntiforgeryPolicy.HeaderName] = token;

        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }
}
