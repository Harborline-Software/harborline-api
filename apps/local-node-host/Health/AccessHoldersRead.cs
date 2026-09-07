using System.Text.Json.Serialization;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Read projection of live grants, with canonical party and signed roster attribution.</summary>
internal static class AccessHoldersRead
{
    internal const string Route = AuthorizationAdminRoutes.RouteBase + "/holders";

    internal sealed record Holder(
        string PartyId, string Source, string GrantId, RoleReference Role,
        string Granter, string Scope, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttributionFailure);

    internal static async Task<IResult> ReadAsync(
        HttpContext http, TenantId tenant, TimeProvider time, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var authority = RequestAuthorization.Authority(http, tenant, time);
        var denied = await RequestAuthorization.RefusalAsync(
            http, authority, TeamRolePermissions.MembersManage, RouteRecord.TheInstall, ct).ConfigureAwait(false);
        if (denied is not null) return denied;

        var services = http.RequestServices;
        var grants = await services.GetRequiredService<IGrantStore>().SnapshotAsync(tenant, ct).ConfigureAwait(false);
        var roster = await services.GetRequiredService<IVerifiedTenantRosterReader>().ReadAsync(tenant, ct)
            .ConfigureAwait(false);
        var parties = services.GetRequiredService<ICanonicalPrincipalPartyReader>();
        var rosterParties = roster.Members.Select(member => member.PartyId).ToHashSet(StringComparer.Ordinal);
        var holders = new List<Holder>();
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
        }
        return Results.Ok(new { holders });
    }
}
