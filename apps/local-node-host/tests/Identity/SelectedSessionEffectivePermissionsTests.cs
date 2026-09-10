using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using KernelTeamId = Harborline.Api.Kernel.Runtime.Teams.TeamId;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Card 3344 / ticket 293 slice 5: the renderer read is the GATE's answer for the session's principal.
/// <para>
/// Slice 5 removed the route's roster fallback. The selected-session branch returns the request-scoped PEP
/// snapshot the selected-cookie accept bound — grants decided by <c>AuthorizationGate</c>, the roster edge
/// contributing membership and ejection only — and the desktop bootstrap-bearer branch answers the CHAIN
/// ROOT's own floor, the same rule the replicated rebuild applies. Neither reads a permission set off the
/// roster, which since slice 3b2 a replicated member does not have.
/// </para>
/// </summary>
[Trait("PlanCard", "MTW-2-3344")]
public sealed class SelectedSessionEffectivePermissionsTests
{
    private const string FounderParty = "party-founder";
    private const string MemberPrincipal = "principal-member";
    private static readonly Guid TeamId =
        Guid.Parse("33440000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName =
        "293 s5: the response follows the CONFERRED GRANT, not the signed roster edge")]
    public async Task Response_Follows_The_Grant_Not_The_Roster_Edge()
    {
        // The roster edge is signed with the full member composition; the administrator then NARROWED the
        // member's conferred grant (ticket 362's one-transaction narrowing) to assets:read alone. The two
        // now disagree, which is the whole point: the answer must be the grant's.
        var fixture = await SessionFixture.CreateAsync(
            signedEdge: PermissionCompositions.Member,
            conferred: PermissionSet.Of("assets:read"));

        var wide = await fixture.ReadAsync();
        Assert.Equal(["assets:read"], wide);

        // Narrow it again, with no roster change at all. The response tracks the grant both times.
        fixture.NarrowConferredGrant(PermissionSet.Empty);
        var narrowed = await fixture.ReadAsync();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, narrowed.StatusCode);
        Assert.Empty(narrowed);

        // And the roster edge it ignored really does hold more than either answer.
        Assert.True(
            PermissionCompositions.Member.Contains("forms:fill")
            || PermissionCompositions.Member.Permissions.Count > 1,
            "the fixture's signed edge must be wider than the conferred grant for this test to mean anything");
    }

    [Fact(DisplayName =
        "293 s5: with no bound PEP snapshot the route answers 'unresolved', never the roster")]
    public async Task No_Bound_Context_Is_Unresolved_Never_The_Roster()
    {
        // The pre-slice-5 route fell back to roster.PermissionsOf(partyId) here, and then to a hardcoded
        // member composition. Both invent an answer: the roster carries no set for a replicated member, and
        // a composition this node never granted is not a permission statement. "We cannot answer" is.
        var fixture = await SessionFixture.CreateAsync(
            signedEdge: PermissionCompositions.Admin,
            conferred: PermissionSet.Of("assets:read"));

        var body = await fixture.ReadAsync(bindContext: false);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, body.StatusCode);
        Assert.Empty(body);
    }

    [Fact(DisplayName = "The permissions route refuses a request without a selected session")]
    public async Task Missing_Selected_Session_Is_Refused()
    {
        var roster = new MutableRosterReader(GenesisRoster());
        var context = Context();

        await (await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // The desktop bootstrap-bearer branch answers about the active roster's CHAIN ROOT and nobody else.
    // These pin what the branch may and may not do.

    [Fact(DisplayName =
        "293 s5: a bootstrap-bearer desktop caller gets the CHAIN ROOT floor, not a roster set read")]
    public async Task Bootstrap_Bearer_Gets_The_Chain_Root_Floor()
    {
        var roster = new MutableRosterReader(GenesisRoster());
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(TeamId)));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        // The chain root's authority is its genesis self-admission, the same rule the replicated rebuild
        // applies. It is the root FLOOR, not a read of the member's signed set.
        Assert.Equal(
            PermissionCompositions.Owner.Permissions.Order(StringComparer.Ordinal),
            permissions.Order(StringComparer.Ordinal));
        Assert.Contains(TeamRolePermissions.MembersManage, permissions);
    }

    [Fact(DisplayName =
        "A desktop caller whose genesis Party is somehow absent gets no baseline")]
    public async Task Roster_Absent_Bootstrap_Caller_Gets_Nothing()
    {
        // A blank genesis party is a broken install, not a deferred admission; handing it any set would
        // invent access from a fault.
        var roster = new MutableRosterReader(MemberRoster.Empty());
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(TeamId)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Empty(permissions);
    }

    [Fact(DisplayName =
        "A bootstrap-bearer caller with no active team is refused rather than defaulted")]
    public async Task Bootstrap_Bearer_Without_Active_Team_Is_Refused()
    {
        var roster = new MutableRosterReader(GenesisRoster());
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);

        // Active is null — there is no tenant to project, and the branch must not fall back to a
        // configured, first, or well-known team. Refusal is the only correct answer.
        await (await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(null)))
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact(DisplayName =
        "A bootstrap-bearer caller on the bound web plane is refused rather than using desktop permissions")]
    public async Task Bootstrap_Bearer_On_Bound_Web_Plane_Is_Refused()
    {
        var roster = new MutableRosterReader(GenesisRoster());
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);
        using var scope = NodeCallerAttributionScope.Enter(new NodeCallerAttribution(
            MemberPartyId: MemberPrincipal,
            MembershipId: "membership-member",
            MembershipOwnerVersion: 1,
            SessionCorrelationId: "session-member",
            CoordinationCorrelationId: "coordination-member",
            AuthorizationEpoch: 1));

        await (await SelectedSessionIdentityRoutes.PermissionsAsync(
            context,
            roster,
            ActiveTeam(TeamId))).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact(DisplayName =
        "A bootstrap-bearer caller cannot name the Party whose permissions it receives")]
    public async Task Bootstrap_Bearer_Cannot_Choose_Its_Party()
    {
        var member = KeyPair.Generate();
        var founder = KeyPair.Generate();
        var verifier = new Ed25519Verifier();
        var signer = new Ed25519Signer(founder);
        // The member's signed edge is the narrow member composition; genesis holds the owner floor.
        var roster = new MutableRosterReader(MemberRoster
            .Genesis(TeamId, FounderParty, signer, verifier, Now, Guid.NewGuid())
            .Admit(
                FounderParty, signer, MemberPrincipal, member.PrincipalId,
                PermissionCompositions.Member, verifier, Now, Guid.NewGuid()));
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);
        // Every channel a caller controls, asserting the member Party rather than the chain root.
        context.Request.QueryString = new QueryString($"?partyId={MemberPrincipal}&role=member");
        context.Request.Headers["X-Party-Id"] = MemberPrincipal;
        context.Request.Headers.Cookie = $"party={MemberPrincipal}";

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(TeamId)));

        // The chain root's floor, not the member's — the request said "member" in three places and the node
        // ignored all three.
        Assert.Contains(TeamRolePermissions.MembersManage, permissions);
        Assert.Equal(
            PermissionCompositions.Owner.Permissions.Order(StringComparer.Ordinal),
            permissions.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// One selected session over the REAL PEP: a signed roster edge, a conferred install-root grant the gate
    /// decides on, and the request-scoped <see cref="SelectedSessionTenantContext"/> the route reads.
    /// </summary>
    private sealed class SessionFixture
    {
        private readonly SelectedSessionRequestPrincipal _principal;
        private readonly ISelectedSessionPermissionResolver _resolver;
        private readonly MutableRosterReader _roster;
        private readonly Dictionary<string, PermissionSet> _conferred;

        private SessionFixture(
            SelectedSessionRequestPrincipal principal,
            ISelectedSessionPermissionResolver resolver,
            MutableRosterReader roster,
            Dictionary<string, PermissionSet> conferred)
        {
            _principal = principal;
            _resolver = resolver;
            _roster = roster;
            _conferred = conferred;
        }

        internal static async Task<SessionFixture> CreateAsync(
            PermissionSet signedEdge, PermissionSet conferred)
        {
            var founder = KeyPair.Generate();
            var member = KeyPair.Generate();
            var verifier = new Ed25519Verifier();
            var signer = new Ed25519Signer(founder);
            var roster = MemberRoster
                .Genesis(TeamId, FounderParty, signer, verifier, Now, Guid.NewGuid())
                .Admit(
                    FounderParty, signer, MemberPrincipal, member.PrincipalId, signedEdge,
                    verifier, Now, Guid.NewGuid());

            var tenant = new TenantId(TeamId.ToString("D"));
            var grants = TestInMemoryAuthorizationStores.GrantStore();
            var grantId = GrantId.New();
            await grants.SaveAsync(tenant, MemberGrant(grantId, tenant), expectedOwnerVersion: 0);

            var held = new Dictionary<string, PermissionSet>(StringComparer.Ordinal)
            {
                [MemberPrincipal] = conferred,
            };
            var rosterReader = new MutableRosterReader(roster);
            var resolver = new SelectedSessionPermissionResolver(
                rosterReader,
                grants,
                new FixedEpochReader(),
                new FixedTimeProvider(Now),
                NullLogger<SelectedSessionPermissionResolver>.Instance,
                TestAuthorization.ConferredGate(actor =>
                    held.TryGetValue(actor.Value, out var set) ? set : PermissionSet.Empty));

            return new SessionFixture(
                new SelectedSessionRequestPrincipal(
                    "account-member",
                    tenant,
                    new PrincipalUserId(MemberPrincipal),
                    new CanonicalPartyReference("people-party-member"),
                    "membership-member",
                    1,
                    [new PinnedGrantOwnerVersion(grantId.ToString(), 1)],
                    1,
                    "session-correlation",
                    "coordination-correlation"),
                resolver,
                rosterReader,
                held);
        }

        internal void NarrowConferredGrant(PermissionSet narrower) => _conferred[MemberPrincipal] = narrower;

        internal async Task<ResponseBody> ReadAsync(bool bindContext = true)
        {
            var services = new ServiceCollection().AddLogging().AddRouting();
            if (bindContext)
            {
                var bound = new SelectedSessionTenantContext(_resolver);
                await bound.BindAsync(_principal);
                services.AddSingleton(bound);
            }
            var provider = services.BuildServiceProvider();
            var context = new DefaultHttpContext
            {
                RequestServices = provider,
                Response = { Body = new MemoryStream() },
            };
            context.Request.Headers.Cookie = $"{WebSessionCookieNames.Selected}=selected-handle";
            context.Features.Set(_principal);

            var result = await SelectedSessionIdentityRoutes.PermissionsAsync(context, _roster);
            await result.ExecuteAsync(context);
            return new ResponseBody(context.Response.StatusCode, await PermissionsOfBodyAsync(context));
        }

        private static AccessGrant MemberGrant(GrantId id, TenantId tenant) => new(
            id, tenant, new ActorId(MemberPrincipal), AccessGrantAuthorizationSeed.MemberRole,
            ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(Now - TimeSpan.FromHours(1)), GranterKind.Person,
            new ActorId("principal-founder"), Now - TimeSpan.FromHours(1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                new ActorId("principal-founder")), Now - TimeSpan.FromHours(1));
    }

    /// <summary>The response's status plus its permission list, so one assertion can read both.</summary>
    private sealed class ResponseBody(int statusCode, IReadOnlyList<string> permissions)
        : List<string>(permissions)
    {
        internal int StatusCode { get; } = statusCode;
    }

    private static async Task<IReadOnlyList<string>> PermissionsOfBodyAsync(DefaultHttpContext context)
    {
        if (context.Response.StatusCode != StatusCodes.Status200OK) return [];
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("effectivePermissions")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
    }

    private static MemberRoster GenesisRoster()
    {
        var founder = KeyPair.Generate();
        return MemberRoster.Genesis(
            TeamId,
            FounderParty,
            new Ed25519Signer(founder),
            new Ed25519Verifier(),
            Now,
            Guid.NewGuid());
    }

    private static StubActiveTeamAccessor ActiveTeam(Guid? teamId) => new(teamId);

    private static async Task<IReadOnlyList<string>> ReadBodyAsync(
        DefaultHttpContext context,
        IResult result)
    {
        await result.ExecuteAsync(context);
        return await PermissionsOfBodyAsync(context);
    }

    private sealed class FixedEpochReader : ISelectedSessionAuthorizationEpochReader
    {
        public Task<long?> ReadAsync(
            TenantId tenantId, string principalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(1);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubActiveTeamAccessor : IActiveTeamAccessor
    {
        private readonly TeamContext? _active;

        internal StubActiveTeamAccessor(Guid? teamId) =>
            _active = teamId is null
                ? null
                : new TeamContext(
                    KernelTeamId.Parse(teamId.Value.ToString("D")),
                    "stub",
                    new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

        public TeamContext? Active => _active;

        public Task SetActiveAsync(KernelTeamId teamId, CancellationToken ct) =>
            throw new NotSupportedException();

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged
        {
            add { }
            remove { }
        }
    }

    private static DefaultHttpContext Context()
    {
        var services = new ServiceCollection().AddLogging().AddRouting();
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
    }

    private sealed class MutableRosterReader(MemberRoster current) : IVerifiedTenantRosterReader
    {
        internal MemberRoster Current { get; set; } = current;

        public Task<MemberRoster> ReadAsync(
            TenantId tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }
}
