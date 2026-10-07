using System.Reflection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Ticket 294 slice 3b — the desktop plane's actor is the founder's canonical tenant principal, the roster party
/// bound to this node's key, never the retired compile-time "local". The seed grants the node-operator holding to
/// that principal, and a store written before this slice has its "local" rows rekeyed to it once at boot, or the
/// boot refuses with a named remedy when the principal cannot be resolved.
/// </summary>
public sealed class DesktopActorRekeyTests
{
    private const string Retired = "local";
    private const string NodeOperatorSource = "authorization-seed:node-operator";
    private static readonly TeamId Team = new(new Guid("7e57dddd-0000-0000-0000-000000000294"));
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(Team);
    private static readonly DateTimeOffset At = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Founder = FounderTenantMembershipAttachService.DerivePrincipal(
        Tenant, InstallationFounderBootstrapCeremony.CorrelationId).Value;

    [Fact]
    [Trait("PlanCard", "294-s3b")]
    public async Task The_desktop_plane_asks_the_gate_as_the_founders_canonical_principal()
    {
        using var node = KeyPair.Generate();
        var roster = FounderRoster(node);
        var (active, _) = await ActiveAsync();
        var asked = new List<ActorId>();
        var plane = new ActiveTeamAuthorizationContext(
            active, new InMemoryTeamRegistry(), new FixedTimeProvider(At), roster, new Ed25519Signer(node),
            TestAuthorization.Gate(_ => true, request => asked.Add(request.Principal)));

        Assert.True(plane.HasPermission(TeamRolePermissions.RecordsRead));
        Assert.Equal(Founder, plane.UserId);
        Assert.All(asked, principal => Assert.Equal(Founder, principal.Value));
        Assert.NotEmpty(asked);
    }

    [Fact(DisplayName = "ck-4: an unbound request context keeps desktop authority on the desktop plane and never lends it to a device-plane request")]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_device_plane_request_never_borrows_the_desktop_operators_grants()
    {
        using var node = KeyPair.Generate();
        var (active, _) = await ActiveAsync();
        var plane = new ActiveTeamAuthorizationContext(
            active, new InMemoryTeamRegistry(), new FixedTimeProvider(At), FounderRoster(node), new Ed25519Signer(node),
            TestAuthorization.Gate(_ => true, _ => { }));
        var context = new Harborline.Api.LocalNodeHost.Health.WebSession.SelectedSessionTenantContext(
            new FailClosedSelectedSessionPermissionResolver(), plane);

        // Desktop plane (no bound web principal, no device): the operator's grant stands.
        Assert.True(context.HasPermission(TeamRolePermissions.RecordsRead));

        // Device plane: no selected principal is bound, and the desktop operator's grants are not lent.
        using (Harborline.Api.LocalNodeHost.Data.Audit.NodeCallerAttributionScope.EnterDevice("device-ck4", Tenant.Value, "device-principal"))
            Assert.False(context.HasPermission(TeamRolePermissions.RecordsRead));
    }

    [Fact]
    [Trait("PlanCard", "294-s3b")]
    public void A_desktop_request_with_no_session_principal_is_attributed_to_the_founders_canonical_principal()
    {
        using var node = KeyPair.Generate();
        var services = new ServiceCollection()
            .AddSingleton(FounderRoster(node))
            .AddSingleton<IOperationSigner>(new Ed25519Signer(node))
            .AddSingleton<NodeOperatorIdentity>()
            .BuildServiceProvider();

        var party = NodeCallerParty.Resolve(new DefaultHttpContext { RequestServices = services });

        Assert.Equal(Founder, party.Value);
    }

    [Fact]
    [Trait("PlanCard", "294-s3b")]
    public async Task The_node_operator_seed_names_the_desktop_actor_and_no_grant_subject_is_local()
    {
        using var node = KeyPair.Generate();
        await using var provider = SeedServices(FounderRoster(node), new Ed25519Signer(node), grantRows: null);
        await StartSeedAsync(provider);

        var grants = await provider.GetRequiredService<IGrantStore>().SnapshotAsync(Tenant);
        Assert.DoesNotContain(grants, grant => grant.Subject.Value == Retired);
        var holding = await provider.GetRequiredService<IGrantStore>()
            .FindBySourceReferenceAsync(Tenant, NodeOperatorSource);
        Assert.Equal(Founder, holding?.Subject.Value);
        Assert.Equal(AccessGrantAuthorizationSeed.NodeOperatorRole, holding!.Role);
    }

