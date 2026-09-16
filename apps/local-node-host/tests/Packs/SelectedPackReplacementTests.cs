using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Fact]
    public async Task Selected_replacement_reports_draft_and_activation_separately()
    {
        await PreloadPlatformThenAccessAsync();
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var bytes = await ExportAsync(source with { Version = "1.1.2" });
        var http = ReplacementHttp(bytes);
        var result = await ReplaceAsync(http, source.Key);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var receipt = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(receipt.GetProperty("draftInstall").GetProperty("installed").GetBoolean());
        Assert.True(receipt.GetProperty("activation").GetProperty("activated").GetBoolean());
        Assert.True(receipt.GetProperty("activation").GetProperty("projected").GetBoolean());
        Assert.Equal("1.1.1", receipt.GetProperty("activeBefore").GetProperty("version").GetString());
        Assert.Equal("1.1.2", receipt.GetProperty("activeAfter").GetProperty("version").GetString());
        Assert.Equal(5, receipt.GetProperty("activeAfter").GetProperty("declaredDefinitions").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_replacement_refuses_before_body_read_without_authority_or_csrf(bool missingCsrf)
    {
        await PreloadPlatformThenAccessAsync();
        var http = ReplacementHttp([1, 2, 3]);
        if (missingCsrf) http.Request.Headers.Remove("X-Harborline-Antiforgery");
        else
        {
            var services = new ServiceCollection().AddSingleton(TestAuthorization.Gate(false)).BuildServiceProvider();
            http.RequestServices = services;
        }
        var before = JsonSerializer.Serialize(_store.ListInstalled(Tenant));
        var result = await ReplaceAsync(http, AccessAdministrationPreloadHostedService.PackKey);
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, http.Request.Body.Position);
        Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(Tenant)));
    }

    [Fact]
    public async Task Selected_replacement_install_refusal_keeps_active_and_installed_state_unchanged()
    {
        await PreloadPlatformThenAccessAsync();
        var before = JsonSerializer.Serialize(_store.ListInstalled(Tenant));
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var bytes = await ExportAsync(source with { Version = "1.0.0" });
        var result = await ReplaceAsync(ReplacementHttp(bytes), source.Key);
        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
        var receipt = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(receipt.GetProperty("draftInstall").GetProperty("installed").GetBoolean());
        Assert.False(receipt.GetProperty("activation").GetProperty("attempted").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(Tenant)));
    }

    [Fact]
    public async Task Selected_replacement_invalid_artifact_keeps_active_and_installed_state_unchanged()
    {
        await PreloadPlatformThenAccessAsync();
        var before = JsonSerializer.Serialize(_store.ListInstalled(Tenant));
        var result = await ReplaceAsync(ReplacementHttp([1, 2, 3]), AccessAdministrationPreloadHostedService.PackKey);
        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(Tenant)));
    }

    private Task<IResult> ReplaceAsync(HttpContext http, string packKey) => SelectedPackReplacementRoutes.ReplaceAsync(
        http, packKey, _installer, _store, TrustingTheNodeKey(), PackRevocationList.Empty,
        new ReplacementAntiforgery(), TimeProvider.System, null, CancellationToken.None);

    private DefaultHttpContext ReplacementHttp(byte[] bytes)
    {
        var http = new DefaultHttpContext { RequestServices = _app.Services };
        http.Features.Set(new SelectedSessionRequestPrincipal("account", Tenant,
            new PrincipalUserId("replacement-admin"), new CanonicalPartyReference("attribution-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination"));
        http.Request.Headers.Cookie = "__Host-hl-selected=selected-handle";
        http.Request.Headers["X-Harborline-Antiforgery"] = "test-csrf";
        http.Request.ContentType = "application/octet-stream";
        http.Request.Body = new MemoryStream(bytes);
        return http;
    }

    private sealed class ReplacementAntiforgery : IWebAntiforgeryPolicy
    {
        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) => Task.FromResult(context.Request.Headers["X-Harborline-Antiforgery"] == "test-csrf");
        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) => Task.FromResult(true);
        public Task<bool> IssueAnonymousAsync(HttpContext context) => throw new NotSupportedException();
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => throw new NotSupportedException();
        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) => throw new NotSupportedException();
        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) => throw new NotSupportedException();
        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) => throw new NotSupportedException();
        public void EmitToken(HttpResponse response, string token) => throw new NotSupportedException();
        public void ExpireAnonymousBinding(HttpResponse response) => throw new NotSupportedException();
    }
}
