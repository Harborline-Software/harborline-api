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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Dated_submission_replay_keeps_its_original_admission_after_the_end(bool selected, bool explicitFutureStart)
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        var firstAt = _submissionClock.GetUtcNow();
        var candidate = new Dictionary<string, string>
        {
            ["station"] = "Marina", ["result"] = "PASS", ["effectiveTo"] = firstAt.AddMinutes(2).ToString("O"),
        };
        if (explicitFutureStart) candidate["inspector"] = firstAt.AddMinutes(1).ToString("O");
        HttpRequestMessage Submit(string key = "dated-replay")
        {
            var request = SelectedSubmit();
            if (!selected)
            {
                request.RequestUri = new Uri($"{Base}/{FormId}/submit", UriKind.Relative);
                request.Headers.Add("X-Test-Desktop", "1");
            }
            request.Headers.Remove("Idempotency-Key");
            request.Headers.Add("Idempotency-Key", key);
            request.Content = JsonContent.Create(candidate);
            return request;
        }
        using var initialRequest = Submit();
        using var initial = await _client.SendAsync(initialRequest);
        Assert.True(initial.StatusCode == HttpStatusCode.Created, await initial.Content.ReadAsStringAsync());
        var original = (await initial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString();
        var count = await CountTenantEntitiesAsync();
        _submissionClock.Offset = TimeSpan.FromMinutes(5);

        using var retryRequest = Submit();
        using var retry = await _client.SendAsync(retryRequest);
        Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
        Assert.Equal(original, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString());
        Assert.Equal(count, await CountTenantEntitiesAsync());

        // The same candidate with a fresh key is a new admission and must still fail before minting.
        using var freshRequest = Submit("dated-new-key");
        using var fresh = await _client.SendAsync(freshRequest);
        Assert.Equal(explicitFutureStart ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, fresh.StatusCode);
        Assert.Equal(explicitFutureStart ? "kernel.backdate-capability-required" : "access.grant.invalid-validity-interval",
            (await fresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(count, await CountTenantEntitiesAsync());

        candidate["station"] = "Changed";
        using var changedRequest = Submit();
        using var changed = await _client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("forms.replay_context_mismatch", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(count, await CountTenantEntitiesAsync());
        if (selected)
        {
            candidate["station"] = "Marina";
            using var correlationRequest = Submit();
            correlationRequest.Headers.Remove("X-Correlation-ID");
            correlationRequest.Headers.Add("X-Correlation-ID", "43300000-0000-4000-8000-000000000011");
            using var correlation = await _client.SendAsync(correlationRequest);
            Assert.Equal(HttpStatusCode.Conflict, correlation.StatusCode);
            Assert.Equal("forms.replay_context_mismatch", (await correlation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            _selected = new SelectedSessionRequestPrincipal("other-account", TenantA,
                new PrincipalUserId("other-holder"), new CanonicalPartyReference("other-party"),
                "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
            using var actorRequest = Submit();
            using var actor = await _client.SendAsync(actorRequest);
            Assert.Equal(HttpStatusCode.Conflict, actor.StatusCode);
            Assert.Equal("forms.replay_context_mismatch", (await actor.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(count, await CountTenantEntitiesAsync());
        }
    }

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

    [Fact(DisplayName = "T-909 ck-9 K3: the selected-session submit refuses a past effective-from by name and commits nothing")]
    [Trait("Holds", "kernel-core-ck-9")]
    public async Task Selected_submit_refuses_a_past_effective_from_by_name_and_commits_nothing()
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        var skewed = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        using var request = SelectedSubmit();
        request.Content = JsonContent.Create(new Dictionary<string, string>
        {
            ["station"] = "Marina",
            ["result"] = "PASS",
            ["inspector"] = skewed,
            ["captured_at"] = skewed,
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("kernel.backdate-capability-required", body.GetProperty("code").GetString());
        var minted = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(
            new AuditQuery(TenantA, new AuditEventType("Forms.InstanceMinted")))) minted.Add(row);
        Assert.Empty(minted);
    }

    [Fact(DisplayName = "T-909 ck-9 K3: the selected-session submit ignores a skewed captured_at and stamps the server clock")]
    [Trait("Holds", "kernel-core-ck-9")]
    public async Task Selected_submit_ignores_a_skewed_captured_at_and_stamps_the_server_clock()
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        using var request = SelectedSubmit();
        request.Content = JsonContent.Create(new Dictionary<string, string>
        {
            ["station"] = "Marina",
            ["result"] = "PASS",
            ["captured_at"] = DateTimeOffset.UtcNow.AddDays(-30).ToString("O"),
        });
        var before = DateTimeOffset.UtcNow;

        using var response = await _client.SendAsync(request);

        var after = DateTimeOffset.UtcNow;
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var instance = EntityId.Parse((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString()!);
        var entity = await _app.Services.GetRequiredService<Harborline.Api.Foundation.Assets.Entities.IEntityStore>().GetAsync(instance);
        var stamped = Assert.IsType<Harborline.Api.Foundation.Assets.Entities.EntityBinding>(entity?.Binding).SubmittedAt;
        Assert.InRange(stamped, before, after);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Selected_submit_refuses_an_end_not_after_its_start_before_minting(int endOffsetMinutes)
    {
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        var start = DateTimeOffset.UtcNow.AddDays(1);
        using var request = SelectedSubmit();
        request.Content = JsonContent.Create(new Dictionary<string, string>
        {
            ["station"] = "Marina", ["result"] = "PASS",
            ["inspector"] = start.ToString("O"),
            ["effectiveTo"] = start.AddMinutes(endOffsetMinutes).ToString("O"),
        });
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("access.grant.invalid-validity-interval", body.GetProperty("code").GetString());
        var minted = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(
            new AuditQuery(TenantA, new AuditEventType("Forms.InstanceMinted")))) minted.Add(row);
        Assert.Empty(minted);
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
        public string? RequiredPermission(FormDefinitionId form) =>
            form.Value == FormId || form.Value == OpenFormId ? "members:manage" : null;
        // K3: the test form's optional text field stands in for a dated form's effective-from.
        public string? EffectiveFromField(FormDefinitionId form) => form.Value == FormId ? "inspector" : null;
        public string? EffectiveToField(FormDefinitionId form) => form.Value == FormId ? "effectiveTo" : null;
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