    [Fact]
    [Trait("PlanCard", "294-s3b")]
    public async Task A_store_holding_local_grant_rows_is_rekeyed_to_the_desktop_actor_once()
    {
        await using var store = await NodeStore.CreateAsync();
        await SeedRetiredRowsAsync(store);
        using var node = KeyPair.Generate();
        await using var provider = SeedServices(FounderRoster(node), new Ed25519Signer(node), store.Factory);

        await StartSeedAsync(provider);
        long rekeyedVersion;
        await using (var context = store.CreateContext())
        {
            Assert.False(await context.Grants.AnyAsync(row => row.SubjectId == Retired));
            Assert.False(await context.GrantAuthorizationEpochs.AnyAsync(row => row.PrincipalId == Retired));
            var holding = await context.Grants.AsNoTracking().SingleAsync(row => row.SourceReference == NodeOperatorSource);
            Assert.Equal(Founder, holding.SubjectId);
            Assert.Equal(2, holding.OwnerVersion);
            rekeyedVersion = holding.OwnerVersion;
            Assert.True(await context.GrantAuthorizationEpochs.AnyAsync(
                row => row.TenantId == Tenant.Value && row.PrincipalId == Founder));
        }

        // The real gate over the real closure: the holding now answers for the founder, and "local" holds nothing.
        var gate = provider.GetRequiredService<AuthorizationGate>();
        var unlock = AuthorizationOperation.Parse(Permission.WorkshopUnlock);
        Assert.Equal(AuthorizationVerdict.Allowed, (await gate.DecideAsync(
            new AuthorizationWriteContext(new ActorId(Founder), Tenant, AdmittedInstant.Read(new FixedTimeProvider(At))).InstallWide(unlock))).Verdict);
        Assert.NotEqual(AuthorizationVerdict.Allowed, (await gate.DecideAsync(
            new AuthorizationWriteContext(new ActorId(Retired), Tenant, AdmittedInstant.Read(new FixedTimeProvider(At))).InstallWide(unlock))).Verdict);

        // Idempotent: a second boot finds nothing to rekey and the seed does not re-add a "local" row.
        await StartSeedAsync(provider);
        await using (var context = store.CreateContext())
        {
            Assert.False(await context.Grants.AnyAsync(row => row.SubjectId == Retired));
            Assert.Equal(rekeyedVersion, (await context.Grants.AsNoTracking()
                .SingleAsync(row => row.SourceReference == NodeOperatorSource)).OwnerVersion);
        }
    }

