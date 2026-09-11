using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Schema.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 151 (L1418) — the live record write is validated by a REAL validator before persistence.
/// The validator under test is the composed default: a body is evaluated against the artefact an
/// ACTIVATION compiled (<see cref="CompiledSchemaCatalog"/>), the write path never compiles, an
/// unactivated schema id refuses by name, and the gate still decides first.
/// </summary>
public sealed class CompiledSchemaEntityValidationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private ActiveTeam _activeTeam = null!;
    private TeamContext _team = null!;
    private MutableAuthorizationContext _authorization = null!;
    private const string Route = "/api/local-node/entities";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestKernelClock();

        _authorization = new MutableAuthorizationContext();
        _authorization.Allow(TeamRolePermissions.RecordsWrite);
        builder.Services.AddSingleton<IAuthorizationContext>(_authorization);
        builder.Services.AddSingleton(Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        // The REAL validation composition: the registry's JSON Schema engine, the compile-at-activation
        // catalog (baseline record schemas compiled once, here) and the production validator.
        builder.Services.AddHarborlineKernelSchemaRegistry();
        builder.Services.AddSingleton(sp => Baseline(sp.GetRequiredService<ISchemaRegistry>()));
        builder.Services.AddSingleton<IEntityValidator>(sp => new CompiledSchemaEntityValidator(
            sp.GetRequiredService<ISchemaRegistry>(),
            sp.GetRequiredService<CompiledSchemaCatalog>()));

        _dir = Path.Combine(Path.GetTempPath(), "harborline-compiled-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "validation-test.db")};Pooling=False";
        _team = new TeamContext(
            new TeamId(Guid.Parse("7e570000-0000-0000-0000-0000000000cc")),
            "Tenant V",
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System);
        _activeTeam = new ActiveTeam(_team);

        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        builder.Services.AddSingleton<IActiveTeamAccessor>(_activeTeam);
        builder.Services.AddScoped<Harborline.Api.Foundation.MultiTenancy.ITenantContext, Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext>();
        builder.Services.AddSingleton<ISelectedSessionPermissionResolver, FailClosedSelectedSessionPermissionResolver>();
        builder.Services.AddScoped<SelectedSessionTenantContext>();

        _app = builder.Build();
        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _app.Use(async (context, next) =>
        {
            context.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(context);
        });

        Writer = new NodeEntityWriter(
            factory,
            _app.Services.GetRequiredService<IEntityValidator>(),
            Authorization.TestAuthorization.AllowGate());
        EntityRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            factory,
            _activeTeam,
            Writer,
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    private NodeEntityWriter Writer { get; set; } = null!;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _team.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // (a) — a body that violates the ACTIVATED record schema is refused, named, pointed, nothing persists.
    [Fact(DisplayName = "151 L1418 (a): a wrong-typed property is refused by the compiled schema (422 + code + pointer) — holds RW-2 RW-4")]
    public async Task Create_WrongTypedProperty_IsRefusedWithCodeAndPointer()
    {
        var resp = await _client.PostAsync(
            Route,
            Json($$"""{"legalName":"{{TooLong}}","kind":"Llc"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // The 422 is the named EntityValidationRefusal DTO: a dotted code, no prose `error` key.
        Assert.False(body.TryGetProperty("error", out _));
        Assert.Equal(CompiledSchemaEntityValidator.BodyInvalid, body.GetProperty("code").GetString());
        Assert.Contains(
            "/legalName",
            body.GetProperty("pointers").EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain(TooLong, body.GetProperty("detail").GetString());
        await AssertNothingPersisted();
    }

    // (a) — the same refusal for a missing required property, through the headless coordinator path.
    [Fact(DisplayName = "151 L1418 (a): a missing required property is refused by the compiled schema — holds RW-2 RW-4")]
    public async Task Create_MissingRequiredProperty_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            Writer.CreateLegalEntityAsync(
                new CreateLegalEntityCommand(LegalEntityId.NewId(), null, "Llc", "DisregardedEntity", null),
                Authority()).AsTask());

        Assert.Equal(CompiledSchemaEntityValidator.BodyInvalid, refusal.ReasonCode);
        Assert.NotEmpty(refusal.Pointers);
        await AssertNothingPersisted();
    }

    // (b) — a valid body persists and reads back.
    [Fact(DisplayName = "151 L1418 (b): a valid body passes the compiled schema, persists and reads back — holds RW-2")]
    public async Task Create_ValidBody_PersistsAndReadsBack()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "Valid Holdings LLC" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Contains(
            "Valid Holdings LLC",
            list.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("legalName").GetString()));
    }

    // (c) — an unknown (unactivated) schema id refuses by name; nothing persists.
    [Fact(DisplayName = "151 L1418 (c): an unactivated schema id refuses by name, nothing persists — holds RW-3")]
    public async Task Validate_UnactivatedSchema_RefusesByName()
    {
        var validator = _app.Services.GetRequiredService<IEntityValidator>();
        using var body = JsonDocument.Parse("""{"legalName":"Unknown Schema LLC"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            validator.ValidateAsync(new SchemaId("not-activated"), body));

        Assert.Equal(CompiledSchemaEntityValidator.SchemaUnknown, refusal.ReasonCode);
        await AssertNothingPersisted();
    }

    // (d) — ordering: an unauthorized caller with an invalid body is refused by the GATE, not the validator.
    [Fact(DisplayName = "151 L1418 (d): an unauthorized caller with an invalid body is refused by the gate, not validation — holds RW-1")]
    public async Task Create_UnauthorizedWithInvalidBody_IsRefusedByTheGate()
    {
        _authorization.DenyAll();

        var resp = await _client.PostAsync(
            Route,
            Json($$"""{"legalName":"{{TooLong}}","kind":"Llc"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        await AssertNothingPersisted();
    }

    // (d) at the coordinator — the gate decides BEFORE the validator sees the body: a denied caller
    // with an invalid body gets the authorization refusal, never the validation one.
    [Fact(DisplayName = "151 L1418 (d): the writer's gate refuses a denied caller before validation runs — holds RW-1")]
    public async Task Writer_DeniedCallerWithInvalidBody_IsRefusedByTheGate()
    {
        var denied = new NodeEntityWriter(
            _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(),
            _app.Services.GetRequiredService<IEntityValidator>(),
            Authorization.TestAuthorization.Gate(false));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            denied.CreateLegalEntityAsync(
                new CreateLegalEntityCommand(LegalEntityId.NewId(), null, "SuperCorp", "DisregardedEntity", null),
                Authority()).AsTask());
        await AssertNothingPersisted();
    }

    // (e) — the headless path (no HTTP) runs the same validator after the same gate.
    [Fact(DisplayName = "151 L1418 (e): the headless coordinator path runs the same validator after the gate — holds RW-1 RW-2")]
    public async Task HeadlessCoordinator_RunsTheSameValidator()
    {
        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            Writer.CreateLegalEntityAsync(
                new CreateLegalEntityCommand(LegalEntityId.NewId(), "Headless LLC", "SuperCorp", "DisregardedEntity", null),
                Authority()).AsTask());

        Assert.Equal(CompiledSchemaEntityValidator.BodyInvalid, refusal.ReasonCode);
        Assert.Contains("/kind", refusal.Pointers);
        await AssertNothingPersisted();

        var created = await Writer.CreateLegalEntityAsync(
            new CreateLegalEntityCommand(LegalEntityId.NewId(), "Headless LLC", "Llc", "DisregardedEntity", null),
            Authority());
        Assert.Equal("Headless LLC", created.Entity.LegalName);
    }

    // (f) — invalidation: re-activation replaces the compiled artefact; the next write sees v2.
    [Fact(DisplayName = "151 L1418 (f): re-activating a record type replaces the compiled artefact atomically — holds RW-6")]
    public async Task ReActivation_ReplacesTheCompiledArtefact()
    {
        var registry = _app.Services.GetRequiredService<ISchemaRegistry>();
        var catalog = _app.Services.GetRequiredService<CompiledSchemaCatalog>();
        var validator = _app.Services.GetRequiredService<IEntityValidator>();
        var name = new SchemaId("records.widget");
        using var body = JsonDocument.Parse("""{"serial":"W-1"}""");

        var v1 = await catalog.ActivateAsync(name, """{"type":"object","required":["serial"]}""") ?? throw new InvalidOperationException("v1 did not activate.");
        await validator.ValidateAsync(name, body);

        var v2 = await catalog.ActivateAsync(
            name,
            """{"type":"object","required":["serial","installedOn"]}""")
            ?? throw new InvalidOperationException("v2 did not activate.");
        Assert.NotEqual(v1.ContentAddress, v2.ContentAddress);
        Assert.True(catalog.TryGet(name, out var current));
        Assert.Equal(v2.CompiledSchemaId, current.CompiledSchemaId);

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            validator.ValidateAsync(name, body));
        Assert.Equal(CompiledSchemaEntityValidator.BodyInvalid, refusal.ReasonCode);

        // Both artefacts remain in the registry — the v1 compile is not torn down under a write
        // that is still holding it, and the swap was one dictionary write.
        Assert.NotNull(await registry.GetAsync(v1.CompiledSchemaId));
    }

    // Composition: the REAL validator is what the node composes for both record write coordinators,
    // and the baseline record schema is compiled when the catalog is composed (not per write).
    [Fact(DisplayName = "151 L1418: the node composes the compiled-schema validator for both record coordinators — holds RW-7")]
    public void Composition_GivesBothRecordCoordinatorsTheRealValidator()
    {
        var program = Read("apps/local-node-host/Program.cs");
        var writerComposition = program[program.IndexOf("new Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter(", StringComparison.Ordinal)..];
        Assert.Contains("CompiledSchemaEntityValidator", writerComposition[..900], StringComparison.Ordinal);
        var hierarchyComposition = program[program.IndexOf("new Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator(", StringComparison.Ordinal)..];
        Assert.Contains("CompiledSchemaEntityValidator", hierarchyComposition[..900], StringComparison.Ordinal);

        var forms = Read("apps/local-node-host/Data/Forms/NodeFormsComposition.cs");
        Assert.Contains("new CompiledSchemaCatalog(", forms, StringComparison.Ordinal);
        Assert.Contains("NodeRecordSchemas.ActivateBaseline(catalog)", forms, StringComparison.Ordinal);
        Assert.Contains("new CompiledSchemaEntityValidator(", forms, StringComparison.Ordinal);
    }

    // (3) — the OTHER live record write path: a hierarchy split mints records without touching the
    // route or the entity writer. It runs the same validator, after its own admission.
    [Fact(DisplayName = "151 L1418: a hierarchy split's minted record runs the validator (unactivated schema refuses) — holds RW-2 RW-3")]
    public async Task HierarchySplit_RunsTheValidator()
    {
        var at = TimeProvider.System.GetUtcNow();
        var actor = new ActorId("split-operator");
        var tenant = new TenantId("split-tenant");
        var storage = new Harborline.Api.Foundation.Assets.Common.InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, TimeProvider.System);
        var hierarchy = new Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities,
            hierarchy,
            new HierarchyAuthorizedAuditWriter(new Harborline.Api.Foundation.Assets.Audit.InMemoryAuditLog(storage)),
            Authorization.TestAuthorization.AllowGate(),
            TimeProvider.System,
            _app.Services.GetRequiredService<IEntityValidator>());

        using var oldBody = JsonDocument.Parse("""{"name":"old"}""");
        using var newBody = JsonDocument.Parse("""{"name":"new"}""");
        var oldId = await entities.CreateAsync(
            new SchemaId("records.unactivated"), oldBody, Split("split-old", actor, tenant, at));

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            coordinator.SplitAsync(
                oldId,
                [new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(
                    new SchemaId("records.unactivated"), newBody, Split("split-new", actor, tenant, at))],
                new Dictionary<EntityId, EntityId>(),
                "split",
                actor,
                tenant,
                at));

        Assert.Equal(CompiledSchemaEntityValidator.SchemaUnknown, refusal.ReasonCode);
        Assert.NotNull(await entities.GetAsync(oldId));
    }

    private static CreateOptions Split(string localPart, ActorId actor, TenantId tenant, DateTimeOffset at) =>
        new("entity", "test", localPart, actor, tenant, at, ExplicitLocalPart: localPart);

    // A schema that declares nothing still passes a well-formed body.
    [Fact(DisplayName = "151 L1418: an activated schema that declares nothing accepts a well-formed body — holds RW-2")]
    public async Task SchemaDeclaringNothing_AcceptsAWellFormedBody()
    {
        var catalog = _app.Services.GetRequiredService<CompiledSchemaCatalog>();
        var validator = _app.Services.GetRequiredService<IEntityValidator>();
        await catalog.ActivateAsync(new SchemaId("records.anything"), """{"type":"object"}""");

        using var body = JsonDocument.Parse("""{"anything":true}""");
        await validator.ValidateAsync(new SchemaId("records.anything"), body);
    }

    // 201 characters: the activated schema caps legalName at 200, and the wire DTO does not.
    private static readonly string TooLong = new('x', 201);

    private static string Read(string relative, [System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null
            && !(Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages"))))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static StringContent Json(string body) =>
        new(body, System.Text.Encoding.UTF8, "application/json");

    private AuthorizationWriteContext Authority() => new(
        new ActorId("compiled-schema-tests"),
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(_team.TeamId),
        TimeProvider.System.GetUtcNow());

    private async Task AssertNothingPersisted()
    {
        await using var context = await _app.Services
            .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()
            .CreateDbContextAsync();
        Assert.DoesNotContain(
            await context.Set<LegalEntity>().AsNoTracking().Select(row => row.LegalName).ToListAsync(),
            name => name.Contains("Unauthorized") || name.StartsWith("xxx") || name.Contains("Unknown Schema")
                || name.Contains("SuperCorp"));
    }

    private static CompiledSchemaCatalog Baseline(ISchemaRegistry registry)
    {
        var catalog = new CompiledSchemaCatalog(registry);
        NodeRecordSchemas.ActivateBaseline(catalog);
        return catalog;
    }

    private sealed class ActiveTeam(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

#pragma warning disable CS0067 // the accessor never raises it; the interface requires the member
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067
    }
}
