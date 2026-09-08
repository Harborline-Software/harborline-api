using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

using EntityId = Harborline.Api.Foundation.Assets.Common.EntityId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// ADR 0101 Rev 3.1 Wave 2b — the two pre-customer recovery gates the deep review named, proven on a
/// real in-process host with the production composition.
/// <list type="bullet">
///   <item><b>F-RECON</b> — the <see cref="FormSubmitProjectionReconcilerDaemon"/> DRAINS the projection
///     outbox on the running node: a pre-seeded Pending (crash-interrupted) or Failed (threw) row heals
///     with NO explicit reconciler call — the scheduled sweep does it.</item>
///   <item><b>F-ROUTE</b> — a committed submission whose post-submit projection faults returns
///     success-with-pending (202), NOT a retry-inviting 500; and a submit is idempotent by the
///     <c>Idempotency-Key</c> header, so a retry (and a same-key concurrent double-fire) yields ONE
///     instance and ONE assessment — never a double-capture.</item>
/// </list>
/// Waits are deterministic polls with a timeout — never sleep-and-hope.
/// </summary>
public sealed class ConditionCaptureRecoveryRouteTests
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000a5502"));
    private static readonly ActorId Operator = new("local");

    private const string AssetBase = "/api/local-node/asset-registry";
    private const string FormsBase = "/api/local-node/forms";
    private const string ConditionForm = "condition.capture.v1";

    private static readonly IReadOnlyList<string> OperatorRoles = new[] { FormsRoutes.NodeOperatorRole };

    /// <summary>A live host wired with the production forms + asset-registry composition (incl. the F-RECON daemon).</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required TenantId Tenant { get; init; }
        public required IFormSubmitOutbox Outbox { get; init; }
        public required IConditionAssessmentStore Conditions { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(TimeSpan sweepInterval, FaultInjectingConditionStore? fault = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();

        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry(sweepInterval);

        // Optional fault injection: override IConditionAssessmentStore with a decorator that can fault the
        // projector's write (models a post-commit projection fault) while still serving reads from a real
        // inner store. LAST registration wins, so both the projector and the condition route use it.
        if (fault is not null)
        {
            builder.Services.AddSingleton<IConditionAssessmentStore>(sp =>
            {
                fault.SetInner(ActivatorUtilities.CreateInstance<InMemoryConditionAssessmentStore>(sp));
                return fault;
            });
        }

        var app = builder.Build();

        var tenant = ActiveTeamTenantContext.ProjectTenantId(TeamA);
        await RegisterConditionFormAsync(app, tenant);

        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA));

        app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        AssetRegistryRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            app.Services.GetRequiredService<IEntityTypeRegistry>(),
            app.Services.GetRequiredService<IRegistryEntityRepository>(),
            app.Services.GetRequiredService<ITypedRelationshipStore>(),
            app.Services.GetRequiredService<IConditionAssessmentStore>(),
            app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            activeTeam,
            TimeProvider.System);
        FormsRoutes.Map(
            app,
            app.Services.GetRequiredService<IFormEngine>(),
            app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            activeTeam,
            OperatorRoles,
            TimeProvider.System);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Harness
        {
            App = app,
            Client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) },
            Tenant = tenant,
            Outbox = app.Services.GetRequiredService<IFormSubmitOutbox>(),
            Conditions = app.Services.GetRequiredService<IConditionAssessmentStore>(),
        };
    }

    private static async Task RegisterConditionFormAsync(WebApplication app, TenantId tenant)
    {
        var registry = app.Services.GetRequiredService<ISchemaRegistry>();
        var store = app.Services.GetRequiredService<IFormDefinitionStore>();

        var schema = await registry.RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "condition": { "type": "integer", "minimum": 1, "maximum": 5 },
                "asset_ref": { "type": "string" }
              },
              "required": ["condition"],
              "additionalProperties": false
            }
            """);

        var def = new FormDefinition(
            Id: new FormDefinitionId(ConditionForm),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schema.Id,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["condition"] = new(InternationalizedText.FromInvariant("Condition"), ControlHint: "condition-rating"),
                    ["asset_ref"] = new(InternationalizedText.FromInvariant("Asset"), ControlHint: "text"),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "main",
                        Title: InternationalizedText.FromInvariant("Condition"),
                        Fields: new[] { "condition", "asset_ref" },
                        Access: new SectionAccess(
                            ReadRoles: new[] { FormsRoutes.NodeOperatorRole },
                            WriteRoles: new[] { FormsRoutes.NodeOperatorRole })),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: InternationalizedText.FromInvariant("Condition Capture")),
            Lineage: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        await store.RegisterAsync(def);
        await store.PublishAsync(new DefinitionCoordinates(tenant, def.Id.Value, def.Version.ToString()));

        app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(
            new EntityTypeId("water-heater"),
            new EntityTypeDescriptor("Water Heater", EntityTrait.Maintainable | EntityTrait.Movable),
            CascadeLayer.Pack));
        await app.Services.GetRequiredService<IConditionRatingFieldBindingStore>().RegisterAsync(
            tenant, new ConditionRatingFieldBinding(
                new FormDefinitionId(ConditionForm), "/condition",
                ConditionEntityRefSource.SubmissionField, "asset_ref", ScaleMax: 5));
    }

    private static async Task<string> CreateEntityAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync($"{AssetBase}/entities", new { type = "water-heater", displayName = "Heater" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private FormSubmitContext ConditionSubmit(TenantId tenant, string assetRef, int grade = 4)
        => new(
            Form: new FormDefinitionId(ConditionForm),
            InstanceId: new EntityId("forminst", "forms", Guid.NewGuid().ToString("N")),
            Tenant: tenant,
            Actor: Operator,
            SubmittedAt: DateTimeOffset.UtcNow,
            SubmittedValues: JsonDocument.Parse($$"""{ "condition": {{grade}}, "asset_ref": "{{assetRef}}" }"""));

    // ── F-RECON — the scheduled sweep drains the outbox on the running host ───────────────────────────

    [Fact(DisplayName = "F-RECON: a pre-seeded Pending outbox row heals on the running host (no explicit reconcile)")]
    public async Task Pending_outbox_row_heals_on_the_running_host()
    {
        await using var h = await StartAsync(TimeSpan.FromMilliseconds(150));

        var heater = await CreateEntityAsync(h.Client);

        // Model a hard process death AFTER the submission committed but BEFORE its projection ran: only the
        // durable Pending outbox row exists; nothing was captured. No explicit reconcile is ever called.
        await h.Outbox.EnqueueAsync(ConditionSubmit(h.Tenant, heater));
        Assert.Single(await h.Outbox.ListUnresolvedAsync()); // visibly unresolved

        // The scheduled sweep re-runs the projection AND drains the outbox row. Wait on the TERMINAL heal
        // state — the outbox draining — rather than on the projection landing: the reconciler marks a row
        // completed only AFTER its projection write commits, so an empty outbox implies the assessment
        // already landed. (Previously this polled the projection landing then asserted the drain non-poll,
        // which raced the mark-completed step under CI load — bug-20260702-d41cf79b.)
        await PollUntilOutboxDrainedAsync(h.Outbox);

        var after = await h.Client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/condition");
        var record = Assert.Single(after.GetProperty("history").EnumerateArray());
        Assert.Equal(4, record.GetProperty("grade").GetInt32());
        Assert.Empty(await h.Outbox.ListUnresolvedAsync()); // the row drained (the poll already proved it)
    }

    [Fact(DisplayName = "F-RECON: a Failed outbox row heals on the running host once the fault clears")]
    public async Task Failed_outbox_row_heals_on_the_running_host()
    {
        await using var h = await StartAsync(TimeSpan.FromMilliseconds(150));

        var heater = await CreateEntityAsync(h.Client);

        // Model a projection that THREW (a store fault): the row is enqueued then marked Failed. The fault
        // has since cleared (the target exists, the store is healthy), so the sweep re-runs it successfully.
        var context = ConditionSubmit(h.Tenant, heater);
        var entry = await h.Outbox.EnqueueAsync(context);
        await h.Outbox.MarkFailedAsync(entry.Id, "condition store unavailable");
        var failed = Assert.Single(await h.Outbox.ListUnresolvedAsync());
        Assert.Equal(FormSubmitOutboxState.Failed, failed.State);

        // Same terminal-state wait as the Pending case: the drain is the last step of the heal, so an
        // empty outbox implies the recovered projection already landed (bug-20260702-d41cf79b).
        await PollUntilOutboxDrainedAsync(h.Outbox);

        var after = await h.Client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/condition");
        Assert.Single(after.GetProperty("history").EnumerateArray()); // the recovered projection landed
        Assert.Empty(await h.Outbox.ListUnresolvedAsync());
    }

    // ── F-ROUTE — committed-but-pending returns 202, and submit is idempotent by key ─────────────────

    [Fact(DisplayName = "F-ROUTE: a post-commit projection fault returns 202 success-with-pending, not 500")]
    public async Task A_post_commit_projection_fault_returns_success_with_pending_not_500()
    {
        var fault = new FaultInjectingConditionStore { Faulting = true };
        // A LONG sweep interval so the daemon's periodic sweep never heals the Failed row mid-assertion.
        await using var h = await StartAsync(TimeSpan.FromHours(1), fault);

        var heater = await CreateEntityAsync(h.Client);

        // The submission commits; the condition projector's write then faults (post-commit).
        var submit = await h.Client.PostAsJsonAsync($"{FormsBase}/{ConditionForm}/submit",
            new { condition = 4, asset_ref = heater });

        // NOT a 500 for a saved submission — a 202 success-with-pending carrying the committed instance.
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("forminst:", body.GetProperty("instanceId").GetString());
        Assert.Equal("pending", body.GetProperty("projection").GetString());

        // The projection did NOT land (it faulted) but the durable outbox holds a Failed row to reconcile —
        // the audited-failure floor, diagnosable and recoverable, rather than a silent loss or a 500.
        var condition = await h.Client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/condition");
        Assert.Empty(condition.GetProperty("history").EnumerateArray());
        var row = Assert.Single(await h.Outbox.ListUnresolvedAsync());
        Assert.Equal(FormSubmitOutboxState.Failed, row.State);
    }

    [Fact(DisplayName = "F-ROUTE: a retry with the same Idempotency-Key yields ONE instance and ONE assessment")]
    public async Task A_retry_with_the_same_idempotency_key_yields_one_instance_and_one_assessment()
    {
        await using var h = await StartAsync(TimeSpan.FromHours(1));
        var heater = await CreateEntityAsync(h.Client);

        var first = await SubmitWithKeyAsync(h.Client, heater, key: "inspection-2026-07-02-heater-1");
        Assert.Equal(HttpStatusCode.Created, first.status);

        // The client retries the SAME submission with the SAME key (e.g. after a dropped response).
        var retry = await SubmitWithKeyAsync(h.Client, heater, key: "inspection-2026-07-02-heater-1");
        Assert.Equal(HttpStatusCode.Created, retry.status);

        // ONE instance: the retry resolved to the SAME instance id (deterministic from tenant+form+key) …
        Assert.Equal(first.instanceId, retry.instanceId);
        // … and therefore ONE assessment (the derived side-record id upserts, never duplicates).
        var condition = await h.Client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/condition");
        Assert.Single(condition.GetProperty("history").EnumerateArray());
    }

    [Fact(DisplayName = "F-ROUTE: without an Idempotency-Key each submit mints a fresh instance (no key ⇒ prior behaviour)")]
    public async Task Without_an_idempotency_key_each_submit_is_a_distinct_instance()
    {
        await using var h = await StartAsync(TimeSpan.FromHours(1));
        var heater = await CreateEntityAsync(h.Client);

        var a = await SubmitWithKeyAsync(h.Client, heater, key: null);
        var b = await SubmitWithKeyAsync(h.Client, heater, key: null);

        Assert.NotEqual(a.instanceId, b.instanceId); // two distinct submissions ⇒ two instances
        // Two submissions against the same entity ⇒ two assessments at (distinct) instance-derived ids.
        var condition = await h.Client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/condition");
        Assert.Equal(2, condition.GetProperty("history").GetArrayLength());
    }

    [Fact(DisplayName = "F-ROUTE: a same-key concurrent double-fire yields ONE instance")]
    public async Task A_same_key_concurrent_double_fire_yields_one_instance()
    {
        await using var h = await StartAsync(TimeSpan.FromHours(1));
        var heater = await CreateEntityAsync(h.Client);

        const string key = "concurrent-fire-1";
        var results = await Task.WhenAll(
            SubmitWithKeyAsync(h.Client, heater, key),
            SubmitWithKeyAsync(h.Client, heater, key));

        // Both requests resolve to the SAME instance — the deterministic id + the store's create guard mean
        // a concurrent double-fire can never fork into two instances.
        Assert.Equal(results[0].instanceId, results[1].instanceId);
        var condition = await h.Client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/condition");
        Assert.Single(condition.GetProperty("history").EnumerateArray());
    }

    private static async Task<(HttpStatusCode status, string? instanceId)> SubmitWithKeyAsync(
        HttpClient client, string assetRef, string? key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{FormsBase}/{ConditionForm}/submit")
        {
            Content = JsonContent.Create(new { condition = 4, asset_ref = assetRef }),
        };
        if (key is not null)
        {
            req.Headers.Add(FormsRoutes.IdempotencyKeyHeader, key);
        }
        var resp = await client.SendAsync(req);
        var instanceId = resp.Content.Headers.ContentLength > 0
            ? (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString()
            : null;
        return (resp.StatusCode, instanceId);
    }

    /// <summary>
    /// Waits until the projection outbox has fully DRAINED — the terminal state of an F-RECON heal. The
    /// reconciler marks a row completed only AFTER its projection write commits (see
    /// <c>FormSubmitProjectionReconciler.ReconcileAsync</c>), so an empty outbox implies the recovered
    /// assessment already landed and is visible to the condition route. Keying the wait on the drain
    /// (instead of polling the projection landing then asserting the drain non-poll) removes the
    /// mark-completed-vs-projection-write race that flaked under CI load (bug-20260702-d41cf79b). On
    /// timeout it dumps the still-unresolved rows (id + state + attempts + last error) for diagnosis.
    /// </summary>
    private static async Task PollUntilOutboxDrainedAsync(
        IFormSubmitOutbox outbox, int timeoutMs = 10_000, int pollMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var unresolved = await outbox.ListUnresolvedAsync().ConfigureAwait(false);
            if (unresolved.Count == 0)
            {
                return;
            }
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                var dump = string.Join(
                    "; ",
                    unresolved.Select(e =>
                        $"{e.Id} state={e.State} attempts={e.Attempts} lastError={e.LastError ?? "<none>"}"));
                Assert.Fail(
                    $"projection outbox did not drain within {timeoutMs}ms — " +
                    $"{unresolved.Count} row(s) still unresolved: [{dump}]");
            }
            await Task.Delay(pollMs).ConfigureAwait(false);
        }
    }

    private static TeamContext TeamContextFor(TeamId teamId)
        => new(teamId, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    /// <summary>
    /// An <see cref="IConditionAssessmentStore"/> that can fault the projector's WRITE (models a
    /// post-commit projection store fault) while delegating everything else to a real inner store, so
    /// reads still work and a recovered write lands where the condition route can read it.
    /// </summary>
    private sealed class FaultInjectingConditionStore : IConditionAssessmentStore
    {
        private IConditionAssessmentStore _inner = null!;
        public bool Faulting { get; set; }
        public void SetInner(IConditionAssessmentStore inner) => _inner = inner;

        public Task RecordAsync(ConditionAssessment assessment, string? actorRef = null, CancellationToken ct = default)
        {
            if (Faulting)
            {
                throw new InvalidOperationException("condition store unavailable (injected fault)");
            }
            return _inner.RecordAsync(assessment, actorRef, ct);
        }

        public Task<ConditionAssessment?> GetByIdAsync(TenantId tenant, ConditionAssessmentId id, CancellationToken ct = default)
            => _inner.GetByIdAsync(tenant, id, ct);

        public Task<IReadOnlyList<ConditionAssessment>> GetHistoryAsync(
            TenantId tenant, RegistryEntityId entity, Instant? asOf = null, CancellationToken ct = default)
            => _inner.GetHistoryAsync(tenant, entity, asOf, ct);

        public Task<ConditionAssessment?> GetLatestAsAtAsync(
            TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken ct = default)
            => _inner.GetLatestAsAtAsync(tenant, entity, asOf, ct);
    }
}
