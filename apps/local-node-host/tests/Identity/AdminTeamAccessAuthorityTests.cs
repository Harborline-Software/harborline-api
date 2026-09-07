using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The real server-side gate for #2617 — exercised end-to-end against SQLite session/identity/grant
/// stores and a signed roster. Proves the members:manage gate refuses a non-admin on EVERY action
/// (never a UI-only gate), that the member list unions signed-roster members with grant-anchored web
/// members (Option A), and that revocation is grant revocation with a self-lockout guard.
/// </summary>
public sealed class AdminTeamAccessAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AdminGrantId = "22222222-2222-2222-2222-222222222222";
    private const string WebGrantId = "55555555-5555-5555-5555-555555555555";
    private const string ThirdGrantId = "77777777-7777-7777-7777-777777777777";

    internal static async Task<DateTimeOffset> RunKernelClockIntegrationAsync(
        TimeProvider timeProvider,
        DateTimeOffset admittedAt)
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin, timeProvider);
        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle,
            TenantId,
            WebGrantId,
            new AuthorizationWriteContext(
                new ActorId("principal-admin"),
                new TenantId(TenantId),
                admittedAt));
        Assert.Equal(AdminRevokeMemberStatus.Revoked, result?.Status);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        var row = await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId);
        return DateTimeOffset.FromUnixTimeMilliseconds(row.RevokedAtUnixMs!.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdminSite_RecordsTheGateDecisionWithRosterInputs(bool allowed)
    {
        using var capture = new RosterDecisionCapture();
        await using var fixture = await Fixture.CreateAsync(allowed ? PermissionCompositions.Admin : PermissionCompositions.Member, refusalAudit: capture.Audit);
        Assert.Equal(allowed, await fixture.Authority.ListMembersAsync(fixture.Handle, TenantId) is not null);
        Assert.Equal("party-admin", capture.AssertSingle(allowed).Roster!.PartyId);
        await capture.AssertAuditAsync(new TenantId(TenantId));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task NonAdmin_Caller_Is_Refused_On_Every_Action_By_The_Server_Gate()
    {
        // The caller's roster permissions lack members:manage — the UI could be bypassed, but the
        // server gate refuses regardless. Every surface returns a non-enumerating null.
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Member);

        Assert.Null(await fixture.Authority.ListMembersAsync(fixture.Handle, TenantId));
        Assert.Null(await fixture.Authority.ListPendingInvitationsAsync(fixture.Handle, TenantId));
        Assert.Null(await fixture.Authority.IssueInvitationAsync(
            fixture.Handle, TenantId, new[] { "records:read" }, "idem-1"));
        Assert.Null(await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, WebGrantId));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Unknown_Session_And_CrossTenant_Are_Refused()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);

        Assert.Null(await fixture.Authority.ListMembersAsync("unknown-handle", TenantId));
        Assert.Null(await fixture.Authority.ListMembersAsync(
            fixture.Handle, "99999999-9999-9999-9999-999999999999"));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Admin_Member_List_Unions_Roster_With_GrantAnchored_Web_Members()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);

        var result = await fixture.Authority.ListMembersAsync(fixture.Handle, TenantId);

        Assert.NotNull(result);
        var byParty = result!.Members.ToDictionary(m => m.PartyId);
        // Signed roster member (the admin), surfaced from the roster plane.
        Assert.Equal(TeamMemberSource.Roster, byParty["party-admin"].Source);
        // Grant-anchored web member: roster-ABSENT but grant-valid (Option A) — surfaced from the grant.
        Assert.True(byParty.ContainsKey("party-web"));
        Assert.Equal(TeamMemberSource.Grant, byParty["party-web"].Source);
        Assert.Equal(WebGrantId, byParty["party-web"].GrantId);
        // No party is double-counted across the two planes.
        Assert.Equal(result.Members.Count, byParty.Count);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Admin_Displays_Live_Closure_Capabilities_Without_Legacy_Grant_Json()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        // members:manage is offered to Administrator alone, so the projected capability follows the ROLE
        // the grant confers rather than any stored bundle.
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        var result = await fixture.Authority.ListMembersAsync(fixture.Handle, TenantId);

        var webMember = Assert.Single(result!.Members, member => member.PartyId == "party-web");
        Assert.Equal(["members:manage"], webMember.Capabilities);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Admin_Revokes_A_GrantAnchored_Member_Via_Grant_Revocation()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);

        var result = await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, WebGrantId);

        Assert.NotNull(result);
        Assert.Equal(AdminRevokeMemberStatus.Revoked, result!.Status);
        await using (var grants = fixture.GrantFactory.CreateDbContext())
        {
            var row = await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId);
            Assert.NotNull(row.RevokedAtUnixMs);
        }

        // The revoked member no longer appears in the member list.
        var after = await fixture.Authority.ListMembersAsync(fixture.Handle, TenantId);
        Assert.DoesNotContain(after!.Members, m => m.PartyId == "party-web");
    }

    [Fact]
    public async Task Admin_Revocation_Carries_One_Gate_Decision_To_All_Four_Write_Boundaries()
    {
        var captures = new BoundaryCaptures();
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin, captures: captures);

        var result = await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, WebGrantId);

        Assert.Equal(AdminRevokeMemberStatus.Revoked, result?.Status);
        var sentinel = Assert.Single(captures.GrantWriterDecisions);
        Assert.Same(sentinel, Assert.Single(captures.RosterWriterDecisions));
        Assert.Same(sentinel, Assert.Single(captures.GrantAudit.Decisions));
        Assert.Same(sentinel, Assert.Single(captures.RosterAudit.Decisions));
    }

    [Fact]
    public async Task Missing_Canonical_Party_Revokes_Grant_With_The_Same_Audited_Decision()
    {
        var captures = new BoundaryCaptures();
        await using var fixture = await Fixture.CreateAsync(
            PermissionCompositions.Admin, captures: captures, omitTargetParty: true);

        var result = await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, WebGrantId);

        Assert.Equal(AdminRevokeMemberStatus.Revoked, result?.Status);
        var decision = Assert.Single(captures.GrantWriterDecisions);
        Assert.Empty(captures.RosterWriterDecisions);
        Assert.Equal(AuditEventType.CapabilityRevoked,
            Assert.Single(captures.GrantAudit.Records).EventType);
        Assert.Same(decision, Assert.Single(captures.GrantAudit.Decisions));
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.NotNull((await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
    }

    [Fact]
    public async Task Grant_Revocation_Writer_Validates_Decision_Against_The_Raw_Write()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        var writer = new AuthorizedGrantRevocationWriter(new NodeEfGrantStore(fixture.GrantFactory));
        var tenant = new TenantId(TenantId);
        var grant = new GrantId(Guid.Parse(WebGrantId));
        var wrongTarget = TestAuthorization.AllowedDecision(
            tenant, AdminGrantId, "members", TeamRolePermissions.MembersManage, "principal-admin", Now);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.RevokeAsync(
            tenant, grant,
            new GrantRevocation(new ActorId("principal-admin"), Now,
                new GrantReason(GrantReasonCodes.RevocationOffboarding, WebGrantId)),
            wrongTarget));

        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.Null((await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Admin_Cannot_Revoke_The_Grant_Backing_Their_Own_Session()
    {
        // Harborline card 3792: this always-on real AdminTeamAccessAuthority test is the guard gate;
        // no permanently-skipped duplicate red fixture is needed.
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);

        var result = await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, AdminGrantId);

        Assert.NotNull(result);
        Assert.Equal(AdminRevokeMemberStatus.SelfRevocationRefused, result!.Status);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        var row = await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == AdminGrantId);
        Assert.Null(row.RevokedAtUnixMs);
    }

    /// <summary>
    /// Ledger L619 — <c>not_last_administrator()</c>: revoking the only Administrator in force is refused
    /// with the refusal status and a permanent refusal audit row, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Revoking_The_Last_Administrator_In_Force_Is_Refused_And_Audited()
    {
        var captures = new BoundaryCaptures();
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin, captures: captures);
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, WebGrantId);

        Assert.Equal(AdminRevokeMemberStatus.LastAdministratorRefused, result?.Status);
        Assert.Empty(captures.GrantWriterDecisions);
        Assert.Empty(captures.RosterWriterDecisions);
        Assert.Equal(new AuditEventType("CapabilityRevocationRefused"),
            Assert.Single(captures.GrantAudit.Records).EventType);
        Assert.Single(captures.GrantAudit.Decisions);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.Null((await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
    }

    /// <summary>The same revocation succeeds the moment a second Administrator is in force.</summary>
    [Fact]
    public async Task Revoking_An_Administrator_Succeeds_While_Another_Is_In_Force()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await PromoteToAdministratorAsync(fixture, WebGrantId);
        await PromoteToAdministratorAsync(fixture, AdminGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(fixture.Handle, TenantId, WebGrantId);

        Assert.Equal(AdminRevokeMemberStatus.Revoked, result?.Status);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.NotNull((await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
    }

    /// <summary>
    /// Ledger L618 — the revocation the guard refuses succeeds as a handover: the successor's Administrator
    /// grant and the revocation land together, and both audit legs carry the same correlation id.
    /// </summary>
    [Fact]
    public async Task Handing_Over_The_Last_Administrator_Writes_Both_Legs_Under_One_Correlation()
    {
        var captures = new BoundaryCaptures();
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin, captures: captures);
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-admin");

        Assert.Equal(AdminRevokeMemberStatus.HandedOver, result?.Status);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.NotNull((await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
        var successor = await grants.Grants.AsNoTracking()
            .SingleAsync(g => g.GrantId == result!.SuccessorGrantId);
        Assert.Equal("principal-admin", successor.SubjectId);
        Assert.Equal(RoleReference.Administrator.Name, successor.RoleName);
        Assert.Null(successor.RevokedAtUnixMs);

        var legs = captures.GrantAudit.Records;
        Assert.Equal(
            new[] { AuditEventType.CapabilityDelegated, AuditEventType.CapabilityRevoked },
            legs.Select(record => record.EventType).ToArray());
        var correlations = legs.Select(record => record.Payload.Payload.Body["correlation_id"]).Distinct().ToArray();
        Assert.Single(correlations);
        Assert.All(legs, record => Assert.Equal(
            Guid.Parse(result!.SuccessorGrantId!).ToString("D"),
            record.Payload.Payload.Body["successor_grant_id"]));
    }

    /// <summary>
    /// The successor must be a live principal of this tenant and must not be the outgoing holder. Every
    /// refusal answers the one status, and nothing is written.
    /// </summary>
    [Theory]
    [InlineData("principal-web")]
    [InlineData("principal-nobody")]
    public async Task Handing_Over_To_An_Ineligible_Successor_Is_Refused_And_Writes_Nothing(string successor)
    {
        var captures = new BoundaryCaptures();
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin, captures: captures);
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: successor);

        Assert.Equal(AdminRevokeMemberStatus.SuccessorRefused, result?.Status);
        Assert.Empty(captures.GrantWriterDecisions);
        Assert.Empty(captures.GrantAudit.Records);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.Equal(3, await grants.Grants.AsNoTracking().CountAsync());
        Assert.Null((await grants.Grants.AsNoTracking().SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
    }

    /// <summary>
    /// The outgoing holder is never their own successor — not even when their Administrator grant has
    /// already expired, where the "already an Administrator in force" clause does not catch them and a
    /// handover would mint them a fresh one.
    /// </summary>
    [Fact]
    public async Task Handing_Over_To_The_Outgoing_Holder_Is_Refused_When_Their_Grant_Has_Expired()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await PromoteToAdministratorAsync(fixture, WebGrantId);
        await using (var grants = fixture.GrantFactory.CreateDbContext())
        {
            var row = await grants.Grants.SingleAsync(g => g.GrantId == WebGrantId);
            row.ValidityUntilUnixMs = (Now - TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();
            await grants.SaveChangesAsync();
        }

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-web");

        Assert.Equal(AdminRevokeMemberStatus.SuccessorRefused, result?.Status);
        await using var after = fixture.GrantFactory.CreateDbContext();
        Assert.Equal(3, await after.Grants.AsNoTracking().CountAsync());
    }

    /// <summary>A successor who already holds Administrator in force hands over nothing, so it is refused.</summary>
    [Fact]
    public async Task Handing_Over_To_A_Sitting_Administrator_Is_Refused()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await PromoteToAdministratorAsync(fixture, WebGrantId);
        await PromoteToAdministratorAsync(fixture, AdminGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-admin");

        Assert.Equal(AdminRevokeMemberStatus.SuccessorRefused, result?.Status);
    }

    /// <summary>
    /// Ticket 211 slice 3 - the admin surface reads ONE members:manage. A caller whose signed roster edge
    /// does not carry it, but whose live install-wide Administrator grant does, is admitted; without the
    /// grant the same caller is refused, so the roster edge is not silently ignored either.
    /// </summary>
    [Fact]
    public async Task An_Administrator_Grant_Admits_A_Caller_The_Signed_Roster_Does_Not_Carry()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);

        // party-web is a grant-anchored web member: no roster edge at all, so the roster read alone
        // refused them however they were granted.
        Assert.Null(await fixture.Authority.ListMembersAsync(fixture.WebHandle, TenantId));

        await PromoteToAdministratorAsync(fixture, WebGrantId);

        Assert.NotNull(await fixture.Authority.ListMembersAsync(fixture.WebHandle, TenantId));
    }

    /// <summary>
    /// The other half of the same rule: the signed roster edge is authoritative where it exists, so a
    /// successor the roster carries WITHOUT members:manage is refused rather than handed a role that
    /// would do nothing for them - the surface and the handover read the same thing.
    /// </summary>
    [Fact]
    public async Task Handing_Over_To_A_Roster_Member_Without_MembersManage_Is_Refused()
    {
        await using var fixture = await Fixture.CreateAsync(
            PermissionCompositions.Admin, successorPermissions: PermissionCompositions.Member);
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-third");

        Assert.Equal(AdminRevokeMemberStatus.SuccessorRefused, result?.Status);
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.Null((await grants.Grants.AsNoTracking()
            .SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
    }

    /// <summary>
    /// Ledger L618 end to end: after the handover the SUCCESSOR can act on the admin surface with the
    /// role they were handed, and the outgoing holder cannot. Neither party holds a roster edge, so this
    /// is the case the grant/roster duality used to strand - the handover satisfied L618 in grant state
    /// while leaving nobody able to manage members.
    /// </summary>
    [Fact]
    public async Task After_The_Handover_The_Successor_Reaches_The_Admin_Surface_And_The_Predecessor_Does_Not()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        Assert.NotNull(await fixture.Authority.ListMembersAsync(fixture.WebHandle, TenantId));
        Assert.Null(await fixture.Authority.ListMembersAsync(fixture.ThirdHandle, TenantId));

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-third");
        Assert.Equal(AdminRevokeMemberStatus.HandedOver, result?.Status);
        // The handover advances the successor's authorization epoch; their next request re-pins it.
        await RepinSessionEpochAsync(fixture, "session-third", "principal-third");

        Assert.NotNull(await fixture.Authority.ListMembersAsync(fixture.ThirdHandle, TenantId));
        Assert.Null(await fixture.Authority.ListMembersAsync(fixture.WebHandle, TenantId));
    }

    /// <summary>A handover that is refused moves nothing: neither party's admin access changes.</summary>
    [Fact]
    public async Task A_Refused_Handover_Leaves_Both_Parties_Admin_Access_Unchanged()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await PromoteToAdministratorAsync(fixture, WebGrantId);

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-nobody");

        Assert.Equal(AdminRevokeMemberStatus.SuccessorRefused, result?.Status);
        Assert.NotNull(await fixture.Authority.ListMembersAsync(fixture.WebHandle, TenantId));
        Assert.Null(await fixture.Authority.ListMembersAsync(fixture.ThirdHandle, TenantId));
    }

    /// <summary>
    /// A party the signed roster admitted and then ejected holds nothing here, whatever their grants
    /// still say - the roster plane's removal is not undone by a grant that outlived it.
    /// </summary>
    [Fact]
    public async Task An_Ejected_Roster_Party_Is_Refused_Even_Holding_An_Administrator_Grant()
    {
        await using var fixture = await Fixture.CreateAsync(
            PermissionCompositions.Admin, ejectSuccessor: true);
        await PromoteToAdministratorAsync(fixture, ThirdGrantId);

        Assert.Null(await fixture.Authority.ListMembersAsync(fixture.ThirdHandle, TenantId));
    }

    /// <summary>
    /// Replaces the session's authorization-epoch pin with the store's current value - what a live
    /// session refresh does after any grant mutation touching that principal.
    /// </summary>
    private static async Task RepinSessionEpochAsync(
        Fixture fixture, string sessionCorrelationId, string principalId)
    {
        long epoch;
        await using (var grants = fixture.GrantFactory.CreateDbContext())
        {
            epoch = (await grants.GrantAuthorizationEpochs.AsNoTracking()
                .SingleAsync(row => row.TenantId == TenantId && row.PrincipalId == principalId))
                .AuthorizationEpoch;
        }

        await using var sessions = fixture.SessionFactory.CreateDbContext();
        var session = await sessions.UserSessions
            .SingleAsync(row => row.SessionCorrelationId == sessionCorrelationId);
        sessions.UserSessions.Remove(session);
        await sessions.SaveChangesAsync();
        sessions.UserSessions.Add(session with { AuthorizationEpoch = epoch });
        await sessions.SaveChangesAsync();
    }

    private static async Task PromoteToAdministratorAsync(Fixture fixture, string grantId)
    {
        await using var grants = fixture.GrantFactory.CreateDbContext();
        var row = await grants.Grants.SingleAsync(g => g.GrantId == grantId);
        row.RoleVocabulary = RoleReference.Administrator.Vocabulary;
        row.RoleName = RoleReference.Administrator.Name;
        await grants.SaveChangesAsync();
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Admin_Revoking_An_Unknown_Grant_Is_NotFound()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);

        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, "66666666-6666-6666-6666-666666666666");

        Assert.NotNull(result);
        Assert.Equal(AdminRevokeMemberStatus.NotFound, result!.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Admin_Lists_Only_Live_Pending_Invitations()
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await fixture.SeedInvitationAsync("inv-live", consumed: false, revoked: false, expired: false);
        await fixture.SeedInvitationAsync("inv-consumed", consumed: true, revoked: false, expired: false);
        await fixture.SeedInvitationAsync("inv-revoked", consumed: false, revoked: true, expired: false);
        await fixture.SeedInvitationAsync("inv-expired", consumed: false, revoked: false, expired: true);

        var result = await fixture.Authority.ListPendingInvitationsAsync(fixture.Handle, TenantId);

        Assert.NotNull(result);
        var ids = result!.Invitations.Select(i => i.InvitationId).ToArray();
        Assert.Contains("inv-live", ids);
        Assert.DoesNotContain("inv-consumed", ids);
        Assert.DoesNotContain("inv-revoked", ids);
        Assert.DoesNotContain("inv-expired", ids);
    }

    [Fact]
    public async Task Unattributed_Administrator_Can_Hand_Over_Without_A_Roster_Write()
    {
        var captures = new BoundaryCaptures();
        await using var fixture = await Fixture.CreateAsync(
            PermissionCompositions.Admin, captures: captures, omitTargetParty: true);
        await PromoteToAdministratorAsync(fixture, WebGrantId);
        var result = await fixture.Authority.RevokeMemberGrantAsync(
            fixture.Handle, TenantId, WebGrantId, successorPrincipalId: "principal-third");
        Assert.Equal(AdminRevokeMemberStatus.HandedOver, result?.Status);
        Assert.Empty(captures.RosterWriterDecisions);
        var decision = Assert.Single(captures.GrantWriterDecisions);
        Assert.Equal(2, captures.GrantAudit.Decisions.Count);
        Assert.All(captures.GrantAudit.Decisions, audit => Assert.Same(decision, audit));
        await using var grants = fixture.GrantFactory.CreateDbContext();
        Assert.NotNull((await grants.Grants.SingleAsync(g => g.GrantId == WebGrantId)).RevokedAtUnixMs);
        Assert.Equal("principal-third", (await grants.Grants.SingleAsync(g => g.GrantId == result!.SuccessorGrantId)).SubjectId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ejection_Refuses_Both_Key_Spaces_Before_Live_Edge_Or_Grant(
        bool ejectedPrincipal, bool otherKeyHasLiveEdge)
    {
        using var founder = KeyPair.Generate();
        using var removed = KeyPair.Generate();
        using var other = KeyPair.Generate();
        var signer = new Ed25519Signer(founder);
        var verifier = new Ed25519Verifier();
        var ejected = ejectedPrincipal ? "principal-web" : "party-web";
        var alternate = ejectedPrincipal ? "party-web" : "principal-web";
        var roster = MemberRoster.Genesis(Guid.Parse(TenantId), "founder", signer, verifier, Now, Guid.NewGuid())
            .Admit("founder", signer, ejected, removed.PrincipalId,
                PermissionCompositions.Admin, verifier, Now, Guid.NewGuid());
        if (otherKeyHasLiveEdge)
            roster = roster.Admit("founder", signer, alternate, other.PrincipalId,
                PermissionCompositions.Admin, verifier, Now, Guid.NewGuid());
        roster = roster.Revoke("founder", ejected);
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin);
        await PromoteToAdministratorAsync(fixture, WebGrantId);
        var closure = new GrantDerivedClosure(new NodeEfGrantStore(fixture.GrantFactory));
        var principal = new ActorId("principal-web");
        var inputs = await EffectiveMemberPermissions.ReadAsync(
            closure, roster, "party-web", new TenantId(TenantId), principal, Now, CancellationToken.None);
        var decision = await TestAuthorization.AllowGate().DecideAsync(
            new AuthorizationWriteContext(principal, new TenantId(TenantId), Now)
                .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", "ejection")
                with { Roster = inputs });
        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.True(decision.Evidence.Roster!.Ejected);

    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string WebHandleValue = "selected-session-handle-web";
        internal const string ThirdHandleValue = "selected-session-handle-third";

        private readonly string[] _paths;

        private Fixture(
            string[] paths,
            ContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
            ContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
            ContextFactory<NodeLocalSearchDbContext> grantFactory,
            string handle,
            IAdminTeamAccessAuthority authority)
        {
            _paths = paths;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            GrantFactory = grantFactory;
            Handle = handle;
            Authority = authority;
        }

        public ContextFactory<NodeLocalInstallationIdentityDbContext> IdentityFactory { get; }
        public ContextFactory<NodeLocalWebSessionDbContext> SessionFactory { get; }
        public ContextFactory<NodeLocalSearchDbContext> GrantFactory { get; }
        public string Handle { get; }
        public string WebHandle => WebHandleValue;
        public string ThirdHandle => ThirdHandleValue;
        public IAdminTeamAccessAuthority Authority { get; }

        public static async Task<Fixture> CreateAsync(
            PermissionSet callerPermissions,
            TimeProvider? timeProvider = null,
            BoundaryCaptures? captures = null,
            bool omitTargetParty = false,
            PermissionSet? successorPermissions = null,
            bool ejectSuccessor = false,
            AuthorizationRefusalAudit? refusalAudit = null)
        {
            var identityPath = TempPath("identity");
            var sessionPath = TempPath("session");
            var grantPath = TempPath("grant");
            var identityFactory = ContextFactory<NodeLocalInstallationIdentityDbContext>.Create(
                identityPath, o => new NodeLocalInstallationIdentityDbContext(o),
                NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName);
            var sessionFactory = ContextFactory<NodeLocalWebSessionDbContext>.Create(
                sessionPath, o => new NodeLocalWebSessionDbContext(o),
                NodeLocalWebSessionDbContext.MigrationsHistoryTableName);
            var grantFactory = ContextFactory<NodeLocalSearchDbContext>.Create(
                grantPath, o => new NodeLocalSearchDbContext(o),
                NodeLocalSearchDbContext.MigrationsHistoryTableName);

            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
                identity.Accounts.Add(new InstallationAccountRecord
                {
                    AccountId = "account-admin",
                    NormalizedUsername = "ADMIN",
                    CredentialHash = "digest",
                    CredentialAlgorithm = "test",
                    CredentialCeremonyId = "test",
                    CredentialVersion = 1,
                    Status = InstallationAccountStatus.Active,
                    SecurityVersion = 2,
                    OwnerVersion = 1,
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                });
                await identity.SaveChangesAsync();
            }

            var handle = "selected-session-handle";
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
                sessions.UserSessions.Add(new WebUserSessionRecord(
                    "session-admin", "account-admin", 2, TenantId, "membership-admin", 3,
                    "principal-admin", "party-admin",
                    [new PinnedGrantOwnerVersion(AdminGrantId, 4)], 5,
                    AccountSetupInvitationStore.Digest(handle), "antiforgery-admin", "selection-admin",
                    Now - TimeSpan.FromMinutes(1), Now + TimeSpan.FromMinutes(30),
                    Now + TimeSpan.FromHours(1), 1));
                // The other two parties hold their own live selected sessions, so the admin surface can be
                // asked what THEY may do -- the only way to see a handover from the successor's side.
                sessions.UserSessions.Add(new WebUserSessionRecord(
                    "session-web", "account-admin", 2, TenantId, "membership-web", 3,
                    "principal-web", "party-web",
                    [new PinnedGrantOwnerVersion(WebGrantId, 4)], 1,
                    AccountSetupInvitationStore.Digest(WebHandleValue), "antiforgery-web", "selection-web",
                    Now - TimeSpan.FromMinutes(1), Now + TimeSpan.FromMinutes(30),
                    Now + TimeSpan.FromHours(1), 1));
                sessions.UserSessions.Add(new WebUserSessionRecord(
                    "session-third", "account-admin", 2, TenantId, "membership-third", 3,
                    "principal-third", "party-third",
                    [new PinnedGrantOwnerVersion(ThirdGrantId, 4)], 1,
                    AccountSetupInvitationStore.Digest(ThirdHandleValue), "antiforgery-third",
                    "selection-third",
                    Now - TimeSpan.FromMinutes(1), Now + TimeSpan.FromMinutes(30),
                    Now + TimeSpan.FromHours(1), 1));
                await sessions.SaveChangesAsync();
            }

            await using (var grants = grantFactory.CreateDbContext())
            {
                await grants.Database.EnsureCreatedAsync();
                grants.Grants.Add(GrantRowFor(AdminGrantId, "principal-admin", "[]"));
                grants.Grants.Add(GrantRowFor(WebGrantId, "principal-web", "[\"Captain\"]"));
                grants.Grants.Add(GrantRowFor(ThirdGrantId, "principal-third", "[]"));
                grants.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
                {
                    TenantId = TenantId, PrincipalId = "principal-admin", AuthorizationEpoch = 5,
                });
                grants.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
                {
                    TenantId = TenantId, PrincipalId = "principal-web", AuthorizationEpoch = 1,
                });
                grants.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
                {
                    TenantId = TenantId, PrincipalId = "principal-third", AuthorizationEpoch = 1,
                });
                await grants.SaveChangesAsync();
            }

            var roster = CreateRoster(
                Guid.Parse(TenantId), callerPermissions, successorPermissions, ejectSuccessor);
            var parties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["principal-admin"] = "party-admin",
                ["principal-third"] = "party-third",
            };
            if (!omitTargetParty) parties["principal-web"] = "party-web";
            var partyReader = new MapPartyReader(parties);
            var store = new AccountSetupInvitationStore(identityFactory);
            var selectedSessionStore = new WebSelectedSessionStore(sessionFactory);
            var issuer = new AccountSetupInvitationIssuer(
                sessionFactory, selectedSessionStore, identityFactory, grantFactory, partyReader,
                new FixedRosterReader(roster), store, TestAuthorization.AllowGate(), new FixedTimeProvider(Now));
            var grantStore = new NodeEfGrantStore(grantFactory);
            IAuthorizedGrantRevocationWriter grantWriter = new AuthorizedGrantRevocationWriter(grantStore);
            INodeRosterMemberRevocationAuthority rosterWriter = new NoopRosterMemberRevocationAuthority();
            IAuthorizedAuditTrail grantAudit = new InMemoryAuditTrail();
            if (captures is not null)
            {
                grantWriter = new CapturingGrantWriter(grantWriter, captures.GrantWriterDecisions);
                rosterWriter = new CapturingRosterWriter(captures.RosterWriterDecisions, captures.RosterAudit);
                grantAudit = captures.GrantAudit;
            }
            var authority = new AdminTeamAccessAuthority(
                sessionFactory, selectedSessionStore, identityFactory, grantFactory, partyReader,
                new FixedRosterReader(roster), store, issuer,
                grantStore, grantWriter, new GrantDerivedClosure(grantStore),
                TestAuthorization.AllowGate(), timeProvider ?? new FixedTimeProvider(Now),
                rosterWriter, grantAudit,
                new Ed25519Signer(KeyPair.Generate()), refusalAudit: refusalAudit);
            return new Fixture(
                [identityPath, sessionPath, grantPath], identityFactory, sessionFactory, grantFactory,
                handle, authority);
        }

        public async Task SeedInvitationAsync(string id, bool consumed, bool revoked, bool expired)
        {
            await using var identity = IdentityFactory.CreateDbContext();
            identity.AccountSetupInvitations.Add(new AccountSetupInvitationRecord
            {
                InvitationId = id,
                TenantId = TenantId,
                InviterAccountId = "account-admin",
                InviterPrincipalId = "principal-admin",
                InviterPartyId = "party-admin",
                InviterSessionCorrelationId = "session-admin",
                InviterMembershipId = "membership-admin",
                InviterMembershipOwnerVersion = 3,
                InviterGrantId = AdminGrantId,
                InviterGrantOwnerVersion = 4,
                InviterAuthorizationEpoch = 5,
                RequestedPermissionsJson = "[\"records:read\"]",
                TokenDigest = AccountSetupInvitationStore.Digest($"code-{id}"),
                Purpose = WebSetupInvitationPurpose.AccountSetup,
                CommandFingerprint = id,
                IssuedAtUtc = Now - TimeSpan.FromMinutes(5),
                AbsoluteExpiresAtUtc = expired ? Now - TimeSpan.FromMinutes(1) : Now + TimeSpan.FromHours(1),
                ConsumedAtUtc = consumed ? Now - TimeSpan.FromMinutes(2) : null,
                RevokedAtUtc = revoked ? Now - TimeSpan.FromMinutes(2) : null,
                OwnerVersion = 1,
            });
            await identity.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            foreach (var path in _paths)
            {
                File.Delete(path);
            }
            return ValueTask.CompletedTask;
        }

        private static GrantRow GrantRowFor(string grantId, string principalId, string rolesJson) => new()
        {
            GrantId = grantId,
            TenantId = TenantId,
            SubjectId = principalId,
            RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
            RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
            ScopeType = 0,
            ScopeValue = "/",
            Residency = 0,
            ValidityFromUnixMs = (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
            ValidityUntilUnixMs = (Now + TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
            Status = 0,
            GranterKind = (int)GranterKind.Person,
            GrantedBy = "principal-founder",
            GrantedAtUnixMs = (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
            Source = (int)GrantSourceKind.Manual,
            ReasonCode = GrantReasonCodes.Manual,
            Approver = "principal-founder",
            LastReviewedAtUnixMs = (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
            OwnerVersion = 4,
        };

        private static MemberRoster CreateRoster(
            Guid tenantId,
            PermissionSet callerPermissions,
            PermissionSet? successorPermissions,
            bool ejectSuccessor)
        {
            using var founderKey = KeyPair.Generate();
            using var adminKey = KeyPair.Generate();
            using var thirdKey = KeyPair.Generate();
            var founderSigner = new Ed25519Signer(founderKey);
            var verifier = new Ed25519Verifier();
            var roster = MemberRoster.Genesis(
                tenantId, "party-founder", founderSigner, verifier, Now,
                Guid.Parse("33333333-3333-3333-3333-333333333333")).Admit(
                "party-founder", founderSigner, "party-admin", adminKey.PrincipalId,
                callerPermissions, verifier, Now, Guid.Parse("44444444-4444-4444-4444-444444444444"));
            if (successorPermissions is null && !ejectSuccessor)
            {
                return roster;
            }

            roster = roster.Admit(
                "party-founder", founderSigner, "party-third", thirdKey.PrincipalId,
                successorPermissions ?? PermissionCompositions.Admin, verifier, Now,
                Guid.Parse("88888888-8888-8888-8888-888888888888"));
            return ejectSuccessor ? roster.Revoke("party-founder", "party-third") : roster;
        }

        private static string TempPath(string kind) =>
            Path.Combine(Path.GetTempPath(), $"admin-team-{kind}-{Guid.NewGuid():N}.db");
    }

    private sealed class ContextFactory<TContext>(
        string path,
        Func<DbContextOptions<TContext>, TContext> create,
        string historyTable) : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public static ContextFactory<TContext> Create(
            string path, Func<DbContextOptions<TContext>, TContext> create, string historyTable) =>
            new(path, create, historyTable);

        public TContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseSqlite($"Data Source={path};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(historyTable))
                .Options;
            return create(options);
        }
    }

    private sealed class FixedRosterReader(MemberRoster roster) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) =>
            Task.FromResult(roster);
    }

    private sealed class MapPartyReader(IReadOnlyDictionary<string, string> principalToParty)
        : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(principalToParty.TryGetValue(user.Value, out var party)
                ? new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(party))
                : null);
    }

    /// <summary>
    /// The closure ticket 205's evaluator derives from THESE grants. The seed offers members:manage to
    /// Administrator alone (<c>AccessGrantAuthorizationSeed</c>), so an install-wide Administrator grant
    /// in force is the only thing that yields <c>members:manage@/</c> - and a handover that moves that
    /// grant moves the atom with it.
    /// </summary>
    private sealed class GrantDerivedClosure(IGrantStore grants) : IAuthorizationClosureReader
    {
        private static readonly PermissionAtomSet Manage =
            PermissionAtomSet.Of(PermissionAtom.Parse("members:manage@/"));

        public async ValueTask<PermissionAtomSet> UserPermissionsAsync(
            TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default)
        {
            var population = await grants.SnapshotAsync(tenantId, ct);
            return population.Any(grant => grant.Subject == principal
                    && LastAdministratorGuard.IsAdministratorInForce(grant, at))
                ? Manage
                : PermissionAtomSet.Empty;
        }

        public ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(
            TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ActorId>>(Array.Empty<ActorId>());

        public ValueTask<PermissionAtomSet> RolePermissionsAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
            ValueTask.FromResult(Manage);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BoundaryCaptures
    {
        internal List<AuthorizationDecision> GrantWriterDecisions { get; } = [];
        internal List<AuthorizationDecision> RosterWriterDecisions { get; } = [];
        internal CapturingAuthorizedAuditTrail GrantAudit { get; } = new();
        internal CapturingAuthorizedAuditTrail RosterAudit { get; } = new();
    }

    private sealed class CapturingGrantWriter(
        IAuthorizedGrantRevocationWriter inner,
        List<AuthorizationDecision> decisions) : IAuthorizedGrantRevocationWriter
    {
        public Task<AccessGrant?> RevokeAsync(
            TenantId tenant,
            GrantId grant,
            GrantRevocation revocation,
            AuthorizationDecision admittedDecision,
            CancellationToken cancellationToken = default)
        {
            decisions.Add(admittedDecision);
            return inner.RevokeAsync(tenant, grant, revocation, admittedDecision, cancellationToken);
        }

        public Task<AdministratorHandover?> HandoverAsync(
            TenantId tenant,
            GrantId current,
            AccessGrant successor,
            GrantRevocation revocation,
            AuthorizationDecision admittedDecision,
            CancellationToken cancellationToken = default)
        {
            decisions.Add(admittedDecision);
            return inner.HandoverAsync(tenant, current, successor, revocation, admittedDecision, cancellationToken);
        }
    }

    private sealed class CapturingRosterWriter(
        List<AuthorizationDecision> decisions,
        IAuthorizedAuditTrail audit) : INodeRosterMemberRevocationAuthority
    {
        public async ValueTask<CompromisedDeviceRevocation?> RevokeAsync(
            TenantId tenant,
            string decisionTargetId,
            string revokedPartyId,
            string revokedByPartyId,
            string reason,
            string? correlationId,
            AuthorizationDecision admittedDecision,
            CancellationToken cancellationToken = default)
        {
            decisions.Add(admittedDecision);
            await audit.AppendAuthorizedAsync(null!, admittedDecision, cancellationToken);
            return null;
        }
    }

    private sealed class CapturingAuthorizedAuditTrail : IAuthorizedAuditTrail
    {
        internal List<AuthorizationDecision> Decisions { get; } = [];
        internal List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask AppendAuthorizedAsync(
            AuditRecord record, AuthorizationDecision decision, CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null)
        {
            Decisions.Add(decision);
            if (record is not null) Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
