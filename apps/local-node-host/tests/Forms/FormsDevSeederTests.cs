using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// ADR 0055 forms-engine wiring (2026-06-25) — proves the DEV demo seeder (<see cref="FormsDevSeeder"/>)
/// registers + publishes the equipment-inspection form so the Harborline App dynamic-form route renders REAL engine
/// data. Drives the SAME production seeder + the SAME production route handlers
/// (<see cref="FormsRoutes.Map"/>) over an in-process Kestrel listener, then asserts the seeded form renders
/// (with bilingual labels + the PII field redacted) and a valid candidate round-trips.
/// </summary>
/// <remarks>
/// This closes the loop the route tests left open: the route tests hand-build a form fixture; this proves the
/// SHIPPED dev seeder produces a form the route serves. The dev gate is forced on with a Development host
/// environment (<see cref="CalendarDevSeeder.ShouldSeed"/>) — the same airtight gate the production seeder
/// uses; a Production host would seed nothing.
/// </remarks>
public sealed class FormsDevSeederTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000fa02"));
    private static readonly ActorId Operator = new("local");
    private static readonly IReadOnlyList<string> OperatorRoles = new[] { FormsRoutes.NodeOperatorRole };

    private const string Base = "/api/local-node/forms";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = Environments.Development; // force the dev seed gate ON.
        builder.Logging.ClearProviders();

        // The PRODUCTION forms composition + the field encryptor the recovery coordinator supplies (INV-S3).
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();

        // The active-team accessor the seeder + route resolve the tenant from.
        var activeTeam = new FixedActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));
        builder.Services.AddSingleton<IActiveTeamAccessor>(activeTeam);

        _app = builder.Build();

        // Drive the SHIPPED dev seeder directly (StartAsync registers + publishes the demo form).
        var seeder = new FormsDevSeeder(
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
            activeTeam,
            _app.Environment,
            _app.Services.GetRequiredService<ILogger<FormsDevSeeder>>(),
            TimeProvider.System);
        await seeder.StartAsync(CancellationToken.None);

        // Map the SAME production routes (mirrors HostedFormsApiEndpoint wiring).
        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            activeTeam,
            OperatorRoles,
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

    [Fact(DisplayName = "seed: the dev-seeded equipment-inspection form renders (bilingual labels, PII redacted)")]
    public async Task SeededForm_Renders()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Base}/{FormsDevSeeder.DemoFormId}");

        Assert.Equal(FormsDevSeeder.DemoFormId, doc.GetProperty("formId").GetString());
        Assert.Equal("1.0.0", doc.GetProperty("version").GetString());

        // Title carries both en + ar (the Dubai path). The view resolves to a default locale string, but the
        // raw values map carries both languages.
        var title = doc.GetProperty("title");
        Assert.Equal("en", title.GetProperty("defaultLocale").GetString());
        var titleValues = title.GetProperty("values");
        Assert.True(titleValues.TryGetProperty("en", out _));
        Assert.True(titleValues.TryGetProperty("ar", out var titleAr));
        Assert.False(string.IsNullOrWhiteSpace(titleAr.GetString()));

        var fields = doc.GetProperty("sections")[0].GetProperty("fields");
        // The PII inspectorName is structurally present but never readable in a view.
        var inspector = FieldByName(fields, "inspectorName");
        Assert.True(inspector.GetProperty("isSensitive").GetBoolean());
        Assert.False(inspector.GetProperty("isReadable").GetBoolean());

        // A non-PII field is readable + carries its bilingual label.
        var assetId = FieldByName(fields, "assetId");
        Assert.True(assetId.GetProperty("isReadable").GetBoolean());
        Assert.True(assetId.GetProperty("label").GetProperty("values").TryGetProperty("ar", out _));
    }

    [Fact(DisplayName = "seed: a valid candidate round-trips against the seeded form (INV-S1 read-back)")]
    public async Task SeededForm_Submit_RoundTrips()
    {
        var candidate = new
        {
            assetId = "metro-car-12",
            assetType = "metro-car",
            conditionRating = 4,
            inspectedOn = "2026-06-26",
            status = "pass",
            followUp = false,
            notes = "Brake pads within tolerance.",
            inspectorName = "A. Khan",
        };
        var resp = await _client.PostAsJsonAsync($"{Base}/{FormsDevSeeder.DemoFormId}/submit", candidate);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = created.GetProperty("instanceId").GetString()!;
        Assert.StartsWith("forminst:", instanceId);

        // Read back through the SAME tenant — non-PII values return; the PII field stays redacted.
        var view = await _client.GetFromJsonAsync<JsonElement>(
            $"{Base}/{FormsDevSeeder.DemoFormId}?instance={Uri.EscapeDataString(instanceId)}");
        var fields = view.GetProperty("sections")[0].GetProperty("fields");
        Assert.Equal("metro-car-12", FieldByName(fields, "assetId").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, FieldByName(fields, "inspectorName").GetProperty("value").ValueKind);
    }

    [Fact(DisplayName = "seed: a schema-invalid candidate is a 422 with JSON-Pointer errors")]
    public async Task SeededForm_Invalid_422()
    {
        // conditionRating out of range (1–5) + required assetId/inspectedOn missing.
        var bad = new { conditionRating = 99 };
        var resp = await _client.PostAsJsonAsync($"{Base}/{FormsDevSeeder.DemoFormId}/submit", bad);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isValid").GetBoolean());
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
    }

    private static JsonElement FieldByName(JsonElement fields, string name)
    {
        foreach (var f in fields.EnumerateArray())
        {
            if (f.GetProperty("name").GetString() == name)
            {
                return f;
            }
        }
        throw new Xunit.Sdk.XunitException($"field '{name}' not found in view");
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class FixedActiveTeamAccessor : IActiveTeamAccessor
    {
        public FixedActiveTeamAccessor(TeamContext active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
