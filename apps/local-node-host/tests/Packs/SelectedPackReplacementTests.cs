using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Install;
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
        var receipt = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(((IStatusCodeHttpResult)result).StatusCode == 200, receipt.GetRawText());
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
    public async Task Selected_replacement_exposes_native_activation_refusal_as_422_with_inactive_draft()
    {
        await PreloadPlatformThenAccessAsync();
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var bytes = await ExportAsync(source with { Version = "1.1.2-atomicity-probe.0" });
        var before = _store.GetActive(Tenant, source.Key);
        var result = await SelectedPackReplacementRoutes.ReplaceAsync(ReplacementHttp(bytes), source.Key,
            new RefusingActivation(_installer), _store, TrustingTheNodeKey(), PackRevocationList.Empty,
            new ReplacementAntiforgery(), TimeProvider.System, null, CancellationToken.None);
        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
        var wire = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("refused", wire.GetProperty("status").GetString());
        Assert.True(wire.GetProperty("draftInstall").GetProperty("installed").GetBoolean());
        var activation = wire.GetProperty("activation");
        Assert.False(activation.GetProperty("projected").GetBoolean());
        Assert.Equal("pack.view-definition.malformed", activation.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal("/contents/6/contentBase64", activation.GetProperty("refusal").GetProperty("pointer").GetString());
        Assert.Equal(before, _store.GetActive(Tenant, source.Key));
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, source.Key, "1.1.2-atomicity-probe.0")!.Lifecycle);
    }

    private sealed class RefusingActivation(IPackInstaller inner) : IPackInstaller
    {
        public PackInstallPreview Preview(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Preview(bytes, context);
        public PackInstallPreview Check(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Check(bytes, context);
        public PackInstallOutcome Install(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Install(bytes, context);
        public PackActivationOutcome Activate(PackInstallContext context, string key, string version) => new(false, key, version,
            "pack.projection.refused", Refusal: new("pack.view-definition.malformed", "/contents/6/contentBase64"));
        public PackDeactivationOutcome Deactivate(PackInstallContext context, string key, string version) => throw new NotSupportedException();
        public PackNarrowingOutcome Narrow(PackInstallContext context, string key, string contentKey,
            System.Text.Json.Nodes.JsonNode patch, AuthorizationDecision decision) => throw new NotSupportedException();
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
