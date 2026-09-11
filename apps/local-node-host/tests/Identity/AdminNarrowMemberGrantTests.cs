using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Ticket 362 slice 1 — an administrator narrows a member's admission-conferred grant through the
/// production surface (<c>IAdminTeamAccessAuthority.NarrowMemberGrantAsync</c>), over the REAL composition
/// <see cref="Mtw2TwoUserAcceptanceE2E"/> builds: the real definition-joined closure reader, the real
/// <see cref="AuthorizationGate"/>, the real EF grant store and authorization configuration store, the real
/// selected-session PEP, and real signed sessions minted by the production selected-session authority. No
/// security type is doubled anywhere on this path; the harness is the sibling test class's, not a copy.
/// </summary>
[Trait("PlanCard", "L2-362")]
public sealed class AdminNarrowMemberGrantTests
{
    // Mtw2's frozen instant and founder — the decision's At flows from the write authority, never a clock
    // inside the authority.
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 9, 25, 0, TimeSpan.Zero);
    private const string FounderPrincipal = "principal-founder";

    private static AuthorizationWriteContext FounderAuthority(string tenantId) =>
        new(new ActorId(FounderPrincipal), new TenantId(tenantId), Now);

    /// <summary>(a) the narrowing revokes the wider grant, reissues the narrower one, and the REAL gate refuses the removed act.</summary>
    [Fact(DisplayName = "narrowing revokes the wider grant, reissues the narrower one, and the real gate refuses the removed act")]
    public async Task Narrowing_Revokes_The_Wider_Grant_And_The_Real_Gate_Refuses_The_Removed_Act()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var member = await JoinerPrincipalAsync(h, setup);
        var conferred = await ConferAsync(h, tenant, member, Permission.ContactsRead, Permission.SchedulingAuthor);

        // The conferred authority is live before the act: the real closure reader answers with both atoms.
        Assert.Contains(Permission.SchedulingAuthor, await AtomsAsync(h, tenant, member));

        var narrowed = await h.AdminTeam.NarrowMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, conferred.GrantId.ToString(),
            new[] { Permission.ContactsRead }, FounderAuthority(setup.TenantId));

        Assert.NotNull(narrowed);
        Assert.Equal(AdminNarrowMemberGrantStatus.Narrowed, narrowed!.Status);
        Assert.NotNull(narrowed.NarrowedGrantId);
        Assert.NotEqual(conferred.GrantId.ToString(), narrowed.NarrowedGrantId);

        // The old grant is revoked in the durable row, and the reissued one is live on the SAME subject key.
        var old = await h.ReadGrantRowAsync(conferred.GrantId.ToString());
        Assert.NotNull(old.RevokedAtUnixMs);
        Assert.Equal((int)GrantStatus.Revoked, old.Status);
        var reissue = await h.ReadGrantRowAsync(narrowed.NarrowedGrantId!);
        Assert.Null(reissue.RevokedAtUnixMs);
        Assert.Equal(old.SubjectId, reissue.SubjectId);
        Assert.Equal(old.ScopeValue, reissue.ScopeValue);

        // The refusal is traceable to the narrowed grant: the only authority the member now holds from this
        // act is the reissued grant's role, and it no longer carries the removed act.
        var after = await AtomsAsync(h, tenant, member);
        Assert.DoesNotContain(Permission.SchedulingAuthor, after);
        Assert.Contains(Permission.ContactsRead, after);
        Assert.Equal(
            reissue.RoleName,
            (await new NodeEfGrantStore(h.SearchStore.Factory)
                .FindAsync(tenant, new GrantId(Guid.Parse(narrowed.NarrowedGrantId!))))!.Role.Name);
    }

    /// <summary>(b) a superset is refused NotASubset and nothing changes.</summary>
    [Fact(DisplayName = "a superset is refused NotASubset and nothing is written")]
    public async Task Superset_Is_Refused_And_Nothing_Changes()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var member = await JoinerPrincipalAsync(h, setup);
        var conferred = await ConferAsync(h, tenant, member, Permission.ContactsRead);
        var before = await h.ReadGrantRowAsync(conferred.GrantId.ToString());
        var epochBefore = await EpochAsync(h, tenant, member);
        var grantsBefore = await GrantCountAsync(h, member);

        var result = await h.AdminTeam.NarrowMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, conferred.GrantId.ToString(),
            new[] { Permission.ContactsRead, Permission.ContactsCreate }, FounderAuthority(setup.TenantId));

        Assert.NotNull(result);
        Assert.Equal(AdminNarrowMemberGrantStatus.NotASubset, result!.Status);
        Assert.Null(result.NarrowedGrantId);
        var after = await h.ReadGrantRowAsync(conferred.GrantId.ToString());
        Assert.Equal(before.OwnerVersion, after.OwnerVersion);
        Assert.Equal((int)GrantStatus.Active, after.Status);
        Assert.Equal(epochBefore, await EpochAsync(h, tenant, member));
        Assert.Equal(grantsBefore, await GrantCountAsync(h, member));
    }

    /// <summary>(c) the equal set is the NotFound-shaped no-op: an act that narrows nothing writes nothing.</summary>
    [Fact(DisplayName = "narrowing to the set already in force writes nothing")]
    public async Task Equal_Set_Is_A_No_Op()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var member = await JoinerPrincipalAsync(h, setup);
        var conferred = await ConferAsync(h, tenant, member, Permission.ContactsRead);
        var epochBefore = await EpochAsync(h, tenant, member);
        var grantsBefore = await GrantCountAsync(h, member);

        var result = await h.AdminTeam.NarrowMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, conferred.GrantId.ToString(),
            new[] { Permission.ContactsRead }, FounderAuthority(setup.TenantId));

        Assert.NotNull(result);
        Assert.Equal(AdminNarrowMemberGrantStatus.NotFound, result!.Status);
        Assert.Equal(epochBefore, await EpochAsync(h, tenant, member));
        Assert.Equal(grantsBefore, await GrantCountAsync(h, member));
    }

    // (d) the no-escalation subset check needs a caller whose admitted set is NARROWER than the target
    // grant's. Every Administrator in this real composition holds the whole catalogue, so that caller does
    // not exist here; the row lives in AdminTeamAccessAuthorityTests
    // (Narrowing_Refuses_An_Act_The_Caller_Does_Not_Hold), whose fixture owns the caller's set.

    /// <summary>(e) two members on one key: narrowing one does not touch the other.</summary>
    [Fact(DisplayName = "narrowing one member's grant leaves the other member's grant and epoch untouched")]
    public async Task Narrowing_One_Member_Does_Not_Touch_The_Other()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var member = await JoinerPrincipalAsync(h, setup);
        const string other = "principal-other-362";
        var narrowTarget = await ConferAsync(
            h, tenant, member, Permission.ContactsRead, Permission.SchedulingAuthor);
        var bystander = await ConferAsync(
            h, tenant, other, Permission.ContactsRead, Permission.SchedulingAuthor);
        var bystanderBefore = await h.ReadGrantRowAsync(bystander.GrantId.ToString());
        var bystanderEpoch = await EpochAsync(h, tenant, other);

        var result = await h.AdminTeam.NarrowMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, narrowTarget.GrantId.ToString(),
            new[] { Permission.ContactsRead }, FounderAuthority(setup.TenantId));
        Assert.Equal(AdminNarrowMemberGrantStatus.Narrowed, result!.Status);

        var bystanderAfter = await h.ReadGrantRowAsync(bystander.GrantId.ToString());
        Assert.Equal(bystanderBefore.OwnerVersion, bystanderAfter.OwnerVersion);
        Assert.Equal((int)GrantStatus.Active, bystanderAfter.Status);
        Assert.Equal(bystanderEpoch, await EpochAsync(h, tenant, other));
        Assert.Contains(Permission.SchedulingAuthor, await AtomsAsync(h, tenant, other));
        Assert.DoesNotContain(Permission.SchedulingAuthor, await AtomsAsync(h, tenant, member));
    }

    /// <summary>(f) the authorization epoch advances EXACTLY once and a pinned live session re-evaluates.</summary>
    [Fact(DisplayName = "holds 362.A2: the narrowing advances the epoch exactly once and the pinned live session's next read is refused")]
    public async Task Epoch_Advances_Exactly_Once_And_The_Pinned_Session_Re_Evaluates()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var joinerHandle = await Mtw2TwoUserAcceptanceE2E.LoginJoinerAsync(h, setup.TenantId);
        var principal = await h.SelectedSessionPrincipals.AuthenticateAsync(joinerHandle);
        Assert.NotNull(principal);
        var member = principal!.PrincipalUserId.Value;
        var conferred = await ConferAsync(
            h, tenant, member, Permission.ContactsRead, Permission.SchedulingAuthor);
        Assert.True(await HasPermissionAsync(h, principal, Permission.SchedulingAuthor));
        var epochBefore = await EpochAsync(h, tenant, member);

        var result = await h.AdminTeam.NarrowMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, conferred.GrantId.ToString(),
            new[] { Permission.ContactsRead }, FounderAuthority(setup.TenantId));
        Assert.Equal(AdminNarrowMemberGrantStatus.Narrowed, result!.Status);

        // EXACTLY once: the revoke leg and the reissue leg share one advance inside the one transaction.
        Assert.Equal(epochBefore + 1, await EpochAsync(h, tenant, member));
        // The already-pinned live session re-evaluates on its NEXT request and loses the narrowed act
        // without being handed a wider cached closure.
        Assert.False(await HasPermissionAsync(h, principal, Permission.SchedulingAuthor));
    }

    /// <summary>
    /// (h) 362.A3 — the REVOKE leg of the same production surface
    /// (<c>IAdminTeamAccessAuthority.RevokeMemberGrantAsync</c>): a live pinned session's next read of the
    /// revoked grant's act is refused. The member's session survives (the revoked grant is not the one the
    /// session pins), so the refusal is the PEP re-deriving the conferred closure, not a dead session.
    /// </summary>
    [Fact(DisplayName = "holds 362.A3: after a revoke through the production surface the pinned live session's next read is refused")]
    public async Task Revoking_Through_The_Surface_Refuses_The_Pinned_Sessions_Next_Read()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var joinerHandle = await Mtw2TwoUserAcceptanceE2E.LoginJoinerAsync(h, setup.TenantId);
        var principal = await h.SelectedSessionPrincipals.AuthenticateAsync(joinerHandle);
        Assert.NotNull(principal);
        var member = principal!.PrincipalUserId.Value;
        var conferred = await ConferAsync(
            h, tenant, member, Permission.ContactsRead, Permission.SchedulingAuthor);
        Assert.True(await HasPermissionAsync(h, principal, Permission.SchedulingAuthor));

        var revoked = await h.AdminTeam.RevokeMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, conferred.GrantId.ToString(),
            FounderAuthority(setup.TenantId));

        Assert.NotNull(revoked);
        Assert.Equal(AdminRevokeMemberStatus.Revoked, revoked!.Status);
        var row = await h.ReadGrantRowAsync(conferred.GrantId.ToString());
        Assert.Equal((int)GrantStatus.Revoked, row.Status);
        Assert.NotNull(row.RevokedAtUnixMs);
        // The already-pinned live session loses the act on its NEXT request, with no re-login.
        Assert.False(await HasPermissionAsync(h, principal, Permission.SchedulingAuthor));
        Assert.DoesNotContain(Permission.SchedulingAuthor, await AtomsAsync(h, tenant, member));
    }

    /// <summary>(g) both audit rows carry one correlation id and the reason "member-narrowed".</summary>
    [Fact(DisplayName = "both audit legs carry one correlation id, the member-narrowed reason and the reissued grant id")]
    public async Task Both_Audit_Legs_Carry_One_Correlation_Id_And_The_Narrow_Reason()
    {
        var setup = await Mtw2TwoUserAcceptanceE2E.CreateAcceptedMembersAsync();
        await using var h = setup.Harness;
        var tenant = new TenantId(setup.TenantId);
        var member = await JoinerPrincipalAsync(h, setup);
        var conferred = await ConferAsync(
            h, tenant, member, Permission.ContactsRead, Permission.SchedulingAuthor);

        var result = await h.AdminTeam.NarrowMemberGrantAsync(
            setup.FounderSelectedHandle, setup.TenantId, conferred.GrantId.ToString(),
            new[] { Permission.ContactsRead }, FounderAuthority(setup.TenantId));
        Assert.Equal(AdminNarrowMemberGrantStatus.Narrowed, result!.Status);

        var revoked = Assert.Single(await AuditAsync(h, tenant, AuditEventType.CapabilityRevoked));
        var delegated = Assert.Single(await AuditAsync(h, tenant, AuditEventType.CapabilityDelegated));
        foreach (var row in new[] { revoked, delegated })
        {
            Assert.Equal("member-narrowed", row.Payload.Payload.Body["reason"]);
            Assert.Equal(conferred.GrantId.Value.ToString("D"), row.Payload.Payload.Body["grant_id"]);
            Assert.Equal(result.NarrowedGrantId, row.Payload.Payload.Body["successor_grant_id"]);
            Assert.Equal(new ActorId(FounderPrincipal), row.Actor);
        }
        Assert.Equal(
            revoked.Payload.Payload.Body["correlation_id"],
            delegated.Payload.Payload.Body["correlation_id"]);
    }

    // ── helpers (the production derivations; nothing here doubles a security type) ────────────────────

    /// <summary>
    /// Confer an admission grant through the ONE production derivation
    /// (<c>NodeEfAuthorizationConfigurationStore.ConferAdmissionGrantAsync</c>) — the same call
    /// <c>WebAdmittedMemberAtlasBridge</c> makes on a live admission.
    /// </summary>
    private static async Task<AccessGrant> ConferAsync(
        Mtw2TwoUserAcceptanceE2E.Harness h, TenantId tenant, string principalId, params string[] permissions)
    {
        var grant = await new NodeEfAuthorizationConfigurationStore(
                h.SearchStore.Factory, new InMemoryRoleVocabulary([]))
            .ConferAdmissionGrantAsync(
                tenant, principalId, FounderPrincipal, PermissionSet.Of(permissions), Now);
        Assert.NotNull(grant);
        return grant!;
    }

    private static async Task<string> JoinerPrincipalAsync(
        Mtw2TwoUserAcceptanceE2E.Harness h, Mtw2TwoUserAcceptanceE2E.AcceptedMembers setup)
    {
        var row = await h.ReadGrantRowAsync(setup.JoinerGrantId);
        return row.SubjectId;
    }

    private static async Task<IReadOnlyCollection<string>> AtomsAsync(
        Mtw2TwoUserAcceptanceE2E.Harness h, TenantId tenant, string principalId) =>
        (await h.LiveAuthorization.UserPermissionsAsync(tenant, new ActorId(principalId), Now))
            .Atoms.Select(atom => atom.Operation.Value).Distinct(StringComparer.Ordinal).ToArray();

    private static async Task<long> EpochAsync(
        Mtw2TwoUserAcceptanceE2E.Harness h, TenantId tenant, string principalId) =>
        await new NodeEfGrantStore(h.SearchStore.Factory)
            .ReadAuthorizationEpochAsync(tenant, new ActorId(principalId)) ?? 0;

    private static async Task<int> GrantCountAsync(Mtw2TwoUserAcceptanceE2E.Harness h, string principalId)
    {
        await using var grants = h.SearchStore.CreateContext();
        return await grants.Grants.AsNoTracking().CountAsync(g => g.SubjectId == principalId);
    }

    /// <summary>Ask the PRODUCTION selected-session PEP, bound to a real live principal, for one act.</summary>
    private static async Task<bool> HasPermissionAsync(
        Mtw2TwoUserAcceptanceE2E.Harness h, SelectedSessionRequestPrincipal principal, string permission)
    {
        var context = new SelectedSessionTenantContext(h.PermissionResolver);
        await context.BindAsync(principal);
        return context.HasPermission(permission);
    }

    private static async Task<IReadOnlyList<AuditRecord>> AuditAsync(
        Mtw2TwoUserAcceptanceE2E.Harness h, TenantId tenant, AuditEventType eventType)
    {
        var rows = new List<AuditRecord>();
        await foreach (var row in h.AdminAudit.QueryAsync(new AuditQuery(tenant, eventType)))
        {
            rows.Add(row);
        }
        return rows;
    }
}
