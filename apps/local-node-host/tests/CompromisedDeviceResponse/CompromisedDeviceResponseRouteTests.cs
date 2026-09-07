using System.Net.Http.Json;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

public sealed class CompromisedDeviceResponseRouteTests
{
    private const string TeamId = "71560000-0000-0000-0000-000000000001";
    [Fact]
    public async Task Post_returns_the_durable_operator_account()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var service = new CapturingService();
        app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            http.Features.Set(new SelectedSessionRequestPrincipal(
                "account-a", new TenantId(TeamId), new PrincipalUserId("principal-a"),
                new CanonicalPartyReference("operator-a"), "membership-a", 1,
                [new PinnedGrantOwnerVersion("grant-a", 1)], 1,
                "session-a", "coordination-a"));
            await next(http);
        });
        CompromisedDeviceResponseRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            service,
            new FixedTimeProvider());
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            var response = await client.PostAsJsonAsync(
                "/api/local-node/compromised-devices/stolen-node/respond",
                new { teamId = TeamId, revokedByPartyId = "operator-a" });

            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("incident-056", body.RootElement.GetProperty("correlationId").GetString());
            Assert.Equal("durable operator account", body.RootElement.GetProperty("operatorAccount").GetString());
            Assert.Equal(
                ["contacts", "roster"],
                body.RootElement.GetProperty("entitledDocumentIds").EnumerateArray()
                    .Select(item => item.GetString()));
            Assert.Equal("deferred", body.RootElement.GetProperty("keyDisposition").GetString());
            Assert.Equal(
                ["install root seed", "team transport signing subkey"],
                body.RootElement.GetProperty("remainingExposedKeys").EnumerateArray()
                    .Select(item => item.GetString()));
            Assert.Equal("stolen-node", service.Request!.RevokedPartyId);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class CapturingService : ICompromisedDeviceResponseService
    {
        public CompromisedDeviceResponseRequest? Request { get; private set; }

        public ValueTask<CompromisedDeviceResponseResult> RespondAsync(
            CompromisedDeviceResponseRequest request,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(new CompromisedDeviceResponseResult(
                "incident-056",
                "durable operator account",
                ["contacts", "roster"],
                "deferred",
                ["install root seed", "team transport signing subkey"]));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
