using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Versions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

public sealed class PackBoundSelectedPrincipalTests
{
    private const string Principal = "selected-grant-subject";
    private const string Party = "selected-attribution-party";
    private const string Type = "selected.note";
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000003701");
    private static readonly TenantId OtherTenant = new("bbbbbbbb-0000-0000-0000-000000003702");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_principal_grant_authorizes_while_party_remains_record_author(bool differentActiveTenant)
    {
        await using var host = await Host.OpenAsync(Principal, differentActiveTenant);
        using var response = await host.CreateAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = receipt.GetProperty("id").GetString()!;
        var auditId = receipt.GetProperty("auditId").GetGuid();
        Assert.NotEqual(Guid.Empty, auditId);

        var entity = Assert.Single(await host.RecordsAsync(Tenant));
        Assert.Equal(id, entity.Id.LocalPart);
        Assert.Equal("Selected note", entity.Body.RootElement.GetProperty("title").GetString());
        var version = await host.Services.GetRequiredService<IVersionStore>().GetVersionAsync(entity.CurrentVersion);
        Assert.Equal(Party, version!.Author.Value);
        var registryRows = await host.Services.GetRequiredService<IRegistryEntityRepository>()
            .ListByTypeAsync(Tenant, new EntityTypeId(Type));
        Assert.Equal(id, Assert.Single(registryRows).Id.Value);
        Assert.Contains(host.Services.GetRequiredService<IRegistryAuditLog>().ForTenant(Tenant),
            row => row.ActorRef == Party && row.Subject.Contains(id, StringComparison.Ordinal));
        Assert.Empty(await host.RecordsAsync(OtherTenant));
        await host.AssertTraceAsync(auditId, "verdict:allowed");
    }

    [Fact]
    public async Task A_grant_to_the_attribution_party_cannot_authorize_the_selected_principal()
    {
        await using var host = await Host.OpenAsync(Party);
        using var response = await host.CreateAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", refusal.GetProperty("code").GetString());
        Assert.Equal("records:write", refusal.GetProperty("permission").GetString());
        Assert.Empty(await host.RecordsAsync(Tenant));
        Assert.Empty(await host.Services.GetRequiredService<IRegistryEntityRepository>()
            .ListByTypeAsync(Tenant, new EntityTypeId(Type)));
        await host.AssertTraceAsync(refusal.GetProperty("auditId").GetGuid(), "verdict:denied");
    }

    private sealed class Host(WebApplication app, HttpClient client, IDisposable configuration) : IAsyncDisposable
    {
        internal IServiceProvider Services => app.Services;

        internal Task<HttpResponseMessage> CreateAsync() => client.PostAsJsonAsync(
            "/api/local-node/asset-registry/entities",
            new { type = Type, displayName = "Selected note", values = new { title = "Selected note" } });

        internal async Task<List<Entity>> RecordsAsync(TenantId tenant)
        {
            var records = new List<Entity>();
            await foreach (var entity in Services.GetRequiredService<IEntityStore>()
                .QueryAsync(new EntityQuery(Tenant: tenant)))
                if (entity.Id.Scheme == "record" && entity.Id.Authority == "asset-registry") records.Add(entity);
            return records;
        }

