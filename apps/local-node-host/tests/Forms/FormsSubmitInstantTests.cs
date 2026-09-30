using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// T-540 (ck-7): a Forms submit is ONE act with ONE instant. The route reads the host clock once, and the
/// token mint, the pre-save validate, the rule gate and the stored stamp all use that instant. A submit
/// that straddles midnight therefore cannot pass the pre-save validate on one day and be judged (or
/// stamped) on the next.
/// </summary>
public sealed class FormsSubmitInstantTests : IAsyncLifetime
{
    private static readonly TeamId Team = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000c540"));
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(Team);
    private static readonly DateTimeOffset BeforeMidnight = new(2026, 8, 30, 23, 59, 59, TimeSpan.Zero);
    private const string FormId = "ck7.today.v1";

    private readonly MidnightClock _clock = new(BeforeMidnight);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddFrozenKernelClock(_clock);
        builder.Services.AddHttpContextAccessor();
        _app = builder.Build();
        _clock.Requests = _app.Services.GetRequiredService<IHttpContextAccessor>();

        var schema = await _app.Services.GetRequiredService<ISchemaRegistry>().RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "additionalProperties": false
            }
            """);
        var store = _app.Services.GetRequiredService<IFormDefinitionStore>();
        var definition = new FormDefinition(
            Id: new FormDefinitionId(FormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: Tenant,
            Owner: IdentityRef.System,
            SchemaRef: schema.Id,
            Overlay: HarborlineOverlay.Empty with
            {
                Rules = new[]
                {
                    new RuleDefinition(
                        Envelope: new DefinitionEnvelope<string, string, TenantId, string?>(
                            "only.on.the.admitted.day", "1.0.0", Tenant, CascadeLayer.Tenant,
                            Provenance: null, Array.Empty<DefinitionRequirement>()),
                        Tier: RuleTier.JsonLogic,
                        Scope: RuleScope.Schema,
                        ScopeTarget: string.Empty,
                        Expression: """{"==":[{"date.today":[]},"2026-08-30"]}""",
                        Action: RuleActionKind.Validate),
                },
            },
            Lineage: null,
            CreatedAt: BeforeMidnight,
            UpdatedAt: BeforeMidnight);
        await store.RegisterAsync(definition);
        await store.PublishAsync(new DefinitionCoordinates(Tenant, FormId, "1.0.0"));

        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            new FixedActiveTeamAccessor(new TeamContext(Team, "Team ck-7", new ServiceCollection().BuildServiceProvider(), _clock)),
            new[] { FormsRoutes.NodeOperatorRole },
            _clock);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // The clock crosses midnight after N reads within the act. N = 1: every read after admission is the next
    // day. N = 3: before T-540's route slice the route read the clock for the token (1), the pre-save
    // validate's expiry check (2) and its rule gate (3), then again for the authority (4) — so the pre-save
    // validate PASSED on the 30th and the save's gate REFUSED on the 31st: two verdicts for one act.
    [Theory(DisplayName = "T-540: a submit straddling midnight is validated, judged and stamped at the one instant the route admitted")]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Submit_straddling_midnight_decides_and_stamps_at_the_admitted_instant(int readsBeforeMidnight)
    {
        _clock.Arm(readsBeforeMidnight);

        using var response = await _client.PostAsJsonAsync($"{FormsRoutes.RouteBase}/{FormId}/submit", new { name = "late" });

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode} after {_clock.Reads} host-clock reads: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entity = await _app.Services.GetRequiredService<IEntityStore>()
            .GetAsync(EntityId.Parse(body.GetProperty("instanceId").GetString()!));
        Assert.Equal(BeforeMidnight, entity!.Binding!.SubmittedAt);
        Assert.Equal(1, _clock.Reads);
    }

    private sealed class MidnightClock(DateTimeOffset beforeMidnight) : TimeProvider
    {
        private bool _armed;
        private int _readsBeforeMidnight;
        public int Reads { get; private set; }

        // Only the submit request's own reads count: the host's background services also read the kernel clock, and
        // counting their reads shifted which read fell before midnight (T-987).
        public IHttpContextAccessor? Requests { get; set; }

        public void Arm(int readsBeforeMidnight)
        {
            _armed = true;
            _readsBeforeMidnight = readsBeforeMidnight;
            Reads = 0;
        }

        public override DateTimeOffset GetUtcNow()
        {
            if (!_armed || Requests?.HttpContext is null) return beforeMidnight;
            Reads++;
            return Reads <= _readsBeforeMidnight ? beforeMidnight : beforeMidnight.AddSeconds(2);
        }
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
