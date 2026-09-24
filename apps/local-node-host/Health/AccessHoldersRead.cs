using System.Text.Json.Serialization;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Read projection of live grants, with canonical party and signed roster attribution.</summary>
internal static class AccessHoldersRead
{
    internal const string Route = AuthorizationAdminRoutes.RouteBase + "/holders";

    internal static ViewRequestDescriptor ReadRequest { get; } = new(
        "authorization.holders.read.v1", "GET", Route, "application/json",
        "desktop-plane-only", false, TeamRolePermissions.MembersManage, []);

    internal static ViewRequestDescriptor SelectedReadRequest { get; } = new(
        "authorization.holders.selected.read.v1", "GET", "/api/session/admin/grants/holders", "application/json",
        "selected-session", false, TeamRolePermissions.MembersManage, [])
        { RowsPointer = "/rows", RowIdentityPointer = "/grantId" };

    internal static void MapSelected(IEndpointRouteBuilder app, TimeProvider time) =>
        app.MapGet(SelectedReadRequest.RouteTemplate, (Delegate)((HttpContext http) => ReadSelectedAsync(http, time)));

    private static Task<IResult> ReadSelectedAsync(HttpContext http, TimeProvider time)
    {
        http.Response.Headers.CacheControl = "no-store";
        var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
        if (principal is null || string.IsNullOrWhiteSpace(http.Request.Cookies[WebSessionCookieNames.Selected]))
            return Task.FromResult<IResult>(Results.Unauthorized());
        return ReadAsync(http, principal.TenantId, time, http.RequestAborted);
    }

    internal sealed record Holder(
        string PartyId, string Source, string GrantId, RoleReference Role,
        string Granter, string Scope, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttributionFailure);

    /// <summary>Canonical shared-view row for the pack-defined <c>AccessGrant</c> entity.</summary>
    internal sealed record AccessGrantRow(
        string GrantId, string PrincipalId, string Role, string Scope, string Status);

    internal static async Task<IResult> ReadAsync(
        HttpContext http, TenantId tenant, TimeProvider time, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var authority = RequestAuthorization.Authority(http, tenant, time);
        var denied = await RequestAuthorization.RefusalAsync(
            http, authority, ReadRequest.AuthorizationCapability, RouteRecord.TheInstall, ct).ConfigureAwait(false);
        if (denied is not null) return denied;

        var services = http.RequestServices;
        var grants = await services.GetRequiredService<IGrantStore>().SnapshotAsync(tenant, ct).ConfigureAwait(false);
        var roster = await services.GetRequiredService<IVerifiedTenantRosterReader>().ReadAsync(tenant, ct)
            .ConfigureAwait(false);
        var parties = services.GetRequiredService<ICanonicalPrincipalPartyReader>();
        var rosterParties = roster.Members.Select(member => member.PartyId).ToHashSet(StringComparer.Ordinal);
        var holders = new List<Holder>();
        var rows = new List<AccessGrantRow>();
        foreach (var grant in grants.Where(grant => grant.IsActiveAt(authority.At)).OrderBy(grant => grant.GrantId.Value))
        {
            var party = await parties.ResolveAsync(tenant, new PrincipalUserId(grant.Subject.Value), ct)
                .ConfigureAwait(false);
            holders.Add(new Holder(
                party?.PartyId.Value ?? "UNATTRIBUTED",
                party is null ? "unattributed" : rosterParties.Contains(party.PartyId.Value) ? "roster" : "grant",
                grant.GrantId.ToString(), grant.Role, grant.GrantedBy.Value, grant.Scope.Value,
                grant.Validity.ValidFrom, grant.Validity.ValidTo,
                party is null
                    ? "No unique live party binding in this tenant: missing, tombstoned, detached, duplicated or wrong-tenant."
                    : null));
            rows.Add(new AccessGrantRow(
                grant.GrantId.ToString(), grant.Subject.Value,
                grant.Role.ToString(),
                grant.Scope.Value,
                "Active"));
        }
        return Results.Ok(new { holders, rows });
    }
}
