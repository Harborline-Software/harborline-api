using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

/// <summary>
/// Card #3384 — a selected-session KG search must not inherit the desktop canonical-principal read clip.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finding as an experiment.</b> The real <see cref="KgSearchRoutes"/> delegate resolves every
/// unfenced request through <see cref="CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal"/>, whose
/// <c>os:&lt;user&gt;</c> identity is legitimate desktop attestation. It is not a hosted-web member. This
/// fixture gives the operator and two selected-session members three different record grants, then invokes
/// the real GET endpoint under the exact ambient web-plane signal the listener opens. On unfenced main,
/// both members receive the operator-only row because the route ignores that signal.
/// </para>
/// <para>
/// <b>Why empty rather than 403.</b> KG search already defines an empty result as the indistinguishable
/// fail-closed shape for "no grant" and "no match". MTW-2 must stop OS-principal inheritance but must not
/// resolve member permissions (MTW-3), so a selected-session request returns that existing empty clip.
/// The desktop request still traverses the real clipped service and returns only the operator's row.
/// </para>
/// <para>
/// <b>Consumer pin.</b> Listener tests separately pin that a selected-session request opens
/// <see cref="NodeCallerAttributionScope"/> for GETs. These tests pin this consumer's own obligation by
/// executing the actual mapped GET delegate with two distinct selected principals under that scope.
/// Neutering the route fence makes the web test fail on the whole-body forbidden-row scan while the desktop
/// positive control stays green. A blanket empty implementation makes the desktop control fail.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3384")]
public sealed class KgSearchWebPlaneIsolationTests
{
    private const string Query = "constellation";
    private const string OperatorRecordId = "operator-only-record";
    private const string MemberARecordId = "member-a-only-record";
    private const string MemberBRecordId = "member-b-only-record";

    private static readonly TeamId ActiveTeam =
        new(Guid.Parse("33840000-0000-0000-0000-0000000000fe"));

    [Fact(DisplayName =
        "3384: two selected-session members with different grants receive an empty KG clip, never the " +
        "desktop OS operator's rows")]
    public async Task SelectedSessionMembers_DoNotReceiveTheOperatorClip()
    {
        await using var fixture = await Fixture.CreateAsync();

        var memberAResponse = await fixture.SearchAsMemberAsync(
            CreatePrincipal(
                "a",
                fixture.TenantId,
                "principal-kg-member-a",
                "party-kg-member-a",
                "grant-kg-member-a",
                ownerVersion: 2));
        AssertEmptyWebClip(memberAResponse);

        var memberBResponse = await fixture.SearchAsMemberAsync(
            CreatePrincipal(
                "b",
                fixture.TenantId,
                "principal-kg-member-b",
                "party-kg-member-b",
                "grant-kg-member-b",
                ownerVersion: 7));
        AssertEmptyWebClip(memberBResponse);
    }