    [Fact]
    [Trait("PlanCard", "294-s3b")]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_local_grant_row_with_no_retired_epoch_row_is_still_rekeyed_to_the_desktop_actor()
    {
        await using var store = await NodeStore.CreateAsync();
        await SeedRetiredRowsAsync(store, withEpochRow: false);
        using var node = KeyPair.Generate();
        await using var provider = SeedServices(FounderRoster(node), new Ed25519Signer(node), store.Factory);

        await StartSeedAsync(provider);

        await using var context = store.CreateContext();
        Assert.False(await context.Grants.AnyAsync(row => row.SubjectId == Retired));
        Assert.Equal(Founder, (await context.Grants.AsNoTracking()
            .SingleAsync(row => row.SourceReference == NodeOperatorSource)).SubjectId);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task The_rekey_audit_is_typed_and_names_the_moved_grant_and_both_subjects()
    {
        await using var store = await NodeStore.CreateAsync();
        await SeedRetiredRowsAsync(store);
        using var node = KeyPair.Generate();
        await using var provider = SeedServices(FounderRoster(node), new Ed25519Signer(node), store.Factory);

        await StartSeedAsync(provider);

        await using var context = store.CreateContext();
        var audit = await context.AuditOutbox.AsNoTracking()
            .SingleAsync(row => row.EventType == "AuthorizationGrantSubjectRekeyed");
        var body = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(audit.BodyJson)!;
        var moved = await context.Grants.AsNoTracking().SingleAsync(row => row.SourceReference == NodeOperatorSource);
        Assert.Equal(moved.GrantId, body["grantId"]);
        Assert.Equal(Retired, body["fromSubject"]);
        Assert.Equal(Founder, body["toSubject"]);
    }

    [Fact]
    [Trait("PlanCard", "294-s3b")]
    public async Task A_store_holding_local_grant_rows_on_a_node_with_no_roster_edge_refuses_the_boot_with_a_remedy()
    {
        await using var store = await NodeStore.CreateAsync();
        await SeedRetiredRowsAsync(store);
        using var founder = KeyPair.Generate();
        using var stranger = KeyPair.Generate();
        await using var provider = SeedServices(FounderRoster(founder), new Ed25519Signer(stranger), store.Factory);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => StartSeedAsync(provider));

        Assert.Contains("DESKTOP_ACTOR_UNRESOLVED", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("recover", refusal.Message, StringComparison.OrdinalIgnoreCase);
        await using var context = store.CreateContext();
        Assert.Equal(Retired, (await context.Grants.AsNoTracking()
            .SingleAsync(row => row.SourceReference == NodeOperatorSource)).SubjectId);
    }

    /// <summary>The retired value survives only as the rekey's read key, never as an actor or a grant subject.</summary>
    [Fact]
    [Trait("PlanCard", "294-s3b")]
    public void No_production_constant_names_the_retired_desktop_actor()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // The retired single-tenant data sentinel is a tenant id, not an actor.
            "Harborline.Api.LocalNodeHost.Data.Financial.StaticNodeTenantContext.LocalTenantId",
            "Harborline.Api.LocalNodeHost.Data.Identity.NodeOperatorIdentity.RetiredDesktopActor",
        };
        var offenders = new[] { typeof(ActiveTeamAuthorizationContext).Assembly, typeof(AccessGrantAuthorizationSeed).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)
                && Equals(field.GetRawConstantValue(), Retired))
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name}")
            .Where(name => !allowed.Contains(name))
            .ToArray();

        Assert.Empty(offenders);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static NodeTeamRoster FounderRoster(KeyPair node) => new(MemberRoster.Genesis(
        Team.Value, Founder, new Ed25519Signer(node), new Ed25519Verifier(), At, Guid.NewGuid()));

    private static async Task<(IActiveTeamAccessor Active, ITeamContextFactory Factory)> ActiveAsync()
    {
        var factory = new TeamContextFactory(TimeProvider.System);
        await factory.GetOrCreateAsync(Team, "Team 294", CancellationToken.None);
        var active = new ActiveTeamAccessor(factory);
        await active.SetActiveAsync(Team, CancellationToken.None);
        return (active, factory);
    }

    private static ServiceProvider SeedServices(
        NodeTeamRoster roster,
        IOperationSigner signer,
        Microsoft.EntityFrameworkCore.IDbContextFactory<NodeLocalSearchDbContext>? grantRows)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(roster);
        services.AddSingleton(signer);
        services.AddSingleton<NodeOperatorIdentity>();
        TestAuthorization.AddMemberRosterConstraints(services);
        if (grantRows is not null)
        {
            // The production EF authorization model over the store under test.
            services.AddSingleton(grantRows);
            services.AddNodeAuthorizationModel();
        }
        else
        {
            services.AddAccessGrantModule();
        }
        return services.BuildServiceProvider();
    }

    private static async Task StartSeedAsync(ServiceProvider provider)
    {
        var (active, factory) = await ActiveAsync();
        await ActivatorUtilities.CreateInstance<AuthorizationSeedHostedService>(
                provider, active, factory, AuthorizationSeedProfile.Production, (TimeProvider)new FixedTimeProvider(At))
            .StartAsync(CancellationToken.None);
    }

    /// <summary>The rows the pre-slice seed wrote: the node-operator holding and its epoch, keyed "local".</summary>
    private static async Task SeedRetiredRowsAsync(NodeStore store, bool withEpochRow = true)
    {
        var installer = new ActorId("installer:authorization-definition-seed");
        var grant = new AccessGrant(
            AccessGrantAuthorizationSeed.GrantIdFor(Tenant, NodeOperatorSource), Tenant, new ActorId(Retired),
            AccessGrantAuthorizationSeed.NodeOperatorRole, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(At), GranterKind.Installer, installer, At,
            new GrantProvenance(GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap, NodeOperatorSource), installer),
            At);
        await using var context = store.CreateContext();
        context.Grants.Add(NodeEfGrantStore.ToRow(grant, NodeOperatorSource));
        if (withEpochRow)
            context.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
            {
                TenantId = Tenant.Value,
                PrincipalId = Retired,
                AuthorizationEpoch = 1,
            });
        await context.SaveChangesAsync();
    }

    /// <summary>One node database file carrying the grant tables and the installation-identity tables, as production does.</summary>
    private sealed class NodeStore : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _provider;

        private NodeStore(string directory, ServiceProvider provider)
        {
            _directory = directory;
            _provider = provider;
            Factory = provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
        }

        public IDbContextFactory<NodeLocalSearchDbContext> Factory { get; }

        public NodeLocalSearchDbContext CreateContext() => Factory.CreateDbContext();

        public static async Task<NodeStore> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "t294s3b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var connection = $"Data Source={Path.Combine(directory, "local-node.db")};Pooling=False";
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalSearchDbContext>(options => options.UseSqlite(connection,
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalSearchDbContext.MigrationsHistoryTableName)));
            services.AddDbContextFactory<NodeLocalInstallationIdentityDbContext>(options => options.UseSqlite(connection,
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName)));
            var provider = services.BuildServiceProvider();
            await using (var search = await provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>().CreateDbContextAsync())
                await search.Database.MigrateAsync();
            await using (var identity = await provider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>().CreateDbContextAsync())
                await identity.Database.MigrateAsync();
            return new NodeStore(directory, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
