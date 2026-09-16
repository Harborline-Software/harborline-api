using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormsRouteTests
{
    private readonly ReplayProjectionProbe _replayProjections = new();
    private ConcurrentCreateProbe? _racingWrites;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Concurrent_first_use_adopts_only_matching_context_without_second_mint(bool changeActor, bool changeCorrelation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var probe = _racingWrites = new ConcurrentCreateProbe();
        SetReplayPrincipal("original");
        using var first = SelectedSubmit();
        using var second = SelectedSubmit();
        if (changeCorrelation)
        {
            second.Headers.Remove("X-Correlation-ID");
            second.Headers.Add("X-Correlation-ID", "43300000-0000-4000-8000-000000000011");
        }
        Task<HttpResponseMessage>? firstTask = null;
        Task<HttpResponseMessage>? secondTask = null;
        try
        {
            firstTask = _client.SendAsync(first, timeout.Token);
            await probe.Arrived[0].Task.WaitAsync(timeout.Token);
            if (changeActor) SetReplayPrincipal("other-authorized-person");
            secondTask = _client.SendAsync(second, timeout.Token);
            await probe.Arrived[1].Task.WaitAsync(timeout.Token);
            // Both engine pre-reads observed absence. Let one complete entity, Mint and projection
            // before releasing the equal-cleartext-body loser at the actual authorized write seam.
            probe.Release[0].TrySetResult();
            using var accepted = await firstTask;
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            var instance = EntityId.Parse(receipt.GetProperty("instanceId").GetString()!);
            var before = await ReplayStateAsync(instance);
            Assert.Equal(1, _replayProjections.Calls);
            probe.Release[1].TrySetResult();
            using var response = await secondTask;
            var mintCount = (await ReplayMintsAsync()).Count;
            var matching = !changeActor && !changeCorrelation;
            var expectedStatus = matching ? HttpStatusCode.Created : HttpStatusCode.Conflict;
            var expectedProjections = matching ? 2 : 1;
            Assert.True(response.StatusCode == expectedStatus && mintCount == 1 && _replayProjections.Calls == expectedProjections,
                $"Expected {expectedStatus}, one Mint and {expectedProjections} projection calls; actual status={response.StatusCode}, mints={mintCount}, projections={_replayProjections.Calls}");
            if (!matching)
            {
                var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("forms.replay_context_mismatch", refusal.GetProperty("code").GetString());
            }
            Assert.Equal(before, await ReplayStateAsync(instance));
        }
        finally
        {
            foreach (var release in probe.Release) release.TrySetResult();
            // No failed assertion leaves a paused request holding the fixture or a write boundary.
            foreach (var task in new[] { firstTask, secondTask })
                if (task is not null)
                    try { (await task).Dispose(); } catch (OperationCanceledException) { }
            _racingWrites = null;
        }
    }

    [Fact]
    public async Task Canceled_create_wait_leaves_no_entity_mint_or_projection_and_retry_can_claim_key()
    {
        var probe = _racingWrites = new ConcurrentCreateProbe();
        SetReplayPrincipal("original");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = SelectedSubmit();
        var pending = _client.SendAsync(request, cancellation.Token);
        try
        {
            await probe.Arrived[0].Task.WaitAsync(cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await probe.Exited[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(await _app.Services.GetRequiredService<IEntityStore>().GetAsync(probe.InstanceIds[0]));
            Assert.Empty(await ReplayMintsAsync());
            Assert.Equal(0, _replayProjections.Calls);
        }
        finally
        {
            foreach (var release in probe.Release) release.TrySetResult();
            _racingWrites = null;
        }
        using var retry = SelectedSubmit();
        using var accepted = await _client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Single(await ReplayMintsAsync());
        Assert.Equal(1, _replayProjections.Calls);
    }

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
        var mint = Assert.Single(await ReplayMintsAsync());
        return JsonSerializer.Serialize(new
        {
            entity.Id, entity.CurrentVersion, entity.CreatedAt, entity.UpdatedAt, entity.Tenant,
            body = entity.Body.RootElement, entity.Binding, mint.AuditId, mint.Actor,
            mintPayload = mint.Payload.Payload.Body,
        });
    }

    private async Task<IReadOnlyList<AuditRecord>> ReplayMintsAsync()
    {
        var mints = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(
            new AuditQuery(TenantA, new AuditEventType("Forms.InstanceMinted")))) mints.Add(row);
        return mints;
    }

    private sealed class ConcurrentCreateProbe
    {
        internal readonly TaskCompletionSource[] Arrived =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        internal readonly TaskCompletionSource[] Release =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        internal readonly TaskCompletionSource[] Exited =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        internal readonly EntityId[] InstanceIds = new EntityId[2];
        internal int Entrants;
    }

    private sealed class PausedFormEntityWriter(IAuthorizedFormEntityWriter inner, Func<ConcurrentCreateProbe?> control) : IAuthorizedFormEntityWriter
    {
        public async Task<EntityId> CreateAsync(FormDefinitionId form, SchemaId schema, JsonDocument body,
            CreateOptions options, AuthorizationDecision decision, CancellationToken ct = default)
        {
            if (control() is not { } probe)
                return await inner.CreateAsync(form, schema, body, options, decision, ct);
            var slot = Interlocked.Increment(ref probe.Entrants) - 1;
            try
            {
                probe.InstanceIds[slot] = new EntityId(options.Scheme, options.Authority,
                    options.ExplicitLocalPart ?? throw new InvalidOperationException("This probe requires the engine's derived instance ID."));
                probe.Arrived[slot].TrySetResult();
                await probe.Release[slot].Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                return await inner.CreateAsync(form, schema, body, options, decision, ct);
            }
            finally { probe.Exited[slot].TrySetResult(); }
        }
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
