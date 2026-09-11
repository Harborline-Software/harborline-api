using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class TenantSwitchRoutesTests
{
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 7, 28, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Completed_Switch_Replaces_Only_Selected_Cookie_And_Hides_Handle()
    {
        var authority = new RecordingAuthority(new WebTenantSelectionResult(
            "replacement-secret",
            "replacement-antiforgery",
            "22222222-2222-2222-2222-222222222222",
            "Tenant Two",
            ExpiresAt));
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "old-secret",
            requestedTenantId: "22222222-2222-2222-2222-222222222222");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("old-secret", authority.SelectedHandle);
        Assert.Equal("old-secret", antiforgery.SelectedHandle);
        Assert.Contains("\"displayName\":\"Tenant Two\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret", response.Body, StringComparison.Ordinal);
        Assert.Contains(
            $"{WebSessionCookieNames.Selected}=replacement-secret",
            response.SetCookie,
            StringComparison.Ordinal);
        Assert.Equal("replacement-antiforgery", response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Missing_Selected_Cookie_Refuses_Without_Audience_Fallback()
    {
        var authority = new RecordingAuthority(null);
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: null,
            requestedTenantId: "22222222-2222-2222-2222-222222222222",
            additionalCookie: $"{WebSessionCookieNames.Installation}=installation-secret");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.SelectedHandle);
        Assert.Empty(response.SetCookie);
    }

    [Fact]
    [Trait("PlanCard", "3252")]
    public async Task Refused_Switch_Reissues_A_Token_That_Logout_Can_Consume()
    {
        await using var fixture = await RefusedSwitchFixture.CreateAsync();

        var refusal = await InvokeAsync(
            new RecordingAuthority(null),
            fixture.Policy,
            fixture.SelectedHandle,
            requestedTenantId: "11111111-1111-1111-1111-111111111111",
            antiforgeryToken: fixture.InitialToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, refusal.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Antiforgery));
        Assert.Equal(
            StatusCodes.Status204NoContent,
            await InvokeLogoutAsync(
                fixture.Policy,
                fixture.SelectedHandle,
                refusal.Antiforgery));
    }

    [Fact]
    [Trait("PlanCard", "3252")]
    public async Task Refused_Switch_Reissues_A_Token_For_Another_Selected_State_Change()
    {
        await using var fixture = await RefusedSwitchFixture.CreateAsync();

        var refusal = await InvokeAsync(
            new RecordingAuthority(null),
            fixture.Policy,
            fixture.SelectedHandle,
            requestedTenantId: "11111111-1111-1111-1111-111111111111",
            antiforgeryToken: fixture.InitialToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, refusal.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Antiforgery));
        Assert.Equal(
            StatusCodes.Status200OK,
            await InvokeInvitationAsync(
                fixture.Policy,
                fixture.SelectedHandle,
                refusal.Antiforgery));
    }

    private static async Task<(
        int StatusCode,
        string Body,
        string SetCookie,
        string Antiforgery)> InvokeAsync(
        IWebTenantSwitchAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        string? selectedHandle,
        string? requestedTenantId,
        string? additionalCookie = null,
        string? antiforgeryToken = null)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var cookies = new List<string>();
        if (selectedHandle is not null)
        {
            cookies.Add($"{WebSessionCookieNames.Selected}={selectedHandle}");
        }
        if (additionalCookie is not null)
        {
            cookies.Add(additionalCookie);
        }
        if (cookies.Count > 0)
        {
            context.Request.Headers.Cookie = string.Join("; ", cookies);
        }
        if (antiforgeryToken is not null)
        {
            context.Request.Headers[WebAntiforgeryPolicy.HeaderName] = antiforgeryToken;
        }
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var result = await TenantSwitchRoutes.SwitchAsync(
            authority,
            antiforgery,
            new TenantSwitchRoutes.SwitchRequest(requestedTenantId),
            context);
        await result.ExecuteAsync(context);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return (
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.SetCookie.ToString(),
            context.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString());
    }

    private static async Task<int> InvokeLogoutAsync(
        IWebAntiforgeryPolicy antiforgery,
        string selectedHandle,
        string antiforgeryToken)
    {
        var context = BuildSelectedContext(selectedHandle, antiforgeryToken);
        // A selected handle is presented, so the route must dispatch to the SELECTED branch and
        // never reach the legacy authority — hence the refusing legacy double.
        var result = await SessionLogoutRoutes.LogoutAsync(
            new SuccessfulLogoutAuthority(),
            new SelectedOnlyLegacyAuthority(),
            antiforgery,
            context);
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static async Task<int> InvokeInvitationAsync(
        IWebAntiforgeryPolicy antiforgery,
        string selectedHandle,
        string antiforgeryToken)
    {
        var context = BuildSelectedContext(selectedHandle, antiforgeryToken);
        context.Features.Set(new SelectedSessionRequestPrincipal(
            "account-1",
            new TenantId("tenant-1"),
            new PrincipalUserId("principal-1"),
            new CanonicalPartyReference("party-1"),
            "membership-1",
            4,
            [new PinnedGrantOwnerVersion("grant-1", 4)],
            9,
            "session-1",
            "coordination-1"));
        var result = await AdminTeamAccessRoutes.IssueInvitationAsync(
            new SuccessfulAdminAuthority(),
            antiforgery,
            new AdminTeamAccessRoutes.IssueInvitationRequest(["records:read"], "idem-1"),
            context,
            Now);
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static DefaultHttpContext BuildSelectedContext(
        string selectedHandle,
        string antiforgeryToken)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Cookie =
            $"{WebSessionCookieNames.Selected}={selectedHandle}";
        context.Request.Headers[WebAntiforgeryPolicy.HeaderName] = antiforgeryToken;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class RecordingAuthority(WebTenantSelectionResult? result)
        : IWebTenantSwitchAuthority
    {
        public string? SelectedHandle { get; private set; }

        public Task<WebTenantSelectionResult?> SwitchAsync(
            string? selectedHandle,
            string? requestedTenantId,
            CancellationToken cancellationToken = default)
        {
            SelectedHandle = selectedHandle;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public string? SelectedHandle { get; private set; }

        public string? RotatedSelectedHandle { get; private set; }

        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle)
        {
            SelectedHandle = selectedHandle;
            return Task.FromResult(true);
        }

        public Task<bool> ConsumeInstallationAsync(
            HttpContext context,
            string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle)
        {
            RotatedSelectedHandle = selectedHandle;
            return Task.FromResult(true);
        }

        public void EmitToken(HttpResponse response, string token) =>
            response.Headers[WebAntiforgeryPolicy.HeaderName] = token;

        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }

    private sealed class SuccessfulLogoutAuthority : IWebSelectedSessionLogoutAuthority
    {
        public Task<bool> LogoutAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// The legacy web-session authority for a SELECTED-ONLY sign-out (#3343). The selected branch
    /// now consults it so a both-cookie holder is not left with a live legacy session, but these
    /// contexts carry no legacy credential — so it must find nothing and expire no legacy cookie.
    /// Every other member still throws: nothing else on this path may reach the legacy authority.
    /// </summary>
    private sealed class SelectedOnlyLegacyAuthority : INodeWebSessionAuthority
    {
        private static InvalidOperationException Violation() =>
            new("audience violation: a selected-session sign-out reached the LEGACY authority.");

        public bool IsEnabled => true;

        public Task<bool> TryAuthenticateAsync(HttpContext context) => throw Violation();

        public Task<WebLoginAttemptResult> LoginAsync(string? username, string? password, CancellationToken ct) =>
            throw Violation();

        // No legacy credential is present in these contexts, so the real authority would report
        // false and the route would skip the cookie expiry below.
        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) =>
            Task.FromResult(false);

        public void IssueSessionCookie(HttpContext context, WebLoginResult login) => throw Violation();

        public void ClearSessionCookie(HttpContext context) =>
            throw new InvalidOperationException(
                "audience violation: a selected-only sign-out expired the LEGACY cookie.");

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            throw Violation();
    }

    private sealed class SuccessfulAdminAuthority : IAdminTeamAccessAuthority
    {
        public Task<AdminTeamMembersResult?> ListMembersAsync(
            string selectedSessionHandle,
            string tenantId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminPendingInvitationsResult?> ListPendingInvitationsAsync(
            string selectedSessionHandle,
            string tenantId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminIssuedInvitation?> IssueInvitationAsync(
            string selectedSessionHandle,
            string tenantId,
            IReadOnlyCollection<string> requestedPermissions,
            string idempotencyKey,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AdminIssuedInvitation?>(
                new("invitation-1", "raw-code", tenantId, ExpiresAt));

        public Task<AdminRevokeMemberResult?> RevokeMemberGrantAsync(
            string selectedSessionHandle,
            string tenantId,
            string grantId,
            AuthorizationWriteContext authority,
            string? successorPrincipalId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // Ticket 362 - this fixture never narrows; the member surface is not what it is asserting.
        public Task<AdminNarrowMemberGrantResult?> NarrowMemberGrantAsync(
            string selectedSessionHandle,
            string tenantId,
            string grantId,
            IReadOnlyCollection<string> narrowedPermissions,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RefusedSwitchFixture : IAsyncDisposable
    {
        private readonly string _path;

        private RefusedSwitchFixture(
            string path,
            WebAntiforgeryPolicy policy,
            string selectedHandle,
            string initialToken)
        {
            _path = path;
            Policy = policy;
            SelectedHandle = selectedHandle;
            InitialToken = initialToken;
        }

        internal WebAntiforgeryPolicy Policy { get; }

        internal string SelectedHandle { get; }

        internal string InitialToken { get; }

        internal static async Task<RefusedSwitchFixture> CreateAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"tenant-switch-antiforgery-{Guid.NewGuid():N}.db");
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(path);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }
            var clock = new FixedTimeProvider();
            var store = new WebAntiforgeryStateStore(factory, clock);
            var issue = await store.RotateAsync(
                WebCookieAudience.SelectedSession,
                "account-1",
                "session-1",
                "coordination-1",
                ExpiresAt);
            Assert.NotNull(issue);

            const string selectedHandle = "selected-secret";
            await using (var context = factory.CreateDbContext())
            {
                context.UserSessions.Add(new WebUserSessionRecord(
                    "session-1",
                    "account-1",
                    2,
                    "tenant-1",
                    "membership-1",
                    4,
                    "principal-1",
                    "party-1",
                    [new PinnedGrantOwnerVersion("grant-1", 4)],
                    9,
                    WebAntiforgeryStateStore.Digest(selectedHandle),
                    issue.State.AntiforgeryStateId,
                    "coordination-1",
                    Now,
                    Now.AddMinutes(30),
                    ExpiresAt,
                    1));
                await context.SaveChangesAsync();
            }

            return new RefusedSwitchFixture(
                path,
                new WebAntiforgeryPolicy(
                    factory,
                    new WebSelectedSessionStore(factory),
                    store,
                    clock),
                selectedHandle,
                issue.Token);
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
