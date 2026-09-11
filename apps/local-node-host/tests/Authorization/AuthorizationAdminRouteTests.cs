using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Contracts;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.DependencyInjection;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

[Collection("Harborline process environment")]
public sealed class AuthorizationAdminRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));
    private static readonly RoleReference Author = new(RoleVocabularies.Domain, "author");
    private static readonly RoleReference TenantReviewer = new(RoleVocabularies.Domain, "tenant-reviewer");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T15:00:00Z");
    private const string DesktopToken = "authorization-admin-desktop-token-0001";
    private const string SelectedHandle = "authorization-admin-selected-handle-with-enough-entropy-0001";

    private Harborline.Api.LocalNodeHost.Tests.Search.SearchTestStore _grantStore = null!;
    private WebApplication _webApplication = null!;
    private SharedHostedWebApp _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private InMemoryTeamRegistry _memberships = null!;
    private AuthorizationDefinitionWriter _writer = null!;
    private CountingCatalogueReader _catalogue = null!;
    private CountingRoleVocabulary _vocabulary = null!;
    private CountingConfigurationStore _writes = null!;
    private CountingStandingRuleStore _standingReader = null!;
    private AuthorizationCapabilityDefinition _definition = null!;
    private InMemoryStandingRuleDefinitionStore _standingRules = null!;
    private InMemorySchemaRegistry _schemas = null!;
    private Harborline.Api.Kernel.Audit.InMemoryAuditTrail _trail = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://[::1]:7308");
        builder.Logging.ClearProviders();
        _activeTeam = new MutableActiveTeamAccessor(Context(TeamA));
        _memberships = new InMemoryTeamRegistry();
        await _memberships.AddMembershipAsync(
            ActiveTeamAuthorizationContext.NodeOperator,
            Membership(TeamA, TeamRole.Admin));
        await _memberships.AddMembershipAsync(
            ActiveTeamAuthorizationContext.NodeOperator,
            Membership(TeamB, TeamRole.Admin));
        builder.Services.AddSingleton<IActiveTeamAccessor>(_activeTeam);
        builder.Services.AddSingleton<IMutableTeamRegistry>(_memberships);
        builder.Services.AddSingleton<ActiveTeamAuthorizationContext>();
        // The gate reads durable grants directly. Calling this context's permission
        // answer from a grant source would re-enter the gate it is already awaiting.
        _trail = new Harborline.Api.Kernel.Audit.InMemoryAuditTrail();
        builder.Services.AddSingleton<Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail>(_trail);
        builder.Services.AddSingleton<IOperationSigner>(new Ed25519Signer(KeyPair.Generate()));
        // Ticket 331 slice 2: the binding receipt is appended through AuthorizedActAudit, which the
        // production host registers (Program.cs). This fixture composes its own container, so it has to
        // register it too -- the route requires the sink and a missing one is a 500, not a silent gap.
        builder.Services.AddAuthorizedActAudit();
        builder.Services.AddTestKernelClock();
        _grantStore = await Harborline.Api.LocalNodeHost.Tests.Search.SearchTestStore.CreateAsync();
        builder.Services.AddSingleton(_grantStore.Factory);
        RegisterGate(builder.Services);
        builder.Services.AddSingleton(new NodeCallerSessionToken(DesktopToken));
        builder.Services.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
        builder.Services.AddSingleton<ISelectedSessionPermissionResolver>(new FailClosedSelectedSessionPermissionResolver());
        builder.Services.AddHarborlineTenantContext<SelectedSessionTenantContext>();
        builder.Services.AddFrozenKernelClock(new FixedTimeProvider(Now));
        _vocabulary = new CountingRoleVocabulary(new InMemoryRoleVocabulary([
            RoleDefinition.CreatePackageRole(
                new RoleDefinitionId(Guid.Parse("cccccccc-0000-0000-0000-000000000003")),
                Author.Name,
                "Author",
                "package.test"),
            RoleDefinition.CreateTenantRole(
                new RoleDefinitionId(Guid.Parse("cccccccc-0000-0000-0000-000000000005")),
                TenantReviewer.Name,
                "Tenant reviewer",
                new TenantId(TeamA.Value.ToString("D"))),
        ]));
        var store = TestInMemoryAuthorizationStores.ConfigurationStore();
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        _writes = new CountingConfigurationStore(store);
        _catalogue = new CountingCatalogueReader(store);
        _writer = new AuthorizationDefinitionWriter(
            _writes,
            store,
            new AuthorizationDefinitionAdmission(_vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            grants);
        _standingRules = new InMemoryStandingRuleDefinitionStore();
        _standingReader = new CountingStandingRuleStore(_standingRules);
        _schemas = new InMemorySchemaRegistry(TimeProvider.System);
        var standings = new StandingCatalogue(_schemas);
        _webApplication = builder.Build();
        await DesktopGrantSourceTests.SeedAsync(_webApplication.Services, new TenantId(TeamA.Value.ToString("D")), Now);
        await DesktopGrantSourceTests.SeedAsync(_webApplication.Services, new TenantId(TeamB.Value.ToString("D")), Now);
        _app = new SharedHostedWebApp(
            _webApplication,
            Options.Create(new LocalNodeOptions { HealthPort = 7309 }),
            new LocalNodeExecutableEndpointRegistry(),
            _webApplication.Services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _webApplication.Services.GetRequiredService<TimeProvider>());
        var endpoint = new HostedAuthorizationAdminApiEndpoint(
            _app,
            _vocabulary,
            _catalogue,
            _writer,
            _standingReader,
            standings,
            _activeTeam,
            new FixedTimeProvider(Now),
            _webApplication.Services.GetRequiredService<ILogger<HostedAuthorizationAdminApiEndpoint>>());
        await endpoint.StartAsync(CancellationToken.None);

        _definition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("dddddddd-0000-0000-0000-000000000004")),
            "package.test",
            1,
            AuthorizationOperation.Parse(Permission.OrgManageSettings),
            PermissionAtom.Parse($"{Permission.OrgManageSettings}@/"),
            RoleBindingSet.Of(RoleReference.Administrator, Author));
        await _writer.WriteAsync(new InstallAuthorizationDefinition(_definition));

        _vocabulary.Reset();
        _catalogue.Reset();
        _writes.Reset();
        _standingReader.Reset();
        await _app.StartAsync(CancellationToken.None);
        await _webApplication.StartAsync(CancellationToken.None);
        _app.CaptureSelectedUrl();
        _client = new HttpClient { BaseAddress = new Uri(_app.SelectedUrl!) };
        _client.DefaultRequestHeaders.Add(NodeCallerSessionToken.HeaderName, "Bearer " + DesktopToken);
    }

    internal static void RegisterGate(IServiceCollection services) => services.AddNodeAuthorizationModel();

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _webApplication.StopAsync(CancellationToken.None);
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        await _webApplication.DisposeAsync();
        await _grantStore.DisposeAsync();
    }

    [Fact]
    public async Task RoleVocabulary_IsQualifiedStableAndSealed()
    {
        var roles = await _client.GetFromJsonAsync<RoleDefinitionDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/role-vocabulary");

        Assert.Equal(4, roles!.Length);
        Assert.Equal(
            roles.OrderBy(role => role.Role.Vocabulary, StringComparer.Ordinal)
                .ThenBy(role => role.Role.Name, StringComparer.Ordinal),
            roles);
        Assert.Contains(roles, role => role.Role == new RoleReferenceDto(RoleVocabularies.Platform, "administrator")
            && role.IsSealed && role.Owner.Kind == "Platform");
        Assert.Contains(roles, role => role.Role.Vocabulary == RoleVocabularies.Domain);
        Assert.Contains(roles, role => role.Role == new RoleReferenceDto(TenantReviewer.Vocabulary, TenantReviewer.Name));
    }

    public static IEnumerable<object[]> AuthorizationRoutes()
    {
        yield return [HttpMethod.Get, $"{AuthorizationAdminRoutes.RouteBase}/role-vocabulary"];
        yield return [HttpMethod.Get, $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions"];
        yield return [HttpMethod.Get, $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{_DeniedDefinitionId:D}/binding"];
        yield return [HttpMethod.Post, $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{_DeniedDefinitionId:D}/binding"];
        yield return [HttpMethod.Get, $"{AuthorizationAdminRoutes.RouteBase}/standing-catalogue"];
    }

    private static readonly Guid _DeniedDefinitionId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    [Theory(DisplayName = "Admin routes refuse revoked durable grants; registry role changes are not authority")]
    [MemberData(nameof(AuthorizationRoutes))]
    public async Task EveryRoute_DeniesWithoutManageSettingsBeforeAnyReaderOrWriterCall(
        HttpMethod method,
        string path)
    {
        Assert.True(await _memberships.SetRoleAsync(
            ActiveTeamAuthorizationContext.NodeOperator,
            TeamA.Value,
            TeamRole.Viewer));
        var tenant = new TenantId(TeamA.Value.ToString("D"));
        var grants = _webApplication.Services.GetRequiredService<IGrantStore>();
        var grant = await grants.FindBySourceReferenceAsync(tenant, "desktop-fixture");
        Assert.NotNull(grant);
        await grants.RevokeAsync(tenant, grant.GrantId, new GrantRevocation(
            ActiveTeamAuthorizationContext.NodeOperator, Now,
            new GrantReason(GrantReasonCodes.RevocationReview, "remove manage-settings authority")));
        ResetDependencyCounts();
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new NarrowAuthorizationBindingRequest([], "denied"));
            request.Headers.Add(IdempotencyContract.HeaderName, "denied-key");
        }

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authorization.permission_required", body.RootElement.GetProperty("code").GetString());
        AssertNoDependencyCalls();
    }

    [Fact]
    public async Task HostedWebPlane_RefusesTheProductionMappedFamilyBeforeReadersRun()
    {
        ResetDependencyCounts();
        using var webClient = new HttpClient { BaseAddress = _client.BaseAddress };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions");
        request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={SelectedHandle}");

        using var response = await webClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(WebPlaneUnavailableRouteFence.UnavailableCode, body.RootElement.GetProperty("code").GetString());
        AssertNoDependencyCalls();
    }

    [Fact]
    public async Task EmptyBinding_IsCommittedReplayedOnceAndRetainedByReads()
    {
        using var first = PostBinding([], "clear-all", "same-key");
        using var firstResponse = await _client.SendAsync(first);
        using var replay = PostBinding([], "clear-all", "same-key");
        using var replayResponse = await _client.SendAsync(replay);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var result = await firstResponse.Content.ReadFromJsonAsync<NarrowAuthorizationBindingResponse>();
        Assert.Equal(1, result!.Revision);
        Assert.Equal("EmptyBinding", result.Warning);
        Assert.Empty(result.EffectiveRoles);
        Assert.Equal(NodeCallerParty.OperatorParty.Value, result.ChangedBy);
        Assert.Equal(Now, result.ChangedAt);
        var row = Assert.Single(await _catalogue.ListAsync(NodeTenant.Resolve(_activeTeam)));
        Assert.Equal(1, row.BindingRevision);
        Assert.Equal(BindingWarningCode.EmptyBinding, row.Warning);

        var binding = await _client.GetFromJsonAsync<AuthorizationBindingDto>(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{_definition.DefinitionId.Value:D}/binding");
        Assert.Equal("EmptyBinding", binding!.Warning);
        Assert.Empty(binding.EffectiveRoles);
        var list = await _client.GetFromJsonAsync<AuthorizationDefinitionDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions");
        Assert.Empty(Assert.Single(list!).Binding.EffectiveRoles);

        // Ticket 331 slice 2: ONE act, ONE audited entry. The replay is answered from the stored result,
        // so it must not append a second authorized act, and its auditId must be the first one's.
        var replayed = await replayResponse.Content.ReadFromJsonAsync<NarrowAuthorizationBindingResponse>();
        Assert.Equal(result.AuditId, replayed!.AuditId);
        Assert.NotNull(result.AuditId);
        var narrowings = new List<Harborline.Api.Kernel.Audit.AuditRecord>();
        await foreach (var record in _trail.QueryAsync(new Harborline.Api.Kernel.Audit.AuditQuery(
            NodeTenant.Resolve(_activeTeam),
            new Harborline.Api.Kernel.Audit.AuditEventType("AuthorizationBindingNarrowed"))))
            narrowings.Add(record);
        Assert.Equal(result.AuditId, Assert.Single(narrowings).AuditId);
    }

    [Fact]
    public async Task RemovedRoleCannotBeReadded_AndTenantBindingsAreIsolated()
    {
        using var narrow = PostBinding([new(RoleVocabularies.Platform, "administrator")], "narrow", "key-1");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(narrow)).StatusCode);
        using var widen = PostBinding([
            new(RoleVocabularies.Platform, "administrator"), new(RoleVocabularies.Domain, "author")],
            "widen", "key-2");
        var refused = await _client.SendAsync(widen);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("authorization.binding_widening_refused", await refused.Content.ReadAsStringAsync());

        _activeTeam.Active = Context(TeamB);
        var binding = await _client.GetFromJsonAsync<AuthorizationBindingDto>(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{_definition.DefinitionId.Value:D}/binding");
        Assert.Equal(0, binding!.Revision);
        Assert.Equal(2, binding.EffectiveRoles.Count);
    }

    [Fact]
    public async Task MissingIdempotencyMalformedAndUnknownAreMachineCoded()
    {
        var missingKey = await _client.PostAsJsonAsync(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{_definition.DefinitionId.Value:D}/binding",
            new NarrowAuthorizationBindingRequest([], "clear"));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Contains("authorization.idempotency_key_required", await missingKey.Content.ReadAsStringAsync());

        using var malformed = PostBinding([new("", "")], "bad", "bad-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(malformed)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{Guid.NewGuid():D}/binding")).StatusCode);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentBody_IsMachineCodedAndDoesNotAdvanceRevision()
    {
        using var first = PostBinding([], "first-body", "different-body-key");
        using var firstResponse = await _client.SendAsync(first);
        using var conflict = PostBinding([], "different-body", "different-body-key");
        using var conflictResponse = await _client.SendAsync(conflict);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        using var body = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
        Assert.Equal("authorization.idempotency_key_reused", body.RootElement.GetProperty("code").GetString());
        var row = Assert.Single(await _catalogue.ListAsync(NodeTenant.Resolve(_activeTeam)));
        Assert.Equal(1, row.BindingRevision);
        Assert.Equal(1, _writes.CommitCalls);
    }

    [Fact]
    public async Task SameMutationKey_IsScopedByTenant()
    {
        using var tenantA = PostBinding([], "tenant-policy", "cross-tenant-key");
        using var tenantAResponse = await _client.SendAsync(tenantA);
        _activeTeam.Active = Context(TeamB);
        using var tenantB = PostBinding([], "tenant-policy", "cross-tenant-key");
        using var tenantBResponse = await _client.SendAsync(tenantB);

        Assert.Equal(HttpStatusCode.OK, tenantAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tenantBResponse.StatusCode);
        Assert.Equal(1, (await tenantAResponse.Content.ReadFromJsonAsync<NarrowAuthorizationBindingResponse>())!.Revision);
        Assert.Equal(1, (await tenantBResponse.Content.ReadFromJsonAsync<NarrowAuthorizationBindingResponse>())!.Revision);
        Assert.Equal(2, _writes.CommitCalls);
    }

    [Fact]
    public async Task TenantDefinitionAndRole_AreInvisibleAndCannotBeNarrowedAcrossTenants()
    {
        var tenantA = new TenantId(TeamA.Value.ToString("D"));
        var privateDefinition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("eeeeeeee-0000-0000-0000-000000000006")),
            "package.test",
            1,
            AuthorizationOperation.Parse(Permission.OrgManageSettings),
            PermissionAtom.Parse($"{Permission.OrgManageSettings}@/private"),
            RoleBindingSet.Of(TenantReviewer));
        await _writer.WriteAsync(
            new InstallAuthorizationDefinition(privateDefinition, tenantA),
            new AuthorizationWriteContext(new ActorId("test:authorization-writer"), tenantA, Now));
        ResetDependencyCounts();

        var tenantADefinitions = await _client.GetFromJsonAsync<AuthorizationDefinitionDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions");
        var tenantARoles = await _client.GetFromJsonAsync<RoleDefinitionDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/role-vocabulary");
        Assert.Contains(tenantADefinitions!, row => row.DefinitionId == privateDefinition.DefinitionId.Value);
        Assert.Contains(tenantARoles!, row => row.Role == new RoleReferenceDto(TenantReviewer.Vocabulary, TenantReviewer.Name));

        _activeTeam.Active = Context(TeamB);
        var tenantBDefinitions = await _client.GetFromJsonAsync<AuthorizationDefinitionDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions");
        var tenantBRoles = await _client.GetFromJsonAsync<RoleDefinitionDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/role-vocabulary");
        Assert.DoesNotContain(tenantBDefinitions!, row => row.DefinitionId == privateDefinition.DefinitionId.Value);
        Assert.DoesNotContain(tenantBRoles!, row => row.Role == new RoleReferenceDto(TenantReviewer.Vocabulary, TenantReviewer.Name));

        using var hidden = await _client.GetAsync(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{privateDefinition.DefinitionId.Value:D}/binding");
        using var missing = await _client.GetAsync(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{Guid.NewGuid():D}/binding");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await MissingCodeAsync(missing), await MissingCodeAsync(hidden));

        using var narrow = PostBinding([], "cross-tenant-attempt", "cross-tenant-definition-key", privateDefinition.DefinitionId.Value);
        using var narrowResponse = await _client.SendAsync(narrow);
        Assert.Equal(HttpStatusCode.NotFound, narrowResponse.StatusCode);
        Assert.Equal("authorization.definition_not_found", await MissingCodeAsync(narrowResponse));
        Assert.Equal(0, _writes.CommitCalls);
    }

    [Fact]
    public async Task StandingCatalogue_ListsEveryAndOnlyCarryingSchemaAndRetainsEmptyFields()
    {
        await _standingRules.RegisterAsync(HandlerRule());
        var matter = await _schemas.RegisterAsync(SchemaWithProperties("handler_id", "title"));
        var tenancy = await _schemas.RegisterAsync(SchemaWithProperties("tenant_id", "handler_id"));
        _ = await _schemas.RegisterAsync(SchemaWithProperties("posted_at"));

        var rows = await _client.GetFromJsonAsync<StandingDefinitionCatalogueDto[]>(
            $"{AuthorizationAdminRoutes.RouteBase}/standing-catalogue");

        var row = Assert.Single(rows!);
        Assert.Equal("matter.handler", row.RuleId);
        Assert.Equal("handler", row.Standing);
        Assert.Equal("matter", row.DeclaredRecordType);
        Assert.Equal(
            new[] { matter.Id.Value, tenancy.Id.Value }.Order(StringComparer.Ordinal),
            Assert.Single(row.Fields, field => field.Field == "handler_id").CarryingRecordTypes);
        Assert.Empty(Assert.Single(row.Fields, field => field.Field == "unmapped").CarryingRecordTypes);
    }

    private HttpRequestMessage PostBinding(
        IReadOnlyList<RoleReferenceDto> roles,
        string reason,
        string key,
        Guid? definitionId = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{definitionId ?? _definition.DefinitionId.Value:D}/binding")
        {
            Content = JsonContent.Create(new NarrowAuthorizationBindingRequest(roles, reason)),
        };
        request.Headers.Add(IdempotencyContract.HeaderName, key);
        return request;
    }

    private void ResetDependencyCounts()
    {
        _vocabulary.Reset();
        _catalogue.Reset();
        _writes.Reset();
        _standingReader.Reset();
    }

    private void AssertNoDependencyCalls()
    {
        Assert.Equal(0, _vocabulary.ListCalls);
        Assert.Equal(0, _vocabulary.ResolveCalls);
        Assert.Equal(0, _catalogue.ListCalls);
        Assert.Equal(0, _catalogue.FindCalls);
        Assert.Equal(0, _writes.CommitCalls);
        Assert.Equal(0, _standingReader.ListCalls);
    }

    private static async Task<string?> MissingCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private static TeamContext Context(TeamId id) =>
        new(id, id.Value.ToString("D"), new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private static TeamMembership Membership(TeamId id, TeamRole role) =>
        new(
            id.Value,
            id.Value.ToString("D"),
            TeamRolePermissions.DisplayName(role),
            KeyFingerprint.FromPublicKey(id.Value.ToByteArray()),
            role);

    private static StandingRuleDefinition HandlerRule() => new(
        RuleId: "matter.handler",
        RuleVersion: "1.0.0",
        Standing: new StandingReference("handler"),
        RecordType: "matter",
        InputFields: ["handler_id", "unmapped"],
        Predicate: new RuleDefinition(
            new DefinitionEnvelope<string, string, TenantId, string?>(
                "matter.handler",
                "1.0.0",
                new TenantId("bbbbbbbb-0000-0000-0000-000000000220"),
                CascadeLayer.Pack,
                Provenance: null,
                Array.Empty<DefinitionRequirement>()),
            RuleTier.JsonLogic,
            RuleScope.Schema,
            string.Empty,
            "{\"and\":[{\"==\":[{\"var\":\"handler_id\"},\"person-7\"]},{\"==\":[{\"var\":\"unmapped\"},\"yes\"]}]}",
            RuleActionKind.Validate));

    private static string SchemaWithProperties(params string[] properties)
    {
        var propertyNodes = new JsonObject();
        foreach (var property in properties)
            propertyNodes[property] = new JsonObject { ["type"] = "string" };
        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = propertyNodes,
        }.ToJsonString();
    }

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new(null, null));
    }

    private sealed class CountingRoleVocabulary(IRoleVocabularyReader inner) : IRoleVocabularyReader
    {
        public int ResolveCalls { get; private set; }
        public int ListCalls { get; private set; }

        public ValueTask<RoleDefinition?> ResolveAsync(RoleReference role, CancellationToken ct = default)
        {
            ResolveCalls++;
            return inner.ResolveAsync(role, ct);
        }

        public ValueTask<IReadOnlyList<RoleDefinition>> ListAsync(CancellationToken ct = default)
        {
            ListCalls++;
            return inner.ListAsync(ct);
        }

        public void Reset() => (ResolveCalls, ListCalls) = (0, 0);
    }

    private sealed class CountingConfigurationStore(IAuthorizationConfigurationStore inner)
        : IAuthorizationConfigurationStore
    {
        public int CommitCalls { get; private set; }

        public ValueTask CommitAsync(ValidatedAuthorizationConfigurationWrite write, CancellationToken ct = default)
        {
            CommitCalls++;
            return inner.CommitAsync(write, ct);
        }

        public ValueTask CommitBootstrapAsync(
            ValidatedAuthorizationConfigurationWrite write,
            TenantId tenant,
            IGrantStore grants,
            CancellationToken ct = default)
        {
            CommitCalls++;
            return inner.CommitBootstrapAsync(write, tenant, grants, ct);
        }

        public void Reset() => CommitCalls = 0;
    }

    private sealed class CountingCatalogueReader(IAuthorizationDefinitionCatalogueReader inner)
        : IAuthorizationDefinitionCatalogueReader
    {
        public int ListCalls { get; private set; }
        public int FindCalls { get; private set; }

        public ValueTask<IReadOnlyList<AuthorizationDefinitionBindingView>> ListAsync(
            TenantId tenantId,
            CancellationToken ct = default)
        {
            ListCalls++;
            return inner.ListAsync(tenantId, ct);
        }

        public ValueTask<AuthorizationDefinitionBindingView?> FindAsync(
            TenantId tenantId,
            AuthorizationCapabilityDefinitionId definitionId,
            CancellationToken ct = default)
        {
            FindCalls++;
            return inner.FindAsync(tenantId, definitionId, ct);
        }

        public void Reset() => (ListCalls, FindCalls) = (0, 0);
    }

    private sealed class CountingStandingRuleStore(IStandingRuleDefinitionStore inner)
        : IStandingRuleDefinitionStore
    {
        public int ListCalls { get; private set; }

        public ValueTask RegisterAsync(StandingRuleDefinition definition, CancellationToken cancellationToken = default) =>
            inner.RegisterAsync(definition, cancellationToken);

        public ValueTask<StandingRuleDefinition?> GetAsync(
            string ruleId,
            string ruleVersion,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(ruleId, ruleVersion, cancellationToken);

        public ValueTask<bool> RemoveAsync(
            string ruleId,
            string ruleVersion,
            CancellationToken cancellationToken = default) =>
            inner.RemoveAsync(ruleId, ruleVersion, cancellationToken);

        public async IAsyncEnumerable<StandingRuleDefinition> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ListCalls++;
            await foreach (var definition in inner.ListAsync(cancellationToken))
                yield return definition;
        }

        public void Reset() => ListCalls = 0;
    }

    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, SelectedHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    "authorization-admin-account",
                    new TenantId(TeamA.Value.ToString("D")),
                    new PrincipalUserId("authorization-admin-principal"),
                    new CanonicalPartyReference("authorization-admin-party"),
                    "authorization-admin-membership",
                    1,
                    [new PinnedGrantOwnerVersion("authorization-admin-grant", 1)],
                    1,
                    "authorization-admin-session",
                    "authorization-admin-coordination")
                : null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
