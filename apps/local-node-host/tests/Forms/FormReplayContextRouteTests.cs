using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormsRouteTests
{
    private readonly ReplayProjectionProbe _replayProjections = new();

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    public async Task Selected_replay_context_mismatch_never_reaches_projection_or_mutates(
        bool firstCorrelation, bool replayCorrelation, bool changeActor, bool changePayload)
    {
        SetReplayPrincipal("original");
        using var first = SelectedSubmit();
        if (!firstCorrelation) first.Headers.Remove("X-Correlation-ID");
        using var accepted = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var instance = EntityId.Parse(receipt.GetProperty("instanceId").GetString()!);
        var before = await ReplayStateAsync(instance);
        Assert.Equal(1, _replayProjections.Calls);

        if (changeActor) SetReplayPrincipal("other-authorized-person");
        using var replay = SelectedSubmit();
        if (!replayCorrelation) replay.Headers.Remove("X-Correlation-ID");
        if (changePayload) replay.Content = JsonContent.Create(new { station = "changed", result = "PASS" });
        using var refused = await _client.SendAsync(replay);
        var body = await refused.Content.ReadAsStringAsync();
        var after = await ReplayStateAsync(instance);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict && _replayProjections.Calls == 1,
            $"Expected conflict before projection; status={refused.StatusCode}, projectionCalls={_replayProjections.Calls}, body={body}");
        using var refusal = JsonDocument.Parse(body);
        Assert.Equal("forms.replay_context_mismatch", refusal.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_payload_without_correlation_replays_without_duplicate_entity_or_mint(bool selectedRoute)
    {
        if (selectedRoute) SetReplayPrincipal("original");
        using var first = ReplayRequest(selectedRoute, "same");
        using var accepted = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var firstBody = await accepted.Content.ReadAsStringAsync();
        using var receipt = JsonDocument.Parse(firstBody);
        var instance = EntityId.Parse(receipt.RootElement.GetProperty("instanceId").GetString()!);
        var before = await ReplayStateAsync(instance);
        using var replay = ReplayRequest(selectedRoute, "same");
        using var response = await _client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(firstBody, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await ReplayStateAsync(instance));
    }

    [Fact]
    public async Task Desktop_changed_payload_replay_conflicts_before_projection_without_mutation()
    {
        using var first = ReplayRequest(false, "first");
        using var accepted = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var instance = EntityId.Parse(receipt.GetProperty("instanceId").GetString()!);
        var before = await ReplayStateAsync(instance);
        using var replay = ReplayRequest(false, "changed");
        using var refused = await _client.SendAsync(replay);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict && _replayProjections.Calls == 1,
            $"Expected conflict before projection; status={refused.StatusCode}, projectionCalls={_replayProjections.Calls}");
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forms.replay_context_mismatch", body.GetProperty("code").GetString());
        Assert.Equal(before, await ReplayStateAsync(instance));
    }

    private void SetReplayPrincipal(string principal) => _selected = new SelectedSessionRequestPrincipal(
        "account-" + principal, TenantA, new(principal), new("party-" + principal),
        "membership-" + principal, 1, [new("grant-" + principal, 1)], 1, "session-" + principal, "coordination");

    private static HttpRequestMessage ReplayRequest(bool selected, string station)
    {
        var request = selected ? SelectedSubmit() : new HttpRequestMessage(HttpMethod.Post, $"{Base}/{FormId}/submit");
        request.Headers.Remove("X-Correlation-ID");
        if (!selected) request.Headers.Add("Idempotency-Key", "desktop-replay-context");
        request.Content = JsonContent.Create(new { station, result = "PASS" });
        return request;
    }

    private async Task<string> ReplayStateAsync(EntityId instance)
    {
        var entity = await _app.Services.GetRequiredService<IEntityStore>().GetAsync(instance);
        Assert.NotNull(entity);
        var mints = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(
            new AuditQuery(TenantA, new AuditEventType("Forms.InstanceMinted")))) mints.Add(row);
        var mint = Assert.Single(mints);
        return JsonSerializer.Serialize(new
        {
            entity.Id, entity.CurrentVersion, entity.CreatedAt, entity.UpdatedAt, entity.Tenant,
            body = entity.Body.RootElement, entity.Binding, mint.AuditId, mint.Actor,
            mintPayload = mint.Payload.Payload.Body,
        });
    }

    private sealed class ReplayProjectionProbe : IFormSubmitProjectionRunner
    {
        internal int Calls { get; private set; }
        public Task<IReadOnlyList<FormSubmitProjectionSkip>> RunAsync(FormSubmitContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<FormSubmitProjectionSkip>>([]);
        }
    }
}
