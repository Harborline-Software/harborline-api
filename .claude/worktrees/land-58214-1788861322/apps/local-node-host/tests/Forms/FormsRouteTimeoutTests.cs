using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// Ticket 150 (review fix 3) — the structured timeout surface at the submit route. With the
/// gate no longer swallowing <see cref="RuleEngineTimeoutException"/> (D1: a wall-clock trip
/// is a non-authoritative infrastructure fault, never an evaluation outcome), and no exception
/// middleware in the production host, a submit timeout would escape as a bodyless 500. The
/// route now maps it to a structured, retryable 503 in the route family's error shape — NOT a
/// 422 validation result, preserving D1. Hosts the SAME production handlers
/// (<see cref="FormsRoutes.Map"/>) over a stub engine that throws the infrastructure fault.
/// </summary>
public sealed class FormsRouteTimeoutTests : IAsyncLifetime
{
    private static readonly TeamId Team = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000f150"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The production forms composition supplies the capability issuer/verifier pair the
        // route mints per-request tokens through; the ENGINE argument is a stub that throws
        // the D1 infrastructure fault from the save path.
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();

        _app = builder.Build();

        FormsRoutes.Map(
            _app,
            new TimingOutFormEngine(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            new FixedActiveTeamAccessor(new TeamContext(Team, "Team 150", new ServiceCollection().BuildServiceProvider(), TimeProvider.System)),
            new[] { FormsRoutes.NodeOperatorRole },
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "submit: a rule-engine wall-clock timeout is a structured, retryable 503 — never a bodyless 500 or a 422 verdict (D1)")]
    public async Task Submit_Timeout_Is_Structured_503()
    {
        var resp = await _client.PostAsJsonAsync(
            $"{FormsRoutes.RouteBase}/any.form/submit", new { name = "value" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RuleEngineCodes.Timeout, body.GetProperty("code").GetString());
        // Ticket 094: the retry hint is a named detail field of the one envelope; the English sentence
        // that used to sit beside it is gone.
        Assert.True(body.GetProperty("detail").GetProperty("retryable").GetBoolean());
        Assert.False(body.TryGetProperty("error", out _));
    }

    /// <summary>Throws the D1 infrastructure fault from the submit path (the shape the engine
    /// propagates when the rule graph's wall-clock liveness ceiling trips mid-gate).</summary>
    private sealed class TimingOutFormEngine : IFormEngine
    {
        public Task<FormView> RenderAsync(FormDefinitionId form, EntityId? instance, CapabilityToken token, CancellationToken ct)
            => throw new NotSupportedException("render is not under test");

        public Task<ValidationResult> ValidateAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct)
            => throw new RuleEngineTimeoutException();

        public Task<EntityId> SaveAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct)
            => throw new RuleEngineTimeoutException();

        public Task<FormSubmitReceipt> SaveWithReceiptAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, AuthorizationWriteContext authority, CancellationToken ct, string? idempotencyKey = null, string? caseRef = null)
            => throw new RuleEngineTimeoutException();
    }

    private sealed class FixedActiveTeamAccessor : IActiveTeamAccessor
    {
        public FixedActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
