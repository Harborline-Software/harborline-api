using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Forms.Drafts;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Drafts;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>Card #3481 — submission drafts remain desktop-only until MTW-3 supplies member permissions.</summary>
[Trait("PlanCard", "MTW-2-3481")]
public sealed class FormDraftRoutesWebPlaneFenceTests
{
    private const string CallerToken = "form-drafts-web-plane-fence-caller-token";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-draft-entropy-0000";
    private const string FormId = "draft.web.plane.fence.v1";
    private const string SecretValue = "operator-unsent-draft-3481";

    private static readonly TeamId OperatorTeam = new(Guid.Parse("34810000-0000-0000-0000-0000000000fe"));

    [Fact(DisplayName =
        "3481: a web-plane member cannot list the operator's in-progress drafts")]
    public async Task MemberCannotListOperatorsDrafts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SaveAsDesktopOperatorAsync();
        using var seededResponse = seeded.Response;
        Assert.Equal(HttpStatusCode.OK, seeded.Response.StatusCode);
        Assert.NotNull(await fixture.ReadStoredDraftAsync(seeded.CaseId, seeded.PartyId));

        using var response = await fixture.ListAsMemberAsync();
        var body = await response.Content.ReadAsStringAsync();

        // On the unfixed mapping this assertion prints the returned operator draft, making the live
        // disclosure visible in the red-first output rather than recording only an unexpected 200.
        Assert.DoesNotContain(SecretValue, body, StringComparison.Ordinal);
        AssertFenceRefusal(response.StatusCode, body);
    }

    public static IEnumerable<object[]> WebPlaneDraftRoutes()
    {
        yield return [HttpMethod.Put, $"{FormDraftRoutes.RouteBase}/{FormId}/drafts/{Guid.NewGuid():D}"];
        yield return [HttpMethod.Get, $"{FormDraftRoutes.RouteBase}/{FormId}/drafts/{Guid.NewGuid():D}"];
        yield return [HttpMethod.Delete, $"{FormDraftRoutes.RouteBase}/{FormId}/drafts/{Guid.NewGuid():D}"];
        yield return [HttpMethod.Get, $"{FormDraftRoutes.RouteBase}/drafts"];
    }

    [Theory(DisplayName = "3481: every drafts route refuses the web plane with the stable fence code")]
    [MemberData(nameof(WebPlaneDraftRoutes))]
    public async Task EveryDraftRouteRefusesTheWebPlane(HttpMethod method, string path)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Put)
        {
            request.Content = JsonContent.Create(new { values = new { note = "member write" } });
        }

        using var response = await fixture.SendAsMemberAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AssertFenceRefusal(response.StatusCode, body);
    }

    [Fact(DisplayName = "3481: the desktop plane can still write and read its durable draft")]
    public async Task DesktopPlaneCanStillWriteAndReadItsDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.SaveAsDesktopOperatorAsync();
        using var savedResponse = saved.Response;
        Assert.Equal(HttpStatusCode.OK, saved.Response.StatusCode);

        var durable = await fixture.ReadStoredDraftAsync(saved.CaseId, saved.PartyId);
        Assert.NotNull(durable);
        Assert.Contains(SecretValue, System.Text.Encoding.UTF8.GetString(durable.Body.Span), StringComparison.Ordinal);

        using var response = await fixture.ReadAsDesktopOperatorAsync(saved.CaseId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SecretValue, body, StringComparison.Ordinal);
    }

    private static void AssertFenceRefusal(HttpStatusCode statusCode, string body)
    {
        Assert.Equal(HttpStatusCode.Forbidden, statusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            doc.RootElement.GetProperty("code").GetString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly HttpClient _client;
        private readonly ISubmissionDraftStore _store;
        private readonly CapturingLoggerProvider _logs;

        private Fixture(
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            HttpClient client,
            ISubmissionDraftStore store,
            CapturingLoggerProvider logs)
        {
            _outerProvider = outerProvider;
            _app = app;
            _client = client;
            _store = store;
            _logs = logs;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var activeTeam = new FixedActiveTeamAccessor(
                new TeamContext(OperatorTeam, "Operator Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
            var memberships = new InMemoryTeamRegistry();
            await memberships.AddMembershipAsync(
                ActiveTeamAuthorizationContext.NodeOperator,
                new TeamMembership(
                    OperatorTeam.Value,
                    "Operator Team",
                    TeamRolePermissions.DisplayName(TeamRole.Admin),
                    KeyFingerprint.FromPublicKey(OperatorTeam.Value.ToByteArray()),
                    TeamRole.Admin));

            var logs = new CapturingLoggerProvider();
            var outer = new ServiceCollection();
            outer.AddLogging(b => b.ClearProviders().AddProvider(logs));
            outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
            outer.AddSingleton<IMutableTeamRegistry>(memberships);
            outer.AddSingleton<ITeamRegistry>(memberships);
            outer.AddNodeFinancialPosting();
            outer.AddSingleton<Harborline.Api.Foundation.MultiTenancy.ITenantContext, ActiveTeamTenantContext>();
            outer.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            outer.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            outer.AddTestAuthorizationGate().AddTestNodeForms();
            outer.AddNodeDraftPartyContext("34810000-0000-0000-0000-0000000000aa");
            outer.AddHarborlineSubmissionDrafts();
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());

            var provider = outer.BuildServiceProvider();
            await SeedFormDefinitionAsync(provider);

            var app = new SharedHostedWebApp(
                provider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new LocalNodeExecutableEndpointRegistry(),
                provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                provider.GetRequiredService<TimeProvider>());
            var endpoint = new HostedFormDraftsApiEndpoint(
                app,
                provider,
                provider.GetRequiredService<ILogger<HostedFormDraftsApiEndpoint>>());
            await endpoint.StartAsync(CancellationToken.None);
            await app.StartAsync(CancellationToken.None);

            return new Fixture(
                provider,
                app,
                new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) },
                provider.GetRequiredService<ISubmissionDraftStore>(),
                logs);
        }

        internal async Task<(HttpResponseMessage Response, DraftCaseId CaseId, Guid PartyId)>
            SaveAsDesktopOperatorAsync()
        {
            var caseId = DraftCaseId.NewId();
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"{FormDraftRoutes.RouteBase}/{FormId}/drafts/{caseId.Value}")
            {
                Content = JsonContent.Create(new { values = new { note = SecretValue } }),
            };
            var response = await SendAsDesktopOperatorAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Desktop draft save returned {(int)response.StatusCode} {response.StatusCode}. " +
                $"Body: {body}\nCaptured errors:\n{_logs.FormatErrors()}");

            var saved = JsonSerializer.Deserialize<DraftSavedResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(saved);
            return (response, caseId, Guid.ParseExact(saved.PartyId, "N"));
        }

        internal Task<SubmissionDraft?> ReadStoredDraftAsync(DraftCaseId caseId, Guid partyId) =>
            _store.GetAsync(new SubmissionDraftKey(
                ActiveTeamTenantContext.ProjectTenantId(OperatorTeam), caseId, partyId));

        internal Task<HttpResponseMessage> ListAsMemberAsync() =>
            SendAsMemberAsync(new HttpRequestMessage(HttpMethod.Get, $"{FormDraftRoutes.RouteBase}/drafts"));

        internal Task<HttpResponseMessage> ReadAsDesktopOperatorAsync(DraftCaseId caseId) =>
            SendAsDesktopOperatorAsync(new HttpRequestMessage(
                HttpMethod.Get,
                $"{FormDraftRoutes.RouteBase}/{FormId}/drafts/{caseId.Value}"));

        internal async Task<HttpResponseMessage> SendAsMemberAsync(HttpRequestMessage request)
        {
            using (request)
            {
                request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
                return await _client.SendAsync(request);
            }
        }

        private async Task<HttpResponseMessage> SendAsDesktopOperatorAsync(HttpRequestMessage request)
        {
            using (request)
            {
                request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
                return await _client.SendAsync(request);
            }
        }

        private static async Task SeedFormDefinitionAsync(IServiceProvider provider)
        {
            var registry = provider.GetRequiredService<ISchemaRegistry>();
            var store = provider.GetRequiredService<IFormDefinitionStore>();
            var schema = await registry.RegisterAsync(
                """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object",
                  "properties": { "note": { "type": "string" } },
                  "additionalProperties": false
                }
                """);
            var tenant = ActiveTeamTenantContext.ProjectTenantId(OperatorTeam);
            var definition = new FormDefinition(
                new FormDefinitionId(FormId),
                new SemanticVersion(1, 0, 0),
                FormDefinitionStatus.Draft,
                tenant,
                IdentityRef.System,
                schema.Id,
                new HarborlineOverlay(
                    new Dictionary<string, FieldOverlay>
                    {
                        ["note"] = new(InternationalizedText.FromInvariant("Note"), ControlHint: "text"),
                    },
                    [new FormSection(
                        "main",
                        InternationalizedText.FromInvariant("Draft"),
                        ["note"],
                        new SectionAccess(Array.Empty<string>(), Array.Empty<string>()))],
                    Array.Empty<RuleDefinition>(),
                    InternationalizedText.FromInvariant("Draft fence probe")),
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            await store.RegisterAsync(definition);
            await store.PublishAsync(new DefinitionCoordinates(
                tenant, definition.Id.Value, definition.Version.ToString()));
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
        }
    }

    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    "account-form-draft-fence",
                    new TenantId(OperatorTeam.Value.ToString("D")),
                    new PrincipalUserId("principal-form-draft-fence"),
                    new CanonicalPartyReference("party-form-draft-fence-member"),
                    "membership-form-draft-fence",
                    2,
                    [new PinnedGrantOwnerVersion("grant-form-draft-fence", 3)],
                    5,
                    "session-form-draft-fence",
                    "coordination-form-draft-fence")
                : null);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLogRecord> _records = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _records);

        public string FormatErrors()
        {
            var errors = _records.Where(record => record.Level >= LogLevel.Error).ToArray();
            return errors.Length == 0
                ? "<none>"
                : string.Join(Environment.NewLine, errors.Select(record =>
                    $"[{record.Level}] {record.Category}: {record.Message}" +
                    (record.Exception is null ? string.Empty : Environment.NewLine + record.Exception)));
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<CapturedLogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                records.Enqueue(new CapturedLogRecord(category, logLevel, formatter(state, exception), exception));
        }

        private sealed record CapturedLogRecord(
            string Category,
            LogLevel Level,
            string Message,
            Exception? Exception);
    }
}
