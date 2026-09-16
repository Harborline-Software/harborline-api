using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormsRouteTests
{
    private SelectedSessionRequestPrincipal? _selected;

    [Fact]
    public async Task Selected_submit_uses_its_tenant_and_real_form_engine_idempotent_receipt()
    {
        _activeTeam.Active = TeamContextFor(TeamB, "Unrelated ambient team");
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("different-attribution-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        string? instance = null;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var request = SelectedSubmit();
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var current = body.GetProperty("instanceId").GetString();
            if (instance is null) instance = current;
            Assert.Equal(instance, current);
        }
        Assert.StartsWith("forminst:forms/", instance);
    }

    [Theory]
    [InlineData(false, true, HttpStatusCode.Forbidden)]
    [InlineData(true, false, HttpStatusCode.Forbidden)]
    public async Task Selected_submit_requires_principal_and_antiforgery(bool principal, bool csrf, HttpStatusCode expected)
    {
        if (principal)
            _selected = new SelectedSessionRequestPrincipal("account", TenantA,
                new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
                "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        using var request = SelectedSubmit();
        if (!csrf) request.Headers.Remove("X-Harborline-Antiforgery");
        using var response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private sealed class SelectedTestSubmissionGate : IFormSubmissionGate
    {
        public string? RequiredPermission(FormDefinitionId form) => form.Value == FormId ? "members:manage" : null;
        public IReadOnlyList<string> CapabilityRoles(FormDefinitionId form) => OperatorRoles;
    }

    private sealed class SelectedTestAntiforgery : IWebAntiforgeryPolicy
    {
        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(selectedHandle == "selected-handle" && context.Request.Headers["X-Harborline-Antiforgery"] == "test-csrf");
        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) => Task.FromResult(true);
        public Task<bool> IssueAnonymousAsync(HttpContext context) => throw new NotSupportedException();
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => throw new NotSupportedException();
        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) => throw new NotSupportedException();
        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) => throw new NotSupportedException();
        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) => throw new NotSupportedException();
        public void EmitToken(HttpResponse response, string token) => throw new NotSupportedException();
        public void ExpireAnonymousBinding(HttpResponse response) => throw new NotSupportedException();
    }

    private static HttpRequestMessage SelectedSubmit()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/session/forms/{FormId}/submit")
        {
            Content = JsonContent.Create(new { station = "Station A", result = "PASS" }),
        };
        request.Headers.Add("Cookie", "__Host-hl-selected=selected-handle");
        request.Headers.Add("X-Harborline-Antiforgery", "test-csrf");
        request.Headers.Add("Idempotency-Key", "selected-form-test-v1");
        return request;
    }
}
