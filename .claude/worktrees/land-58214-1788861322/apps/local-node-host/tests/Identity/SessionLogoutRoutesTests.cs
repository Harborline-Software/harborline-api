using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Route-level DISPATCH proof for <c>POST /api/session/logout</c>: which audience's authority is
/// reached, which cookie is expired, and which collaborator is never touched.
/// </summary>
/// <remarks>
/// These use recording doubles ON PURPOSE — the question here is dispatch, and a double is the only
/// way to assert that the other audience's authority was NOT called. The claim that revocation
/// actually reaches durable state is proved elsewhere against the real substrate:
/// <c>NodeWebSessionAuthorityTests</c> drives the real <see cref="NodeWebSessionAuthority"/> + real
/// session store over a real listener, and <c>WebSelectedSessionLogoutAuthorityTests</c> drives the
/// real selected-session coordinator. Neither claim rests on a double.
/// </remarks>
public sealed class SessionLogoutRoutesTests
{
    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Completed_Logout_Deletes_Only_Selected_Cookie_With_Matching_Attributes()
    {
        var authority = new RecordingAuthority(true);
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "selected-secret",
            antiforgeryToken: RecordingAntiforgeryPolicy.ValidToken);

        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);
        Assert.Equal("selected-secret", authority.SelectedHandle);
        Assert.Equal(1, authority.InvocationCount);
        Assert.Equal("selected-secret", antiforgery.SelectedHandle);
        Assert.Contains("__Host-hl-selected=", response.SetCookie, StringComparison.Ordinal);
        Assert.Contains("path=/", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__Host-web_session", response.SetCookie, StringComparison.Ordinal);
        Assert.Equal("no-store", response.CacheControl);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Unconfirmed_Logout_Stays_NonSuccess_And_Does_Not_Delete_Cookies()
    {
        var authority = new RecordingAuthority(false);
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "selected-secret",
            antiforgeryToken: RecordingAntiforgeryPolicy.ValidToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Empty(response.SetCookie);
        Assert.Contains("logout_failed", response.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent or EMPTY selected cookie is not a selected-audience request. It falls through to
    /// the legacy branch, which refuses when no legacy credential is presented either — the
    /// no-audience case, not a selected-audience failure.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [Trait("PlanCard", "MTW-2")]
    public async Task Absent_Or_Empty_Selected_Handle_Is_Not_A_Selected_Audience_Request(
        string? selectedHandle)
    {
        var authority = new RecordingAuthority(true);
        var antiforgery = new RecordingAntiforgeryPolicy();
        var legacy = new RecordingLegacyAuthority(credentialPresented: false);

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle,
            RecordingAntiforgeryPolicy.ValidToken,
            legacyAuthority: legacy);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Empty(response.SetCookie);
        Assert.Equal(0, authority.InvocationCount);
        Assert.Null(antiforgery.SelectedHandle);
    }

    /// <summary>
    /// The MTW-01C audience-separation property, restated correctly (#3343). It used to be asserted
    /// as "a legacy cookie makes logout FAIL" — which froze the founder-facing defect into a green
    /// test. The property that actually matters is unchanged and still asserted here: a legacy
    /// cookie is never resolved as a SELECTED handle, and never consumes selected antiforgery
    /// state. What changed is that the holder now gets a working, revoking sign-out.
    /// </summary>
    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Legacy_Cookie_Is_Never_Used_As_Selected_Audience_Fallback()
    {
        var authority = new RecordingAuthority(false);
        var antiforgery = new RecordingAntiforgeryPolicy();
        var legacy = new RecordingLegacyAuthority(credentialPresented: true);

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: null,
            antiforgeryToken: RecordingAntiforgeryPolicy.ValidToken,
            additionalCookie: "__Host-web_session=legacy-secret",
            legacyAuthority: legacy);

        // The legacy holder gets a confirmed, revoking sign-out.
        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);
        Assert.Equal(1, legacy.LogoutInvocationCount);
        Assert.True(legacy.SessionCookieCleared);
        Assert.Equal("no-store", response.CacheControl);

        // ...and the selected audience is untouched: its authority never saw a handle, its
        // one-time antiforgery state was never consumed, and no selected cookie was written.
        Assert.Null(authority.SelectedHandle);
        Assert.Equal(0, authority.InvocationCount);
        Assert.Null(antiforgery.SelectedHandle);
        Assert.DoesNotContain("__Host-hl-", response.SetCookie, StringComparison.Ordinal);
    }

    /// <summary>
    /// A holder of BOTH cookies signs out of BOTH. The selected handle is resolved by the selected
    /// authority and the legacy credential by the legacy authority — separation is "each handle
    /// through its own authority", which is preserved here, NOT "the other session survives".
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite outcome — that the legacy session was neither
    /// revoked nor its cookie cleared — which is the pinned trap `bug-20260729-cabe04e3` recurring
    /// one case over: read as English it was a user-facing failure ("sign-out leaves the other
    /// session live"). It was not dormant. `SharedHostedWebApp.ClassifyWebCookieAudience` returns
    /// `Selected` while the selected cookie is present, so the legacy record is never consulted;
    /// the moment logout deletes that cookie the classification falls to `LegacyEligible` and the
    /// listener's accept path re-admits the browser on the surviving record, putting the user
    /// straight back into the app on the Harborline App's next boot check.
    /// </remarks>
    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Both_Cookie_Holder_Signs_Out_Of_Both_Audiences()
    {
        var authority = new RecordingAuthority(true);
        var antiforgery = new RecordingAntiforgeryPolicy();
        var legacy = new RecordingLegacyAuthority(credentialPresented: true);

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "selected-secret",
            antiforgeryToken: RecordingAntiforgeryPolicy.ValidToken,
            additionalCookie: "__Host-web_session=legacy-secret",
            legacyAuthority: legacy);

        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);

        // Each handle went to its own authority — the separation property, still asserted.
        Assert.Equal("selected-secret", authority.SelectedHandle);
        Assert.Equal(1, authority.InvocationCount);
        Assert.Equal("selected-secret", antiforgery.SelectedHandle);

        // ...and NO audience is left live behind the sign-out.
        Assert.Equal(1, legacy.LogoutInvocationCount);
        Assert.True(legacy.SessionCookieCleared);
        Assert.Contains("__Host-hl-selected=", response.SetCookie, StringComparison.Ordinal);
    }

    /// <summary>A request carrying no credential of either audience is refused, and mutates nothing.</summary>
    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task No_Credential_Of_Either_Audience_Refuses_And_Mutates_No_Cookie()
    {
        var authority = new RecordingAuthority(true);
        var antiforgery = new RecordingAntiforgeryPolicy();
        var legacy = new RecordingLegacyAuthority(credentialPresented: false);

        var response = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: null,
            antiforgeryToken: null,
            legacyAuthority: legacy);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Contains("no_session", response.Body, StringComparison.Ordinal);
        Assert.Empty(response.SetCookie);
        Assert.False(legacy.SessionCookieCleared);
        Assert.Equal(0, authority.InvocationCount);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Missing_Or_Replayed_Antiforgery_Refuses_Before_Logout()
    {
        var authority = new RecordingAuthority(true);
        var antiforgery = new RecordingAntiforgeryPolicy();

        var missing = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "selected-secret",
            antiforgeryToken: null);
        var accepted = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "selected-secret",
            antiforgeryToken: RecordingAntiforgeryPolicy.ValidToken);
        var replay = await InvokeAsync(
            authority,
            antiforgery,
            selectedHandle: "selected-secret",
            antiforgeryToken: RecordingAntiforgeryPolicy.ValidToken);

        Assert.Equal(StatusCodes.Status400BadRequest, missing.StatusCode);
        Assert.Contains("antiforgery_failed", missing.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status204NoContent, accepted.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, replay.StatusCode);
        Assert.Contains("antiforgery_failed", replay.Body, StringComparison.Ordinal);
        Assert.Equal(1, authority.InvocationCount);
    }

    private static async Task<(int StatusCode, string Body, string SetCookie, string CacheControl)>
        InvokeAsync(
            IWebSelectedSessionLogoutAuthority authority,
            IWebAntiforgeryPolicy antiforgery,
            string? selectedHandle,
            string? antiforgeryToken,
            string? additionalCookie = null,
            INodeWebSessionAuthority? legacyAuthority = null)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var cookies = new List<string>();
        if (selectedHandle is not null)
        {
            cookies.Add($"__Host-hl-selected={selectedHandle}");
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
        // A selected-ONLY case supplies no legacy authority: the default fails the test loudly if a
        // legacy credential turns out to be present, or if a legacy cookie is ever expired.
        var result = await SessionLogoutRoutes.LogoutAsync(
            authority,
            legacyAuthority ?? SelectedOnlyLegacyAuthority.Instance,
            antiforgery,
            context);
        await result.ExecuteAsync(context);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return (
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.SetCookie.ToString(),
            context.Response.Headers.CacheControl.ToString());
    }

    private sealed class RecordingAuthority(bool result) : IWebSelectedSessionLogoutAuthority
    {
        public string? SelectedHandle { get; private set; }
        public int InvocationCount { get; private set; }

        public Task<bool> LogoutAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default)
        {
            SelectedHandle = selectedHandle;
            InvocationCount++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Records the legacy-audience calls the route makes (and only those).</summary>
    private class RecordingLegacyAuthority(bool credentialPresented) : INodeWebSessionAuthority
    {
        public int LogoutInvocationCount { get; private set; }

        public bool SessionCookieCleared { get; private set; }

        public bool IsEnabled => true;

        public virtual Task<bool> LogoutAsync(HttpContext context, CancellationToken ct)
        {
            LogoutInvocationCount++;
            return Task.FromResult(credentialPresented);
        }

        public virtual void ClearSessionCookie(HttpContext context) => SessionCookieCleared = true;

        public Task<bool> TryAuthenticateAsync(HttpContext context) =>
            throw new NotSupportedException();

        public Task<WebLoginAttemptResult> LoginAsync(string? username, string? password, CancellationToken ct) =>
            throw new NotSupportedException();

        public void IssueSessionCookie(HttpContext context, WebLoginResult login) =>
            throw new NotSupportedException();

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The legacy authority for SELECTED-ONLY cases. The selected branch now legitimately consults
    /// it (a both-cookie holder must not be left with a live legacy session — F1), so refusing every
    /// call would be wrong. What it still guards is the separation property: the real authority
    /// reads only its OWN transports from the request, so on a selected-only request it must find
    /// nothing, and no legacy cookie may be expired.
    /// </summary>
    private sealed class SelectedOnlyLegacyAuthority : RecordingLegacyAuthority
    {
        internal static readonly SelectedOnlyLegacyAuthority Instance = new();

        private SelectedOnlyLegacyAuthority()
            : base(credentialPresented: false)
        {
        }

        public override Task<bool> LogoutAsync(HttpContext context, CancellationToken ct)
        {
            // The selected handle must never be visible to the legacy authority as a credential.
            if (context.Request.Cookies.ContainsKey(NodeWebSessionAuthority.SessionCookieName) ||
                context.Request.Headers.ContainsKey("Authorization"))
            {
                throw new InvalidOperationException(
                    "test premise violated: this case is selected-ONLY, but the request carries a " +
                    "legacy credential.");
            }
            return base.LogoutAsync(context, ct);
        }

        public override void ClearSessionCookie(HttpContext context) =>
            throw new InvalidOperationException(
                "audience violation: a selected-only sign-out expired the LEGACY cookie.");
    }

    private sealed class RecordingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        internal const string ValidToken = "selected-antiforgery";

        private bool _consumed;

        public string? SelectedHandle { get; private set; }

        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle)
        {
            SelectedHandle = selectedHandle;
            var accepted = !_consumed &&
                string.Equals(
                    context.Request.Headers[WebAntiforgeryPolicy.HeaderName],
                    ValidToken,
                    StringComparison.Ordinal);
            _consumed |= accepted;
            return Task.FromResult(accepted);
        }

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
            throw new NotSupportedException();

        public void EmitToken(HttpResponse response, string token)
        {
        }

        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }
}
