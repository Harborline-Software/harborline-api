using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class AccountSetupAcceptRateLimitTests
{
    private static readonly IPAddress SharedSourceAddress = IPAddress.Parse("192.0.2.44");

    [Fact(DisplayName = "Sustained anonymous redemption returns 429 before another credential derivation")]
    public async Task Sustained_Anonymous_Redemption_Is_Throttled_Before_Credential_Derivation()
    {
        AssertProductionRegistration();
        var credentials = new CountingCredentialFactory();
        var limiter = new PairingRedeemRateLimiter(perSourceMax: 1, perTenantMax: 10, clock: TimeProvider.System);
        var request = Request("stable-invitation-code", "load-probe");

        var first = await InvokeAsync(credentials, limiter, request);
        var throttled = await InvokeAsync(credentials, limiter, request);

        Assert.Equal(StatusCodes.Status401Unauthorized, first);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.Equal(1, credentials.Calls);
    }

    [Fact(DisplayName = "Distinct invitees sharing one source keep separate redemption allowances")]
    public async Task Distinct_Invitees_Sharing_A_Source_Are_Not_Throttled()
    {
        var credentials = new CountingCredentialFactory();
        var limiter = new PairingRedeemRateLimiter(perSourceMax: 1, perTenantMax: 120, clock: TimeProvider.System);

        var statuses = new List<int>();
        for (var invitee = 0; invitee < 8; invitee++)
        {
            statuses.Add(await InvokeAsync(
                credentials,
                limiter,
                Request($"invitation-{invitee}", $"invitee-{invitee}")));
        }

        Assert.All(statuses, status => Assert.Equal(StatusCodes.Status401Unauthorized, status));
        Assert.Equal(8, credentials.Calls);
    }

    [Fact(DisplayName = "The shared limiter leaves twenty per-invitation attempts to the durable attempt bound")]
    public async Task Invitation_Allowance_Does_Not_Preempt_The_Durable_Attempt_Bound()
    {
        const int invitationAttemptCompatibilityFloor = 20;
        var credentials = new CountingCredentialFactory();
        var limiter = new PairingRedeemRateLimiter(clock: TimeProvider.System);
        var request = Request("retryable-invitation", "invitee");

        var statuses = new List<int>();
        for (var attempt = 0; attempt < invitationAttemptCompatibilityFloor; attempt++)
        {
            statuses.Add(await InvokeAsync(credentials, limiter, request));
        }

        Assert.All(statuses, status => Assert.Equal(StatusCodes.Status401Unauthorized, status));
        Assert.Equal(invitationAttemptCompatibilityFloor, credentials.Calls);
    }

    [Fact(DisplayName = "Cycling invitation and tenant input cannot evade the route-wide load bound")]
    public async Task Route_Wide_Bound_Cannot_Be_Evaded_By_Cycling_Request_Input()
    {
        var credentials = new CountingCredentialFactory();
        var limiter = new PairingRedeemRateLimiter(perSourceMax: 10, perTenantMax: 1, clock: TimeProvider.System);

        var first = await InvokeAsync(credentials, limiter, Request("invitation-a", "invitee-a"));
        var secondRequest = Request("invitation-b", "invitee-b") with
        {
            TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        };
        var throttled = await InvokeAsync(credentials, limiter, secondRequest);

        Assert.Equal(StatusCodes.Status401Unauthorized, first);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.Equal(1, credentials.Calls);
    }

    private static AccountSetupAcceptRoutes.AcceptRequest Request(string code, string username) =>
        new(
            Code: code,
            TenantId: "11111111-2222-3333-4444-555555555555",
            Username: username,
            Password: "correct horse battery staple");

    private static async Task<int> InvokeAsync(
        IWebChosenCredentialFactory credentials,
        PairingRedeemRateLimiter limiter,
        AccountSetupAcceptRoutes.AcceptRequest request)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddRouting()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = SharedSourceAddress;
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var result = await AccountSetupAcceptRoutes.AcceptAsync(
            new RefusingAuthority(),
            credentials,
            new AcceptingAntiforgeryPolicy(),
            limiter,
            request,
            context);
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static void AssertProductionRegistration()
    {
        var hostRoot = FindHostSourceRoot();
        var program = ReadSource(Path.Combine(hostRoot, "Program.cs"));
        const string singletonRegistration =
            "AddSingleton(sp => new Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemRateLimiter(\n" +
            "        sp.GetRequiredService<TimeProvider>()))";
        Assert.Equal(1, CountOccurrences(program, singletonRegistration));

        var registrar = ReadSource(Path.Combine(
            hostRoot,
            "Health",
            "WebSession",
            "HostedWebSessionApiEndpoint.cs"));
        const string routeRegistration =
            "AccountSetupAcceptRoutes.Map(\n" +
            "            preAuth,\n" +
            "            _acceptance,\n" +
            "            _chosenCredentials,\n" +
            "            _antiforgery,\n" +
            "            _redeemRateLimiter);";
        var registrationCount = CountOccurrences(registrar, routeRegistration);
        Assert.True(
            registrationCount == 1,
            $"Expected one production route registration, but found {registrationCount}. " +
            $"Searched for the literal:\n\"{routeRegistration}\"");
    }

    // The literals below are newline-sensitive and .gitattributes pins these sources to LF, but an
    // editor on Windows can still leave CRLF in the working tree. Compare on normalised text.
    private static string ReadSource(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindHostSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "local-node-host", "Program.cs");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)
                    ?? throw new InvalidOperationException("Program.cs has no parent directory.");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }

    private sealed class CountingCredentialFactory : IWebChosenCredentialFactory
    {
        public int Calls { get; private set; }

        public WebChosenCredential Create(string? password)
        {
            Calls++;
            return new WebChosenCredential("canonical-test-artifact", "ceremony");
        }
    }

    private sealed class RefusingAuthority : IAccountSetupAcceptanceAuthority
    {
        public Task<AccountSetupAcceptResult> AcceptAsync(
            AccountSetupAcceptCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountSetupAcceptResult(
                AccountSetupAcceptStatus.InvitationRefused,
                AccountId: null));
    }

    private sealed class AcceptingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public void EmitToken(HttpResponse response, string token)
        {
        }

        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }
}