    [Fact(DisplayName =
        "294 s2b: a desktop-plane KG search is decided on the canonical tenant principal — the fix is " +
        "not a blanket empty result")]
    public async Task DesktopPlane_SearchStillReturnsTheOperatorClip()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.SearchAsDesktopOperatorAsync();

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains(OperatorRecordId, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberARecordId, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberBRecordId, response.Body, StringComparison.Ordinal);
        Assert.Equal(new[] { OperatorRecordId }, ReadRecordIds(response.Body));
    }

    private static void AssertEmptyWebClip(RouteResponse response)
    {
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);

        // Whole-response scans come FIRST so a borrowed row fails on the assertion that names the leak,
        // rather than being shadowed by the narrower empty-array assertion (bug-20260729-81b7ff68).
        Assert.DoesNotContain(OperatorRecordId, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberARecordId, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberBRecordId, response.Body, StringComparison.Ordinal);
        Assert.Empty(ReadRecordIds(response.Body));
    }

    private static string[] ReadRecordIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Select(hit => hit.GetProperty("recordId").GetString()!)
            .ToArray();
    }

    private static SelectedSessionRequestPrincipal CreatePrincipal(
        string suffix,
        TenantId tenantId,
        string principalId,
        string partyId,
        string grantId,
        long ownerVersion) =>
        new(
            accountId: $"account-kg-member-{suffix}",
            tenantId: tenantId,
            principalUserId: new PrincipalUserId(principalId),
            canonicalParty: new CanonicalPartyReference(partyId),
            membershipId: $"membership-kg-member-{suffix}",
            membershipOwnerVersion: ownerVersion,
            pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion(grantId, ownerVersion)],
            authorizationEpoch: ownerVersion,
            sessionCorrelationId: $"session-kg-member-{suffix}",
            coordinationCorrelationId: $"coordination-kg-member-{suffix}");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SearchTestStore _searchStore;
        private readonly ServiceProvider _requestServices;
        private readonly ServiceProvider _teamServices;
        private readonly RouteEndpoint _endpoint;

        private Fixture(
            TenantId tenantId,
            SearchTestStore searchStore,
            ServiceProvider requestServices,
            ServiceProvider teamServices,
            RouteEndpoint endpoint)
        {
            TenantId = tenantId;
            _searchStore = searchStore;
            _requestServices = requestServices;
            _teamServices = teamServices;
            _endpoint = endpoint;
        }

        internal TenantId TenantId { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var tenantId = ActiveTeamTenantContext.ProjectTenantId(ActiveTeam);
            // The real desktop route receives this value from the current node's roster edge. It is
            // intentionally unlike an OS spelling, so this search-read assertion fails if the route mints one.
            var operatorPrincipal = new ActorId("canonical-tenant-principal-3384");

            var searchStore = await SearchTestStore.CreateAsync();
            var indexer = new NodeSearchIndexer(searchStore.Factory);
            await SeedAsync(indexer, tenantId, OperatorRecordId, "Operator constellation");
            await SeedAsync(indexer, tenantId, MemberARecordId, "Member A constellation");
            await SeedAsync(indexer, tenantId, MemberBRecordId, "Member B constellation");

            // Three genuinely different clips. The selected principals' own clips are deliberately not
            // resolved by this MTW-2 card; their presence makes the inherited-operator result observable.
            var grants = TestInMemoryAuthorizationStores.GrantStore();
            await SaveGrantAsync(grants, tenantId, operatorPrincipal, OperatorRecordId);
            await SaveGrantAsync(grants, tenantId, new ActorId("principal-kg-member-a"), MemberARecordId);
            await SaveGrantAsync(grants, tenantId, new ActorId("principal-kg-member-b"), MemberBRecordId);

            var readService = new NodeSearchReadService(
                searchStore.Factory,
                TestSearchAuthorization.Projection(grants));
            var teamServices = new ServiceCollection().BuildServiceProvider();
            var activeTeam = new FixedActiveTeamAccessor(
                new TeamContext(ActiveTeam, "KG search web-plane isolation", teamServices, TimeProvider.System));

            var requestServices = new ServiceCollection()
                .AddLogging()
                .AddRouting()
                .BuildServiceProvider();
            var routes = new TestEndpointRouteBuilder(requestServices);
            KgSearchRoutes.Map(
                routes.MapDeviceReachableProductDataGroup(),
                readService,
                activeTeam,
                () => operatorPrincipal,
                TimeProvider.System);

            var endpoint = routes.DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(candidate =>
                    candidate.RoutePattern.RawText == $"{KgSearchRoutes.RouteBase}/search");

            return new Fixture(tenantId, searchStore, requestServices, teamServices, endpoint);
        }

        internal async Task<RouteResponse> SearchAsMemberAsync(
            SelectedSessionRequestPrincipal principal)
        {
            using var scope = NodeCallerAttributionScope.Enter(NodeCallerAttribution.From(principal));
            return await InvokeSearchAsync(principal);
        }

        internal Task<RouteResponse> SearchAsDesktopOperatorAsync() => InvokeSearchAsync(null);

        public async ValueTask DisposeAsync()
        {
            await _requestServices.DisposeAsync();
            await _teamServices.DisposeAsync();
            await _searchStore.DisposeAsync();
        }

        private async Task<RouteResponse> InvokeSearchAsync(SelectedSessionRequestPrincipal? principal)
        {
            await using var body = new MemoryStream();
            var context = new DefaultHttpContext
            {
                RequestServices = _requestServices,
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = $"{KgSearchRoutes.RouteBase}/search";
            context.Request.QueryString = new QueryString($"?q={Query}");
            context.Response.Body = body;
            context.SetEndpoint(_endpoint);
            if (principal is null)
            {
                context.Features.Set(DesktopPlaneRequestFeature.Instance);
            }
            else
            {
                context.Features.Set(principal);
            }

            await _endpoint.RequestDelegate!(context);

            body.Position = 0;
            using var reader = new StreamReader(body);
            return new RouteResponse(context.Response.StatusCode, await reader.ReadToEndAsync());
        }

        private static async Task SeedAsync(
            NodeSearchIndexer indexer,
            TenantId tenantId,
            string recordId,
            string title)
        {
            await indexer.IndexNodeAsync(new SearchNodeRow
            {
                RecordId = recordId,
                TenantId = tenantId.ToString(),
                NodeType = "isolation-probe",
                Title = title,
                Body = Query,
                Residency = SearchResidency.Cache,
            });
        }

        private static async Task SaveGrantAsync(
            InMemoryGrantStore grants,
            TenantId tenantId,
            ActorId principalId,
            string recordId)
        {
            var grant = TestSearchAuthorization.Grant(tenantId, principalId,
                ScopeExpression.Parse($"/records/{recordId}"), DateTimeOffset.UnixEpoch);
            await grants.SaveAsync(tenantId, grant, expectedOwnerVersion: 0);
        }
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider)
        : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed record RouteResponse(int StatusCode, string Body);

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