        internal async Task AssertTraceAsync(Guid id, string verdict)
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(Tenant)))
                if (row.AuditId == id) rows.Add(row);
            var recorded = Assert.Single(rows);
            Assert.Equal(Principal, recorded.Actor!.Value.Value);
            var trace = recorded.AuthoritySnapshot!.Trace!;
            Assert.Equal(4, trace.Count);
            var facts = trace.SelectMany(step => step.Facts).ToArray();
            Assert.Contains("principal:" + Principal, facts);
            Assert.Contains(verdict, facts);
            Assert.DoesNotContain("principal:" + Party, facts);
        }

        internal static async Task<Host> OpenAsync(string grantedSubject, bool differentActiveTenant = false)
        {
            var now = DateTimeOffset.UtcNow;
            var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
            var vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
            var seedWriter = new AuthorizationDefinitionWriter(
                configuration, configuration, new AuthorizationDefinitionAdmission(vocabulary),
                new AuthorizationCapabilityBindingAdmission(), TestAuthorization.AllowGate(), grants);
            await new AccessGrantAuthorizationSeed(seedWriter, configuration, grants)
                .InstallAsync(Tenant, now.AddMinutes(-1), AuthorizationSeedProfile.Production);
            var subject = new ActorId(grantedSubject);
            await grants.AppendAsync(Tenant, new AccessGrant(
                GrantId.New(), Tenant, subject, AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/"),
                GrantResidency.Cache, new GrantValidity(now.AddMinutes(-1)), GranterKind.Person, subject,
                now.AddMinutes(-1), new GrantProvenance(GrantSourceKind.Manual,
                    new GrantReason(GrantReasonCodes.Manual), subject), now.AddMinutes(-1)));
            // The request gate joins real stored grants to real seeded definitions; it never guesses a verdict.
            var gate = new AuthorizationGate(new DefinitionJoinedAuthorizationReader(grants, configuration),
                new EmptyRecordStandingResolver(), configuration);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            var audit = new InMemoryAuditTrail();
            builder.Services.AddSingleton<IAuditTrail>(audit);
            builder.Services.AddSingleton<IAuthorizedAuditTrail>(audit);
            builder.Services.AddTestAuthorizationGate();
            builder.Services.AddSingleton(gate);
            builder.Services.AddTestNodeForms(configureWriters: (services, mutations, _) =>
                services.AddSingleton(sp => new NodeEntityWriter(null!, mutations(sp),
                    sp.GetRequiredService<CompiledSchemaEntityValidator>(), gate,
                    sp.GetRequiredService<AuthorizationRefusalAudit>(), sp.GetRequiredService<AuthorizedActAudit>())));
            builder.Services.AddAuthorizedActAudit();
            builder.Services.AddAuthorizationRefusalAudit();
            builder.Services.AddNodeAssetRegistry();
            builder.Services.AddSingleton<PackBoundRegistryRecordWriter>();
            var app = builder.Build();
            var schema = await app.Services.GetRequiredService<ISchemaRegistry>().RegisterAsync(
                """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""");
            var form = new FormDefinition(new FormDefinitionId("selected.note.capture"), new SemanticVersion(1, 0, 0),
                FormDefinitionStatus.Draft, Tenant, IdentityRef.System, schema.Id,
                new HarborlineOverlay(new Dictionary<string, FieldOverlay>(), [], [],
                    InternationalizedText.FromInvariant("Selected note")), null, now, now);
            var forms = app.Services.GetRequiredService<IFormDefinitionStore>();
            await forms.RegisterAsync(form);
            await forms.PublishAsync(new DefinitionCoordinates(Tenant, form.Id.Value, form.Version.ToString()));
            app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(new EntityTypeId(Type),
                new EntityTypeDescriptor("Selected note", EntityTrait.Movable,
                    PropertyFormBinding: new FormBindingRef(form.Id, form.Version)), CascadeLayer.Pack));
            var activeTenant = differentActiveTenant ? OtherTenant : Tenant;
            var active = new ActiveTeam(new TeamContext(new TeamId(Guid.Parse(activeTenant.Value)), "Active",
                new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
            var principal = new SelectedSessionRequestPrincipal("selected-account", Tenant,
                new PrincipalUserId(Principal), new CanonicalPartyReference(Party), "membership", 1,
                [new PinnedGrantOwnerVersion("pinned-grant", 1)], 1, "session", "coordination");
            AuthorizationDenialTranslation.Use(app);
            app.Use(async (http, next) =>
            {
                // Authentication is upstream; this fixture pins the route's immutable listener feature.
                http.Features.Set(principal);
                await next(http);
            });
            AssetRegistryRoutes.Map(app.MapDeviceReachableProductDataGroup(),
                app.Services.GetRequiredService<IEntityTypeRegistry>(),
                app.Services.GetRequiredService<IRegistryEntityRepository>(),
                app.Services.GetRequiredService<ITypedRelationshipStore>(),
                app.Services.GetRequiredService<IConditionAssessmentStore>(),
                app.Services.GetRequiredService<IFormSubmissionRecordStore>(), active, TimeProvider.System,
                app.Services.GetRequiredService<PackBoundRegistryRecordWriter>());
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Host(app, new HttpClient { BaseAddress = new Uri(address) }, configuration);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            configuration.Dispose();
        }
    }

    private sealed class ActiveTeam(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active => active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
