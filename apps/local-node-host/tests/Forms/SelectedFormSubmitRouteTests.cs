using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Kernel.Audit;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormsRouteTests
{
    private SelectedSessionRequestPrincipal? _selected;
    private AuthorizationDeniedException? _selectedSubmissionDenial;

    [Fact]
    public async Task Selected_submit_caught_denial_uses_renderer_without_exception_or_private_decision_on_wire()
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        var operation = AuthorizationOperation.Parse("forms:author");
        var decision = await TestAuthorization.Gate(false).DecideAsync(TestAuthorization.Write(TenantA)
            .Request(operation, AuthorizationGate.RecordKindFor(operation), "private-form-denial-record"));
        _selectedSubmissionDenial = new AuthorizationDeniedException(decision);
        using var request = SelectedSubmit();
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var wire = await response.Content.ReadAsStringAsync();
        var rendered = await RequestAuthorization.RefusedAsync(
            new DefaultHttpContext { RequestServices = _app.Services }, _selectedSubmissionDenial, CancellationToken.None);
        var expected = JsonSerializer.SerializeToElement(((IValueHttpResult)rendered).Value);
        using var actual = JsonDocument.Parse(wire);
        foreach (var field in new[] { "code", "permission", "title", "detail", "remediation" })
            Assert.Equal(expected.GetProperty(field).GetRawText(), actual.RootElement.GetProperty(field).GetRawText());
        Assert.DoesNotContain(_selectedSubmissionDenial.Message, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("private-form-denial-record", wire, StringComparison.Ordinal);
        Assert.False(actual.RootElement.TryGetProperty("decision", out _));
        Assert.False(actual.RootElement.TryGetProperty("resolution", out _));
    }

    private sealed class SelectedDenialIssuer(IFormCapabilityIssuer inner, Func<AuthorizationDeniedException?> denial) : IFormCapabilityIssuer
    {
        public Task<string> IssueAsync(TenantId tenant, ActorId subject, IReadOnlyList<string> roles,
            IReadOnlyList<FormCapabilityAction> actions, DateTimeOffset expiresAt, CancellationToken ct = default) =>
            denial() is { } refused ? Task.FromException<string>(refused) : inner.IssueAsync(tenant, subject, roles, actions, expiresAt, ct);
    }

    [Fact]
    public async Task Selected_submit_uses_its_tenant_and_real_form_engine_idempotent_receipt()
    {
        _activeTeam.Active = TeamContextFor(TeamB, "Unrelated ambient team");
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("different-attribution-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        string? instance = null;
        string? auditId = null;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var request = SelectedSubmit();
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var current = body.GetProperty("instanceId").GetString();
            if (instance is null) instance = current;
            Assert.Equal(instance, current);
            var currentAudit = Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Id"));
            auditId ??= currentAudit;
            Assert.Equal(auditId, currentAudit);
            Assert.Equal(auditId, body.GetProperty("auditId").GetString());
            Assert.Equal("43300000-0000-4000-8000-000000000010", body.GetProperty("correlationId").GetString());
        }
        Assert.StartsWith("forminst:forms/", instance);
    }

    [Theory]
    [InlineData("not-a-guid", HttpStatusCode.BadRequest)]
    [InlineData("43300000-0000-4000-8000-000000000011", HttpStatusCode.Conflict)]
    public async Task Selected_submit_refuses_malformed_or_changed_replay_correlation(string correlation, HttpStatusCode expected)
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        using var first = SelectedSubmit();
        using var accepted = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var replay = SelectedSubmit();
        replay.Headers.Remove("X-Correlation-ID");
        replay.Headers.Add("X-Correlation-ID", correlation);
        using var refused = await _client.SendAsync(replay);
        Assert.Equal(expected, refused.StatusCode);
    }

    [Fact]
    public async Task Selected_submit_changed_payload_with_same_key_and_correlation_refuses_without_second_mint()
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        using var first = SelectedSubmit();
        using var accepted = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var replay = SelectedSubmit();
        replay.Content = JsonContent.Create(new { station = "changed", result = "PASS" });
        using var refused = await _client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var rows = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(
            new AuditQuery(TenantA, new AuditEventType("Forms.InstanceMinted")))) rows.Add(row);
        Assert.Single(rows);
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
        request.Headers.Add("X-Correlation-ID", "43300000-0000-4000-8000-000000000010");
        return request;
    }
}
