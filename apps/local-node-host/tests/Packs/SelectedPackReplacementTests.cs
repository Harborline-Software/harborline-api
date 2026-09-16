using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;
using Harborline.Api.Conformance;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Kernel.Audit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Fact]
    public async Task Selected_replacement_caught_denial_uses_renderer_without_exception_or_private_decision_on_wire()
    {
        await PreloadPlatformThenAccessAsync();
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var bytes = await ExportAsync(source with { Version = "1.1.2" });
        var decision = await TestAuthorization.Gate(false).DecideAsync(TestAuthorization.Write(Tenant)
            .Request(AuthorizationOperation.Parse("records:write"), "record", "private-pack-denial-record"));
        var denied = new AuthorizationDeniedException(decision);
        var http = ReplacementHttp(bytes);
        var before = JsonSerializer.Serialize(_store.ListInstalled(Tenant));
        var result = await SelectedPackReplacementRoutes.ReplaceAsync(http, source.Key,
            new DenyingInstall(_installer, denied), _store, TrustingTheNodeKey(), PackRevocationList.Empty,
            new ReplacementAntiforgery(), TimeProvider.System, null, CancellationToken.None);
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        var wire = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var rendered = await RequestAuthorization.RefusedAsync(http, denied, CancellationToken.None);
        var expected = JsonSerializer.SerializeToElement(((IValueHttpResult)rendered).Value);
        var refusal = wire.GetProperty("activation").GetProperty("refusal");
        foreach (var field in new[] { "code", "permission", "title", "detail", "remediation" })
            Assert.Equal(expected.GetProperty(field).GetRawText(), refusal.GetProperty(field).GetRawText());
        Assert.DoesNotContain(denied.Message, wire.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-pack-denial-record", wire.GetRawText(), StringComparison.Ordinal);
        Assert.False(refusal.TryGetProperty("decision", out _));
        Assert.False(refusal.TryGetProperty("resolution", out _));
        Assert.False(wire.GetProperty("draftInstall").GetProperty("installed").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(Tenant)));
    }

    private sealed class DenyingInstall(IPackInstaller inner, AuthorizationDeniedException denied) : IPackInstaller
    {
        public PackInstallPreview Preview(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Preview(bytes, context);
        public PackInstallPreview Check(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Check(bytes, context);
        public PackInstallOutcome Install(ReadOnlySpan<byte> bytes, PackInstallContext context) => throw denied;
        public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string key, string version, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Activation must not run after denial.");
        public PackDeactivationOutcome Deactivate(PackInstallContext context, string key, string version) => throw new NotSupportedException();
        public PackNarrowingOutcome Narrow(PackInstallContext context, string key, string contentKey,
            System.Text.Json.Nodes.JsonNode patch, AuthorizationDecision decision) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Selected_replacement_real_signed_probe_preserves_active_runtime_and_returns_native_pointer()
    {
        await PreloadPlatformThenAccessAsync();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx"))) directory = directory.Parent;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory!.FullName, AccessReplacementFixture.DirectoryPath,
            AccessReplacementFixture.ProbeArtifactName));
        var signed = new PackFileCodec().TryDecode(bytes)!;
        var trust = new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, signed.Envelope!.IssuerId, 1, TrustRootStatus.Current)]);
        var before = await PublishedSnapshotAsync();
        var http = ReplacementHttp(bytes);
        var correlation = Guid.Parse("43300000-0000-4000-8000-000000000109");
        http.Request.Headers["X-Correlation-ID"] = correlation.ToString("D");
        var trail = new InMemoryAuditTrail();
        var audit = new AuthorizedActAudit(trail, _signer.Signer, NullLogger<AuthorizedActAudit>.Instance);
        var result = await SelectedPackReplacementRoutes.ReplaceAsync(http, signed.Envelope.Payload.Manifest.Key,
            _installer, _store, trust, PackRevocationList.Empty, new ReplacementAntiforgery(), TimeProvider.System, audit, CancellationToken.None);
        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
        var wire = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("refused", wire.GetProperty("status").GetString());
        Assert.True(wire.GetProperty("draftInstall").GetProperty("installed").GetBoolean());
        var activation = wire.GetProperty("activation");
        Assert.False(activation.GetProperty("activated").GetBoolean());
        Assert.False(activation.GetProperty("projected").GetBoolean());
        Assert.Equal("pack.view-definition.malformed", activation.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal("/contents/6/contentBase64", activation.GetProperty("refusal").GetProperty("pointer").GetString());
        Assert.Equal(before, await PublishedSnapshotAsync());
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, signed.Envelope.Payload.Manifest.Key, AccessReplacementFixture.ProbeVersion)!.Lifecycle);
        var rows = new List<AuditRecord>();
        await foreach (var row in trail.QueryAsync(new AuditQuery(Tenant))) rows.Add(row);
        var receipt = Assert.Single(rows);
        Assert.Equal("PackReplacementAttempt", receipt.EventType.Value);
        Assert.Equal(receipt.AuditId, wire.GetProperty("auditId").GetGuid());
        Assert.Equal(correlation, wire.GetProperty("correlationId").GetGuid());
        Assert.Equal(correlation.ToString("D"), receipt.Payload.Payload.Body["correlation_id"]);
    }

    [Fact]
    public async Task Selected_replacement_postcommit_diagnostic_preserves_success_and_carried_correlation()
    {
        await PreloadPlatformThenAccessAsync();
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var http = ReplacementHttp(await ExportAsync(source with { Version = "1.1.2" }));
        var correlation = Guid.Parse("43300000-0000-4000-8000-000000000110");
        http.Request.Headers["X-Correlation-ID"] = correlation.ToString("D");
        var installer = new DiagnosticActivation(_installer);
        var result = await SelectedPackReplacementRoutes.ReplaceAsync(http, source.Key, installer, _store,
            TrustingTheNodeKey(), PackRevocationList.Empty, new ReplacementAntiforgery(), TimeProvider.System, null, CancellationToken.None);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var wire = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("replaced", wire.GetProperty("status").GetString());
        var activation = wire.GetProperty("activation");
        Assert.True(activation.GetProperty("activated").GetBoolean());
        Assert.True(activation.GetProperty("projected").GetBoolean());
        Assert.Equal("Activation committed; observer diagnostic", activation.GetProperty("detail").GetString());
        Assert.Equal(correlation, installer.Installed!.Decision!.Request.CorrelationId);
        var decision = installer.Activated!.Decision!;
        Assert.Equal(correlation, decision.Request.CorrelationId);
        var trail = new InMemoryAuditTrail();
        var adapter = new KernelAuditPackInstallAudit(trail, _signer, NullLogger<KernelAuditPackInstallAudit>.Instance);
        adapter.AppendAuthorized(new PackInstallAuditEntry(Tenant, PackInstallAuditAction.Activated, source.Key,
            "1.1.2", decision.Request.At, null, null, "pack.install.activated", ActingPrincipal: decision.Request.Principal.Value), decision);
        var rows = new List<AuditRecord>();
        await foreach (var row in trail.QueryAsync(new AuditQuery(Tenant))) rows.Add(row);
        Assert.Equal(correlation.ToString("D"), Assert.Single(rows).Payload.Payload.Body["correlation_id"]);
    }

    private sealed class DiagnosticActivation(IPackInstaller inner) : IPackInstaller
    {
        public PackInstallOutcome? Installed { get; private set; }
        public PackActivationOutcome? Activated { get; private set; }
        public PackInstallPreview Preview(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Preview(bytes, context);
        public PackInstallPreview Check(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Check(bytes, context);
        public PackInstallOutcome Install(ReadOnlySpan<byte> bytes, PackInstallContext context) => Installed = inner.Install(bytes, context);
        public async Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string key, string version, CancellationToken cancellationToken = default)
        {
            Activated = await inner.ActivateAsync(context, key, version, cancellationToken).ConfigureAwait(false);
            return Activated with { Detail = "Activation committed; observer diagnostic" };
        }
        public PackDeactivationOutcome Deactivate(PackInstallContext context, string key, string version) => inner.Deactivate(context, key, version);
        public PackNarrowingOutcome Narrow(PackInstallContext context, string key, string contentKey,
            System.Text.Json.Nodes.JsonNode patch, AuthorizationDecision decision) => inner.Narrow(context, key, contentKey, patch, decision);
    }

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
        public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string key, string version, CancellationToken cancellationToken = default) => Task.FromResult(new PackActivationOutcome(false, key, version,
            "pack.projection.refused", Refusal: new("pack.view-definition.malformed", "/contents/6/contentBase64")));
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
