using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class FounderBindRoutesTests
{
    private const string SelectedHandle = "selected-secret";
    private static readonly DateTimeOffset DesignatedAt =
        new(2026, 7, 21, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Bound_Returns_The_Designation_Facts_Without_Any_Authority_Secret()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokeAsync(
            authority,
            idempotencyKey: "key-1",
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"accountId\":\"account-1\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"tenantId\":\"tenant-1\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"ownerVersion\":7", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"replayed\":false", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(SelectedHandle, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("key-1", response.Body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Empty(response.SetCookie);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Founder_Evidence_Comes_From_The_Principal_And_Never_From_The_Request()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));

        _ = await InvokeAsync(authority, idempotencyKey: "key-1");

        var request = Assert.IsType<WebFounderBindRequest>(authority.Request);
        Assert.Equal("account-1", request.AccountId);
        Assert.Equal("tenant-1", request.TenantId.Value);
        Assert.Equal("principal-1", request.PrincipalUserId.Value);
        Assert.Equal("party-1", request.PartyId.Value);
        Assert.Equal(4, request.ExpectedSourceVersion);
        Assert.Equal("coordination-1", request.AuditCorrelationId);
        // The ONLY browser-supplied value.
        Assert.Equal("key-1", request.IdempotencyKey);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Replay_Returns_The_Same_Facts_Flagged_As_A_Replay()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Replayed, "account-1", 7, DesignatedAt));

        var response = await InvokeAsync(authority, idempotencyKey: "key-1");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"ownerVersion\":7", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"replayed\":true", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Missing_Selected_Cookie_Refuses_Before_Reaching_The_Authority()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));

        var response = await InvokeAsync(authority, idempotencyKey: "key-1", selectedHandle: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.Request);
        Assert.Contains("bind_failed", response.Body, StringComparison.Ordinal);
        Assert.Empty(response.SetCookie);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Missing_Session_Principal_Refuses_Before_Reaching_The_Authority()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));

        var response = await InvokeAsync(authority, idempotencyKey: "key-1", withPrincipal: false);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.Request);
        Assert.Contains("bind_failed", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Antiforgery_Is_Consumed_On_The_Selected_Audience_Before_Any_Authority_Call()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptSelected = false };

        var response = await InvokeAsync(authority, idempotencyKey: "key-1", antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("antiforgery_failed", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.Request);
        Assert.Equal(SelectedHandle, antiforgery.SelectedHandle);
        Assert.Null(antiforgery.ChallengeHandle);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Changed_Replay_Is_A_Conflict_That_Names_No_Stored_Evidence()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Conflict, null, 0, null));

        var response = await InvokeAsync(authority, idempotencyKey: "key-1");

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Contains("bind_conflict", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("account-1", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Existing_Designation_Refuses_A_Different_Key_As_A_Conflict()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.AlreadyDesignated, null, 0, null));

        var response = await InvokeAsync(authority, idempotencyKey: "key-2");

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Contains("already_designated", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Rejected_Command_Values_Are_A_Refused_Request_Not_A_Server_Fault()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.InvalidRequest, null, 0, null));

        var response = await InvokeAsync(authority, idempotencyKey: null);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("bind_rejected", response.Body, StringComparison.Ordinal);
        Assert.Equal(string.Empty, authority.Request!.IdempotencyKey);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public async Task Inactive_Account_Is_Indistinguishable_From_An_Unauthenticated_Refusal()
    {
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.AccountNotActive, null, 0, null));

        var response = await InvokeAsync(authority, idempotencyKey: "key-1");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Contains("bind_failed", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("account", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3003")]
    public void The_Bind_Path_Is_A_Literal_Selected_Session_Route_Outside_The_PreAuth_Allowlist()
    {
        // The fence is retired for exactly ONE path, and that path is NOT pre-auth allowlisted:
        // the listener's selected-cookie accept authenticates it before the handler runs.
        Assert.Equal("/api/session/founder-bind", FounderBindRoutes.BindPath);
        Assert.DoesNotContain('{', FounderBindRoutes.BindPath);
        Assert.False(
            Harborline.Api.LocalNodeHost.Health.NodeListenerCallerAuthPolicy.IsAllowlisted(
                FounderBindRoutes.BindPath));
    }


    /// <remarks>
    /// earlier repository ticket #3311 — the token is single-use, so a branch that returns after consuming it and does
    /// not re-issue leaves the browser unable to perform ANY state-changing action, logout included.
    /// The success statuses were already covered; every refusal below is a state a human reaches by
    /// ordinary mistake, and none of them should cost the session.
    /// </remarks>
    [Theory]
    [Trait("PlanCard", "MTW-2-3311")]
    [InlineData(WebFounderBindStatus.Conflict)]
    [InlineData(WebFounderBindStatus.AlreadyDesignated)]
    [InlineData(WebFounderBindStatus.InvalidRequest)]
    [InlineData(WebFounderBindStatus.AccountNotActive)]
    public async Task A_refused_bind_still_re_issues_the_spent_token(WebFounderBindStatus status)
    {
        var authority = new RecordingAuthority(new WebFounderBindOutcome(status, null, 0, null));
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokeAsync(authority, idempotencyKey: "key-1", antiforgery: antiforgery);

        Assert.NotEqual(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        // On the RESPONSE, not just the double — the browser has to actually receive it.
        Assert.Equal("replacement-token", response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3311")]
    public async Task A_rejected_token_is_not_re_issued()
    {
        // Fail-closed edge: re-issue happens only AFTER a successful consume, so a caller who never
        // presented a valid token is not handed one.
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptSelected = false };

        var response = await InvokeAsync(authority, idempotencyKey: "key-1", antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Null(antiforgery.RotatedSelectedHandle);
        Assert.Equal(string.Empty, response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3311")]
    public async Task A_failed_re_issue_does_not_disturb_the_operation()
    {
        // The remark on RotateSelectedAsync says a failed re-issue leaves the operation standing and the
        // client recovers through GET /api/session/antiforgery. Nothing exercised that until now: the
        // doubles could only ever answer true, so the discarded return value was an untested claim.
        var authority = new RecordingAuthority(
            new WebFounderBindOutcome(WebFounderBindStatus.Bound, "account-1", 7, DesignatedAt));
        var antiforgery = new RecordingAntiforgeryPolicy { RotateSelectedSucceeds = false };

        var response = await InvokeAsync(authority, idempotencyKey: "key-1", antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        // No header, because the real policy emits only on a successful rotation.
        Assert.Equal(string.Empty, response.Antiforgery);
    }

    private static async Task<(
        int StatusCode,
        string Body,
        string SetCookie,
        string CacheControl,
        string Antiforgery)> InvokeAsync(
        IWebFounderBindAuthority authority,
        string? idempotencyKey,
        string? selectedHandle = SelectedHandle,
        bool withPrincipal = true,
        RecordingAntiforgeryPolicy? antiforgery = null)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        antiforgery ??= new RecordingAntiforgeryPolicy();
        if (selectedHandle is not null)
        {
            context.Request.Headers.Cookie = $"__Host-hl-selected={selectedHandle}";
        }
        if (withPrincipal)
        {
            context.Features.Set(Principal());
        }
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var result = await FounderBindRoutes.BindAsync(
            authority,
            antiforgery,
            new FounderBindRoutes.BindRequest(idempotencyKey),
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

    private static SelectedSessionRequestPrincipal Principal() =>
        new(
            "account-1",
            new TenantId("tenant-1"),
            new PrincipalUserId("principal-1"),
            new CanonicalPartyReference("party-1"),
            "membership-1",
            4,
            [new PinnedGrantOwnerVersion("grant-1", 4)],
            9,
            "session-1",
            "coordination-1");

    private sealed class RecordingAuthority(WebFounderBindOutcome outcome) : IWebFounderBindAuthority
    {
        public WebFounderBindRequest? Request { get; private set; }

        public Task<WebFounderBindOutcome> BindAsync(
            WebFounderBindRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public bool AcceptSelected { get; init; } = true;

        public string? SelectedHandle { get; private set; }

        public string? ChallengeHandle { get; private set; }

        public string? RotatedSelectedHandle { get; private set; }

        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle)
        {
            ChallengeHandle = challengeHandle;
            return Task.FromResult(true);
        }

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle)
        {
            SelectedHandle = selectedHandle;
            return Task.FromResult(AcceptSelected);
        }

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public bool RotateSelectedSucceeds { get; init; } = true;

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle)
        {
            RotatedSelectedHandle = selectedHandle;
            if (!RotateSelectedSucceeds)
            {
                // The real RotateAudienceAsync returns false WITHOUT emitting on both of its failure
                // paths; a double that emitted anyway would hide exactly the case this exercises.
                return Task.FromResult(false);
            }
            // The REAL policy emits the header from inside RotateAudienceAsync. A double that records
            // the call but produces no header lets a test pass on a response the server never sends
            // (ADR 0130 anti-pattern A1) — and the header is the whole point: it is what the browser
            // needs in order to make its next request.
            context.Response.Headers[WebAntiforgeryPolicy.HeaderName] = "replacement-token";
            return Task.FromResult(true);
        }

        public void EmitToken(HttpResponse response, string token) =>
            response.Headers[WebAntiforgeryPolicy.HeaderName] = token;

        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }
}
