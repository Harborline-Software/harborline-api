using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

using KernelTeamId = Harborline.Api.Kernel.Runtime.Teams.TeamId;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Card 3344: the renderer read is driven by a selected session's real canonical Party and the
/// live roster, not a role copied into the session or a client-side founder default.
/// </summary>
[Trait("PlanCard", "MTW-2-3344")]
public sealed class SelectedSessionEffectivePermissionsTests
{
    private const string FounderParty = "party-founder";
    private const string MemberParty = "party-member";
    private static readonly Guid TeamId =
        Guid.Parse("33440000-0000-0000-0000-000000000001");

    [Fact(DisplayName =
        "A non-founder sees members:manage only while the live roster grants it")]
    public async Task NonFounder_Response_Follows_Live_Roster_Permissions()
    {
        var founder = KeyPair.Generate();
        var member = KeyPair.Generate();
        var verifier = new Ed25519Verifier();
        var roster = MemberRoster.Genesis(
                TeamId,
                FounderParty,
                new Ed25519Signer(founder),
                verifier,
                DateTimeOffset.UtcNow,
                Guid.NewGuid())
            .Admit(
                FounderParty,
                new Ed25519Signer(founder),
                MemberParty,
                member.PrincipalId,
                PermissionCompositions.Member,
                verifier,
                DateTimeOffset.UtcNow,
                Guid.NewGuid());
        var liveRoster = new MutableRosterReader(roster);

        var withoutManage = await ReadPermissionsAsync(liveRoster);
        Assert.DoesNotContain(TeamRolePermissions.MembersManage, withoutManage);

        liveRoster.Current = roster.Grant(
            FounderParty,
            MemberParty,
            PermissionCompositions.Member.With(TeamRolePermissions.MembersManage));

        var withManage = await ReadPermissionsAsync(liveRoster);
        Assert.Contains(TeamRolePermissions.MembersManage, withManage);
    }

    [Fact(DisplayName = "The permissions route refuses a request without a selected session")]
    public async Task Missing_Selected_Session_Is_Refused()
    {
        var founder = KeyPair.Generate();
        var roster = new MutableRosterReader(MemberRoster.Genesis(
            TeamId,
            FounderParty,
            new Ed25519Signer(founder),
            new Ed25519Verifier(),
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));
        var context = Context();

        await (await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact(DisplayName =
        "A grant-anchored web member absent from the signed roster still resolves the member baseline")]
    public async Task Roster_Absent_Selected_Member_Gets_The_Member_Baseline()
    {
        // The population the invite flow creates. A web invitee holds an account, a Party binding, a
        // live grant and an Active membership, but the signed atlas admission is deferred to first
        // wire enrollment — and the bridge that performs it runs only from the device-pairing path.
        // Accept an invitation in a browser, never pair a device, and you are absent from the roster
        // permanently. Answering [] for them blanks all 27 destinations.
        var roster = new MutableRosterReader(GenesisRoster());
        var context = Context();
        context.Request.Headers.Cookie = $"{WebSessionCookieNames.Selected}=selected-handle";
        context.Features.Set(new SelectedSessionRequestPrincipal(
            "account-web-member",
            new TenantId(TeamId.ToString("D")),
            new PrincipalUserId("principal-web-member"),
            new CanonicalPartyReference("party-not-in-roster"),
            "membership-web-member",
            1,
            [new PinnedGrantOwnerVersion("grant-web-member", 1)],
            1,
            "session-correlation",
            "coordination-correlation"));

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        // Everyday work, and nothing from the build or admin groups.
        Assert.Contains("inbox:read", permissions);
        Assert.Contains("calendar:read", permissions);
        Assert.DoesNotContain("members:manage", permissions);
        Assert.DoesNotContain("forms:design", permissions);
    }

    [Fact(DisplayName =
        "A desktop caller whose genesis Party is somehow absent gets no baseline")]
    public async Task Roster_Absent_Bootstrap_Caller_Gets_Nothing()
    {
        // The baseline is for a SELECTED session only. A bootstrap-bearer caller resolves the genesis
        // Party, so a genesis-absent roster is a broken install, not a deferred admission — handing it
        // the member baseline would invent access from a fault.
        var roster = new MutableRosterReader(MemberRoster.Empty());
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(TeamId)));

        Assert.Empty(permissions);
    }

    // The desktop bootstrap-bearer branch resolves the active roster's GENESIS party. Until it
    // existed, "a bearer-only caller cannot read this surface" was true by construction and needed
    // no test; now it is a guard, and a guard nobody tests is a guard nobody notices losing. These
    // three pin what the branch may and may not do.

    [Fact(DisplayName =
        "A bootstrap-bearer desktop caller resolves the active roster's genesis Party")]
    public async Task Bootstrap_Bearer_Resolves_Genesis_Party()
    {
        var roster = new MutableRosterReader(GenesisRoster());
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(TeamId)));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotEmpty(permissions);
        Assert.Contains(TeamRolePermissions.MembersManage, permissions);
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
            MemberPartyId: MemberParty,
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
        var roster = new MutableRosterReader(MemberRoster.Genesis(
                TeamId, FounderParty, new Ed25519Signer(founder), verifier,
                DateTimeOffset.UtcNow, Guid.NewGuid())
            .Admit(
                FounderParty, new Ed25519Signer(founder), MemberParty, member.PrincipalId,
                PermissionCompositions.Member, verifier, DateTimeOffset.UtcNow, Guid.NewGuid()));
        var context = Context();
        context.Features.Set(SharedHostedWebApp.BootstrapBearerRequestPrincipal.Instance);
        // Every channel a caller controls, asserting the member Party rather than genesis.
        context.Request.QueryString = new QueryString($"?partyId={MemberParty}&role=member");
        context.Request.Headers["X-Party-Id"] = MemberParty;
        context.Request.Headers.Cookie = $"party={MemberParty}";

        var permissions = await ReadBodyAsync(
            context,
            await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster, ActiveTeam(TeamId)));

        // Genesis permissions, not the member's — the request said "member" in three places and the
        // node ignored all three.
        Assert.Contains(TeamRolePermissions.MembersManage, permissions);
    }

    private static MemberRoster GenesisRoster()
    {
        var founder = KeyPair.Generate();
        return MemberRoster.Genesis(
            TeamId,
            FounderParty,
            new Ed25519Signer(founder),
            new Ed25519Verifier(),
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
    }

    private static StubActiveTeamAccessor ActiveTeam(Guid? teamId) => new(teamId);

    private static async Task<IReadOnlyList<string>> ReadBodyAsync(
        DefaultHttpContext context,
        IResult result)
    {
        await result.ExecuteAsync(context);
        if (context.Response.StatusCode != StatusCodes.Status200OK) return [];
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("permissions")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
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

    private static async Task<IReadOnlyList<string>> ReadPermissionsAsync(MutableRosterReader roster)
    {
        var context = Context();
        context.Request.Headers.Cookie = $"{WebSessionCookieNames.Selected}=selected-handle";
        context.Features.Set(new SelectedSessionRequestPrincipal(
            "account-member",
            new TenantId(TeamId.ToString("D")),
            new PrincipalUserId("principal-member"),
            new CanonicalPartyReference(MemberParty),
            "membership-member",
            1,
            [new PinnedGrantOwnerVersion("grant-member", 1)],
            1,
            "session-correlation",
            "coordination-correlation"));

        await (await SelectedSessionIdentityRoutes.PermissionsAsync(context, roster)).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("permissions")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
    }

    private static DefaultHttpContext Context()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddRouting()
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
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
