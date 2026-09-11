using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class AdminTeamAccessRoutesTests
{
    private const string SelectedHandle = "selected-secret";
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset IssuedAt = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = new(2026, 7, 24, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AdminTeamAccessRoutes.InvitationsPath)]
    [InlineData(AdminTeamAccessRoutes.RevokeMemberPath)]
    [InlineData(AdminTeamAccessRoutes.NarrowMemberPath)]
    public async Task Write_Authorization_Denial_Returns_Rendered_403(string path)
    {
        var operation = AuthorizationOperation.Parse("members:manage");
        var write = new AuthorizationWriteContext(
            new ActorId("party-1"), new TenantId("tenant-1"), Now);
        var decision = await TestRouteGate.Denying().DecideAsync(
            write.Request(operation, AuthorizationGate.RecordKindFor(operation), "grant-9"));
        var denial = Assert.Throws<AuthorizationDeniedException>(() => decision.RequireAllowed());
        var authority = new RecordingAuthority { Denial = denial };
        var antiforgery = new RecordingAntiforgeryPolicy();
        object request = path == AdminTeamAccessRoutes.InvitationsPath
            ? new AdminTeamAccessRoutes.IssueInvitationRequest(["records:read"], "idem-1")
            : path == AdminTeamAccessRoutes.RevokeMemberPath
                ? new AdminTeamAccessRoutes.RevokeMemberRequest("grant-9")
                : new AdminTeamAccessRoutes.NarrowMemberRequest("grant-9", ["records:read"]);

        var response = await InvokePostAsync(path, authority, request, antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        var refusal = await AuthorizationRefusalRenderer.RenderAsync(decision, [], null);
        using var json = System.Text.Json.JsonDocument.Parse(response.Body);
        var body = json.RootElement;
        Assert.Equal(5, body.EnumerateObject().Count());
        Assert.Equal(refusal.Code, body.GetProperty("code").GetString());
        Assert.Equal(operation.Value, body.GetProperty("permission").GetString());
        Assert.Equal(refusal.Title, body.GetProperty("title").GetString());
        Assert.Equal(refusal.Detail, body.GetProperty("detail").GetString());
        Assert.Equal(refusal.Remediation, body.GetProperty("remediation").GetString());
        Assert.DoesNotContain(denial.Message, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(refusal.Diagnostic, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("party-1", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-1", response.Body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        Assert.Equal("replacement-token", response.Antiforgery);
    }

    // --- list members -------------------------------------------------------------------------

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task List_Members_Returns_Roster_And_Grant_Members_From_The_Principal_Tenant()
    {
        var authority = new RecordingAuthority
        {
            Members = new AdminTeamMembersResult(new[]
            {
                new TeamMemberView("party-roster", TeamMemberSource.Roster, new[] { "members:manage" }, null),
                new TeamMemberView("party-web", TeamMemberSource.Grant, new[] { "Captain" }, "grant-9"),
            }),
        };

        var response = await InvokeGetAsync(AdminTeamAccessRoutes.MembersPath, authority);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"partyId\":\"party-roster\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"roster\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"grant\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"grantId\":\"grant-9\"", response.Body, StringComparison.Ordinal);
        // The tenant came from the principal, never from the request.
        Assert.Equal("tenant-1", authority.MembersTenantId);
        Assert.Equal(SelectedHandle, authority.MembersHandle);
        Assert.Equal("no-store", response.CacheControl);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task List_Members_Missing_Cookie_Refuses_Before_The_Authority()
    {
        var authority = new RecordingAuthority { Members = new AdminTeamMembersResult(Array.Empty<TeamMemberView>()) };

        var response = await InvokeGetAsync(AdminTeamAccessRoutes.MembersPath, authority, selectedHandle: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.MembersHandle);
        Assert.Contains("admin_access_denied", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task List_Members_Missing_Principal_Refuses_Before_The_Authority()
    {
        var authority = new RecordingAuthority { Members = new AdminTeamMembersResult(Array.Empty<TeamMemberView>()) };

        var response = await InvokeGetAsync(AdminTeamAccessRoutes.MembersPath, authority, withPrincipal: false);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.MembersHandle);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task List_Members_Authority_Refusal_Is_A_NonEnumerating_401()
    {
        // A non-admin caller: the authority gate returns null. The route must not distinguish it.
        var authority = new RecordingAuthority { Members = null };

        var response = await InvokeGetAsync(AdminTeamAccessRoutes.MembersPath, authority);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Contains("admin_access_denied", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_Members_Exposes_Unattributed_Grant_And_Reason()
    {
        var authority = new RecordingAuthority
        {
            Members = new AdminTeamMembersResult([
                new TeamMemberView("UNATTRIBUTED", TeamMemberSource.Unattributed,
                    ["members:manage"], "grant-unresolved", "No unique live party binding in this tenant.")]),
        };
        var response = await InvokeGetAsync(AdminTeamAccessRoutes.MembersPath, authority);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(response.Body);
        var row = Assert.Single(json.RootElement.GetProperty("members").EnumerateArray());
        Assert.Equal("UNATTRIBUTED", row.GetProperty("partyId").GetString());
        Assert.Equal("unattributed", row.GetProperty("source").GetString());
        Assert.Equal("grant-unresolved", row.GetProperty("grantId").GetString());
        Assert.Equal("members:manage", Assert.Single(row.GetProperty("capabilities").EnumerateArray()).GetString());
        Assert.Equal("No unique live party binding in this tenant.", row.GetProperty("attributionFailure").GetString());
    }

    // --- list invitations ---------------------------------------------------------------------

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task List_Invitations_Returns_Pending_Without_Any_Token_Material()
    {
        var authority = new RecordingAuthority
        {
            Pending = new AdminPendingInvitationsResult(new[]
            {
                new AdminPendingInvitation("inv-1", "party-inviter", new[] { "records:read" }, IssuedAt, ExpiresAt),
            }),
        };

        var response = await InvokeGetAsync(AdminTeamAccessRoutes.InvitationsPath, authority);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"invitationId\":\"inv-1\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("\"records:read\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("digest", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("tenant-1", authority.PendingTenantId);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task List_Invitations_Missing_Cookie_Refuses_Before_The_Authority()
    {
        var authority = new RecordingAuthority
        {
            Pending = new AdminPendingInvitationsResult(Array.Empty<AdminPendingInvitation>()),
        };

        var response = await InvokeGetAsync(
            AdminTeamAccessRoutes.InvitationsPath, authority, selectedHandle: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Null(authority.PendingTenantId);
    }

    // --- issue invitation ---------------------------------------------------------------------

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Issue_Invitation_Returns_The_Code_Once_On_The_Principal_Tenant()
    {
        var authority = new RecordingAuthority
        {
            Issued = new AdminIssuedInvitation("inv-9", "raw-code-xyz", "tenant-1", ExpiresAt),
        };
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.InvitationsPath,
            authority,
            new AdminTeamAccessRoutes.IssueInvitationRequest(new[] { "records:read" }, "idem-1"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"code\":\"raw-code-xyz\"", response.Body, StringComparison.Ordinal);
        Assert.Equal("tenant-1", authority.IssueTenantId);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        Assert.Equal("no-store", response.CacheControl);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Issue_Invitation_Antiforgery_Failure_Refuses_Before_The_Authority()
    {
        var authority = new RecordingAuthority
        {
            Issued = new AdminIssuedInvitation("inv-9", "raw", "tenant-1", ExpiresAt),
        };
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptSelected = false };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.InvitationsPath,
            authority,
            new AdminTeamAccessRoutes.IssueInvitationRequest(new[] { "records:read" }, "idem-1"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("antiforgery_failed", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.IssueTenantId);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Issue_Invitation_Empty_Permission_Set_Is_A_Refused_Request()
    {
        var authority = new RecordingAuthority
        {
            Issued = new AdminIssuedInvitation("inv-9", "raw", "tenant-1", ExpiresAt),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.InvitationsPath,
            authority,
            new AdminTeamAccessRoutes.IssueInvitationRequest(Array.Empty<string>(), "idem-1"));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.IssueTenantId);
        Assert.Null(response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Issue_Invitation_Authority_Refusal_Is_A_NonEnumerating_401()
    {
        var authority = new RecordingAuthority { Issued = null };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.InvitationsPath,
            authority,
            new AdminTeamAccessRoutes.IssueInvitationRequest(new[] { "records:read" }, "idem-1"));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Contains("admin_access_denied", response.Body, StringComparison.Ordinal);
    }

    // --- revoke member ------------------------------------------------------------------------

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Revoke_Member_Success_Returns_Revoked_On_The_Principal_Tenant()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.Revoked),
        };
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-9"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"status\":\"revoked\"", response.Body, StringComparison.Ordinal);
        Assert.Equal("tenant-1", authority.RevokeTenantId);
        Assert.Equal("grant-9", authority.RevokeGrantId);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Revoke_Member_Self_Revocation_Is_A_Conflict()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.SelfRevocationRefused),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-self"));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Contains("self_revocation_refused", response.Body, StringComparison.Ordinal);
    }

    /// <summary>Ledger L619 — the last-Administrator refusal is its own conflict, not a 404.</summary>
    [Fact]
    public async Task Revoke_Member_Last_Administrator_Is_A_Conflict()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.LastAdministratorRefused),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-last-admin"));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Contains("last_administrator_refused", response.Body, StringComparison.Ordinal);
    }

    /// <summary>Ledger L618 — the successor id reaches the authority and the handover answers its own body.</summary>
    [Fact]
    public async Task Revoke_Member_With_A_Successor_Is_A_Handover()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.HandedOver, "grant-successor"),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-outgoing", "principal-successor"));

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("principal-successor", authority.RevokeSuccessorPrincipalId);
        Assert.Contains("handed_over", response.Body, StringComparison.Ordinal);
        Assert.Contains("grant-successor", response.Body, StringComparison.Ordinal);
    }

    /// <summary>An ineligible successor is a conflict, not a 404.</summary>
    [Fact]
    public async Task Revoke_Member_With_An_Ineligible_Successor_Is_A_Conflict()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.SuccessorRefused),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-outgoing", "principal-nobody"));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Contains("successor_refused", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Revoke_Member_Not_Found_Is_A_NonEnumerating_404()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.NotFound),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-absent"));

        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.Contains("member_not_found", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Revoke_Member_Antiforgery_Failure_Refuses_Before_The_Authority()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.Revoked),
        };
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptSelected = false };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-9"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("antiforgery_failed", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.RevokeGrantId);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public async Task Revoke_Member_Missing_Grant_Id_Is_A_Refused_Request()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.Revoked),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest(null));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.RevokeGrantId);
    }

    [Fact]
    [Trait("PlanCard", "3311")]
    public async Task Logged_In_Bootstrap_Invitation_And_Logout_Use_Real_Selected_Rotations()
    {
        await using var fixture = await RealAntiforgeryFixture.CreateAsync();

        var bootstrap = BuildContext(fixture.SelectedHandle, withPrincipal: true);
        var bootstrapResult = await AntiforgeryRoutes.IssueAsync(fixture.Policy, bootstrap);
        await bootstrapResult.ExecuteAsync(bootstrap);
        var invitationToken =
            bootstrap.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString();

        Assert.Equal(StatusCodes.Status204NoContent, bootstrap.Response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(invitationToken));

        var invitation = BuildContext(fixture.SelectedHandle, withPrincipal: true);
        invitation.Request.Headers[WebAntiforgeryPolicy.HeaderName] = invitationToken;
        var invitationResult = await AdminTeamAccessRoutes.IssueInvitationAsync(
            new RecordingAuthority
            {
                Issued = new AdminIssuedInvitation("inv-9", "raw-code", "tenant-1", ExpiresAt),
            },
            fixture.Policy,
            new AdminTeamAccessRoutes.IssueInvitationRequest(["records:read"], "idem-1"),
            invitation,
            Now);
        await invitationResult.ExecuteAsync(invitation);
        var logoutToken =
            invitation.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString();

        Assert.Equal(StatusCodes.Status200OK, invitation.Response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(logoutToken));

        var logout = BuildContext(fixture.SelectedHandle, withPrincipal: true);
        logout.Request.Headers[WebAntiforgeryPolicy.HeaderName] = logoutToken;
        // A selected handle is presented and no legacy credential is, so the route must dispatch to
        // the SELECTED branch; the legacy double below fails the test loudly if that stops holding.
        var logoutResult = await SessionLogoutRoutes.LogoutAsync(
            new SuccessfulLogoutAuthority(),
            new SelectedOnlyLegacyAuthority(),
            fixture.Policy,
            logout);
        await logoutResult.ExecuteAsync(logout);

        Assert.Equal(StatusCodes.Status204NoContent, logout.Response.StatusCode);
    }

    /// <summary>
    /// Ticket 362 slice 2 - the route-level row the retired permissions route used to hold, moved onto the
    /// one surviving member-permissions writer: the narrow route binds the tenant from the PRINCIPAL (never
    /// the request body), passes the grant id and the requested set through unchanged, and rotates
    /// antiforgery.
    /// </summary>
    [Fact]
    [Trait("PlanCard", "MTW-3770")]
    public async Task Narrow_Member_Returns_Narrowed_On_The_Principal_Tenant()
    {
        var authority = new RecordingAuthority
        {
            Narrow = new AdminNarrowMemberGrantResult(
                AdminNarrowMemberGrantStatus.Narrowed, "grant-narrowed"),
        };
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.NarrowMemberPath,
            authority,
            new AdminTeamAccessRoutes.NarrowMemberRequest(
                "grant-9", new[] { "assets:read", "forms:read" }),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains("\"status\":\"narrowed\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("grant-narrowed", response.Body, StringComparison.Ordinal);
        Assert.Equal("tenant-1", authority.NarrowTenantId);
        Assert.Equal("grant-9", authority.NarrowGrantId);
        Assert.Equal(new[] { "assets:read", "forms:read" }, authority.NarrowPermissions);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
    }

    [Fact]
    [Trait("PlanCard", "MTW-3770")]
    public async Task Narrow_Member_Without_A_Permission_Set_Is_Refused_Before_Authority()
    {
        var authority = new RecordingAuthority
        {
            Narrow = new AdminNarrowMemberGrantResult(
                AdminNarrowMemberGrantStatus.Narrowed, "grant-narrowed"),
        };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.NarrowMemberPath,
            authority,
            new AdminTeamAccessRoutes.NarrowMemberRequest("grant-9", null));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", response.Body, StringComparison.Ordinal);
        Assert.Null(authority.NarrowGrantId);
        Assert.Null(response.Antiforgery);
    }

    // --- path invariants ----------------------------------------------------------------------

    [Fact]
    [Trait("PlanCard", "MTW-2-2617")]
    public void Admin_Routes_Are_Literal_Selected_Session_Paths_Outside_The_PreAuth_Allowlist()
    {
        foreach (var path in new[]
                 {
                     AdminTeamAccessRoutes.MembersPath,
                     AdminTeamAccessRoutes.InvitationsPath,
                     AdminTeamAccessRoutes.RevokeMemberPath,
                     AdminTeamAccessRoutes.NarrowMemberPath,
                 })
        {
            Assert.DoesNotContain('{', path);
            Assert.False(
                Harborline.Api.LocalNodeHost.Health.NodeListenerCallerAuthPolicy.IsAllowlisted(path));
        }
    }

    // --- harness ------------------------------------------------------------------------------


    // --- earlier repository ticket #3311: a spent token is re-issued on EVERY exit path, not just success ----------

    /// <remarks>
    /// The card's symptom: one admin invitation left the session unable to perform any state-changing
    /// action, logout included, until it expired. The token is single-use, so a branch that returns
    /// after consuming it and does not re-issue hands the browser nothing to send next. Success was
    /// already covered; these pin the FAILURE branches, which are the ones a human actually hits.
    /// </remarks>
    [Fact]
    [Trait("PlanCard", "MTW-2-3311")]
    public async Task Issue_Invitation_Reissues_The_Token_When_The_Authority_Refuses()
    {
        var authority = new RecordingAuthority { Issued = null };   // refused
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.InvitationsPath,
            authority,
            new AdminTeamAccessRoutes.IssueInvitationRequest(new[] { "records:read" }, "idem-1"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        // Asserted on the RESPONSE, not just the double: the browser has to actually receive it.
        Assert.Equal("replacement-token", response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3311")]
    public async Task Revoke_Member_Reissues_The_Token_When_Self_Revocation_Is_Refused()
    {
        // The 409 an admin hits by trying to revoke the grant backing their own session — a mistake a
        // human makes once and should recover from, not be locked out by.
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.SelfRevocationRefused),
        };
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-self"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        Assert.Equal("replacement-token", response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3311")]
    public async Task Revoke_Member_Reissues_The_Token_When_The_Grant_Is_Not_Found()
    {
        var authority = new RecordingAuthority
        {
            Revoke = new AdminRevokeMemberResult(AdminRevokeMemberStatus.NotFound),
        };
        var antiforgery = new RecordingAntiforgeryPolicy();

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.RevokeMemberPath,
            authority,
            new AdminTeamAccessRoutes.RevokeMemberRequest("grant-missing"),
            antiforgery: antiforgery);

        Assert.NotEqual(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(SelectedHandle, antiforgery.RotatedSelectedHandle);
        Assert.Equal("replacement-token", response.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3311")]
    public async Task A_rejected_token_is_not_re_issued()
    {
        // The fail-closed edge. Re-issue happens only AFTER a successful consume — a caller who never
        // presented a valid token must not be handed one, or the route would mint credentials for an
        // unauthenticated request.
        var authority = new RecordingAuthority { Issued = null };
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptSelected = false };

        var response = await InvokePostAsync(
            AdminTeamAccessRoutes.InvitationsPath,
            authority,
            new AdminTeamAccessRoutes.IssueInvitationRequest(new[] { "records:read" }, "idem-1"),
            antiforgery: antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Null(antiforgery.RotatedSelectedHandle);
        Assert.Null(response.Antiforgery);
    }

    private static async Task<(int StatusCode, string Body, string CacheControl)> InvokeGetAsync(
        string path,
        RecordingAuthority authority,
        string? selectedHandle = SelectedHandle,
        bool withPrincipal = true)
    {
        var context = BuildContext(selectedHandle, withPrincipal);
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var result = path == AdminTeamAccessRoutes.MembersPath
            ? await AdminTeamAccessRoutes.ListMembersAsync(authority, context)
            : await AdminTeamAccessRoutes.ListInvitationsAsync(authority, context);
        await result.ExecuteAsync(context);
        return await ReadAsync(context, responseBody);
    }

    private static async Task<(
        int StatusCode,
        string Body,
        string CacheControl,
        string? Antiforgery)> InvokePostAsync(
        string path,
        RecordingAuthority authority,
        object request,
        string? selectedHandle = SelectedHandle,
        bool withPrincipal = true,
        RecordingAntiforgeryPolicy? antiforgery = null)
    {
        var context = BuildContext(selectedHandle, withPrincipal);
        antiforgery ??= new RecordingAntiforgeryPolicy();
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        IResult result = path == AdminTeamAccessRoutes.InvitationsPath
            ? await AdminTeamAccessRoutes.IssueInvitationAsync(
                authority, antiforgery, (AdminTeamAccessRoutes.IssueInvitationRequest)request, context, Now)
            : path == AdminTeamAccessRoutes.RevokeMemberPath
                ? await AdminTeamAccessRoutes.RevokeMemberAsync(
                    authority, antiforgery, (AdminTeamAccessRoutes.RevokeMemberRequest)request, context, Now)
                : await AdminTeamAccessRoutes.NarrowMemberAsync(
                    authority, antiforgery,
                    (AdminTeamAccessRoutes.NarrowMemberRequest)request, context, Now);
        await result.ExecuteAsync(context);
        var response = await ReadAsync(context, responseBody);
        return (
            response.StatusCode,
            response.Body,
            response.CacheControl,
            context.Response.Headers[WebAntiforgeryPolicy.HeaderName].FirstOrDefault());
    }

    private static DefaultHttpContext BuildContext(string? selectedHandle, bool withPrincipal)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (selectedHandle is not null)
        {
            context.Request.Headers.Cookie = $"__Host-hl-selected={selectedHandle}";
        }
        if (withPrincipal)
        {
            context.Features.Set(Principal());
        }
        return context;
    }

    private static async Task<(
        int StatusCode,
        string Body,
        string CacheControl)> ReadAsync(
            HttpContext context,
            MemoryStream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body);
        return (
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.CacheControl.ToString());
    }

    private static SelectedSessionRequestPrincipal Principal() =>
        new(
            "account-1",
            new TenantId("tenant-1"),
            new PrincipalUserId("principal-1"),
            new CanonicalPartyReference("party-1"),
            "membership-1",
            4,
            [new PinnedGrantOwnerVersion("grant-1", 4)],
            9,
            "session-1",
            "coordination-1");

    private sealed class RecordingAuthority : IAdminTeamAccessAuthority
    {
        public AuthorizationDeniedException? Denial { get; init; }

        public AdminTeamMembersResult? Members { get; init; }

        public AdminPendingInvitationsResult? Pending { get; init; }

        public AdminIssuedInvitation? Issued { get; init; }

        public AdminRevokeMemberResult? Revoke { get; init; }

        public AdminNarrowMemberGrantResult? Narrow { get; init; }

        public string? NarrowTenantId { get; private set; }

        public string? NarrowGrantId { get; private set; }

        public IReadOnlyCollection<string>? NarrowPermissions { get; private set; }

        public string? MembersHandle { get; private set; }

        public string? MembersTenantId { get; private set; }

        public string? PendingTenantId { get; private set; }

        public string? IssueTenantId { get; private set; }

        public string? RevokeTenantId { get; private set; }

        public string? RevokeGrantId { get; private set; }
        public string? RevokeSuccessorPrincipalId { get; private set; }

        public Task<AdminTeamMembersResult?> ListMembersAsync(
            string selectedSessionHandle, string tenantId, CancellationToken cancellationToken = default)
        {
            MembersHandle = selectedSessionHandle;
            MembersTenantId = tenantId;
            return Task.FromResult(Members);
        }

        public Task<AdminPendingInvitationsResult?> ListPendingInvitationsAsync(
            string selectedSessionHandle, string tenantId, CancellationToken cancellationToken = default)
        {
            PendingTenantId = tenantId;
            return Task.FromResult(Pending);
        }

        public Task<AdminIssuedInvitation?> IssueInvitationAsync(
            string selectedSessionHandle,
            string tenantId,
            IReadOnlyCollection<string> requestedPermissions,
            string idempotencyKey,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default)
        {
            IssueTenantId = tenantId;
            if (Denial is not null) return Task.FromException<AdminIssuedInvitation?>(Denial);
            return Task.FromResult(Issued);
        }

        public Task<AdminRevokeMemberResult?> RevokeMemberGrantAsync(
            string selectedSessionHandle,
            string tenantId,
            string grantId,
            AuthorizationWriteContext authority,
            string? successorPrincipalId = null,
            CancellationToken cancellationToken = default)
        {
            RevokeTenantId = tenantId;
            RevokeGrantId = grantId;
            RevokeSuccessorPrincipalId = successorPrincipalId;
            if (Denial is not null) return Task.FromException<AdminRevokeMemberResult?>(Denial);
            return Task.FromResult(Revoke);
        }

        // Ticket 362 - the narrow route's recording leg.
        public Task<AdminNarrowMemberGrantResult?> NarrowMemberGrantAsync(
            string selectedSessionHandle,
            string tenantId,
            string grantId,
            IReadOnlyCollection<string> narrowedPermissions,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default)
        {
            NarrowTenantId = tenantId;
            NarrowGrantId = grantId;
            NarrowPermissions = narrowedPermissions;
            if (Denial is not null) return Task.FromException<AdminNarrowMemberGrantResult?>(Denial);
            return Task.FromResult(Narrow);
        }
    }

    private sealed class RecordingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public bool AcceptSelected { get; init; } = true;

        public string? RotatedSelectedHandle { get; private set; }

        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(AcceptSelected);

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle)
        {
            RotatedSelectedHandle = selectedHandle;
            context.Response.Headers[WebAntiforgeryPolicy.HeaderName] = "replacement-token";
            return Task.FromResult(true);
        }

        public void EmitToken(HttpResponse response, string token) =>
            response.Headers[WebAntiforgeryPolicy.HeaderName] = token;

        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }

    private sealed class SuccessfulLogoutAuthority : IWebSelectedSessionLogoutAuthority
    {
        public Task<bool> LogoutAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// The legacy web-session authority for a SELECTED-ONLY sign-out. The selected branch consults
    /// it so a holder of both cookies is not left with a live legacy session, but this test's
    /// context carries only <c>__Host-hl-selected</c> — so it must find no legacy credential and
    /// expire no legacy cookie. Every other member throws: nothing else here may reach it.
    /// </summary>
    private sealed class SelectedOnlyLegacyAuthority : INodeWebSessionAuthority
    {
        private static InvalidOperationException Violation() =>
            new("audience violation: a selected-session sign-out reached the LEGACY authority.");

        public bool IsEnabled => true;

        public Task<bool> TryAuthenticateAsync(HttpContext context) => throw Violation();

        public Task<WebLoginAttemptResult> LoginAsync(string? username, string? password, CancellationToken ct) =>
            throw Violation();

        // No legacy credential is present in this context, so the real authority would report false
        // and the route would skip the legacy cookie expiry.
        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) =>
            Task.FromResult(false);

        public void IssueSessionCookie(HttpContext context, WebLoginResult login) => throw Violation();

        public void ClearSessionCookie(HttpContext context) =>
            throw new InvalidOperationException(
                "audience violation: a selected-only sign-out expired the LEGACY cookie.");

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            throw Violation();
    }

    private sealed class RealAntiforgeryFixture : IAsyncDisposable
    {
        private readonly string _path;

        private RealAntiforgeryFixture(
            string path,
            WebAntiforgeryPolicy policy,
            string selectedHandle)
        {
            _path = path;
            Policy = policy;
            SelectedHandle = selectedHandle;
        }

        internal WebAntiforgeryPolicy Policy { get; }

        internal string SelectedHandle { get; }

        internal static async Task<RealAntiforgeryFixture> CreateAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"admin-antiforgery-{Guid.NewGuid():N}.db");
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(path);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }

            var clock = new FixedTimeProvider();
            var store = new WebAntiforgeryStateStore(factory, clock);
            var issue = await store.RotateAsync(
                WebCookieAudience.SelectedSession,
                "account-1",
                "session-1",
                "coordination-1",
                ExpiresAt);
            Assert.NotNull(issue);

            const string selectedHandle = AdminTeamAccessRoutesTests.SelectedHandle;
            await using (var context = factory.CreateDbContext())
            {
                context.UserSessions.Add(new WebUserSessionRecord(
                    "session-1",
                    "account-1",
                    2,
                    "tenant-1",
                    "membership-1",
                    4,
                    "principal-1",
                    "party-1",
                    [new PinnedGrantOwnerVersion("grant-1", 4)],
                    9,
                    WebAntiforgeryStateStore.Digest(selectedHandle),
                    issue.State.AntiforgeryStateId,
                    "coordination-1",
                    Now,
                    Now.AddMinutes(30),
                    ExpiresAt,
                    1));
                await context.SaveChangesAsync();
            }

            return new RealAntiforgeryFixture(
                path,
                new WebAntiforgeryPolicy(
                    factory,
                    new WebSelectedSessionStore(factory),
                    store,
                    clock),
                selectedHandle);
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
