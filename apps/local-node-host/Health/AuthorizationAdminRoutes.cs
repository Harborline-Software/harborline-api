using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Contracts;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Desktop-only administration surface over the authoritative authorization model.</summary>
public static class AuthorizationAdminRoutes
{
    public const string RouteBase = "/api/local-node/authorization";

    public static void Map(
        IEndpointRouteBuilder app,
        IRoleVocabularyReader vocabulary,
        IAuthorizationDefinitionCatalogueReader definitions,
        AuthorizationDefinitionWriter writer,
        IStandingRuleDefinitionStore standingRules,
        StandingCatalogue standings,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(standingRules);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Ticket 205 slice 4: the closure-backed gate, resolved at the point of use. Every route on this
        // surface administers the INSTALL's own authorization configuration — the role vocabulary, the
        // capability definitions, the standing catalogue — which is not a record, so the act is install-wide
        // (org:manage-settings is declared so in PermissionVocabulary, with the reason on the definition).
        // It returns the request's tenant with the verdict so each handler has ONE tenant resolution for the
        // whole request rather than a second, independently resolved one after the guard.
        async ValueTask<(TenantId Tenant, IResult? Denied)> SettingsAuthorityAsync(
            HttpContext http, CancellationToken ct)
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            return (tenant, await RequestAuthorization.RefusalAsync(
                http, tenant, Permission.OrgManageSettings, RouteRecord.TheInstall, ct).ConfigureAwait(false));
        }

        app.MapGet($"{RouteBase}/role-vocabulary", async (HttpContext http, CancellationToken ct) =>
        {
            var (tenant, denied) = await SettingsAuthorityAsync(http, ct).ConfigureAwait(false);
            if (denied is not null) return denied;
            var rows = await vocabulary.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(rows
                .Where(row => row.Owner.Kind != RoleOwnerKind.Tenant
                    || string.Equals(row.Owner.OwnerId, tenant.Value, StringComparison.Ordinal))
                .OrderBy(row => row.Role.Vocabulary, StringComparer.Ordinal)
                .ThenBy(row => row.Role.Name, StringComparer.Ordinal)
                .Select(ToDto)
                .ToArray());
        });

        app.MapGet($"{RouteBase}/capability-definitions", async (HttpContext http, CancellationToken ct) =>
        {
            var (tenant, denied) = await SettingsAuthorityAsync(http, ct).ConfigureAwait(false);
            if (denied is not null) return denied;
            var rows = await definitions.ListAsync(tenant, ct).ConfigureAwait(false);
            return Results.Ok(rows.Select(ToDto).ToArray());
        });

        app.MapGet($"{RouteBase}/capability-definitions/{{definitionId:guid}}/binding",
            async (Guid definitionId, HttpContext http, CancellationToken ct) =>
            {
                var (tenant, denied) = await SettingsAuthorityAsync(http, ct).ConfigureAwait(false);
                if (denied is not null) return denied;
                var row = await definitions.FindAsync(
                    tenant,
                    new AuthorizationCapabilityDefinitionId(definitionId),
                    ct).ConfigureAwait(false);
                return row is null
                    ? Results.NotFound(new { code = "authorization.definition_not_found" })
                    : Results.Ok(ToBindingDto(row));
            });

        app.MapPost($"{RouteBase}/capability-definitions/{{definitionId:guid}}/binding",
            async (Guid definitionId, NarrowAuthorizationBindingRequest? request, HttpContext http, CancellationToken ct) =>
            {
                var (tenant, denied) = await SettingsAuthorityAsync(http, ct).ConfigureAwait(false);
                if (denied is not null) return denied;
                if (!http.Request.Headers.TryGetValue(IdempotencyContract.HeaderName, out var key)
                    || string.IsNullOrWhiteSpace(key.ToString()))
                    return Results.BadRequest(new { code = "authorization.idempotency_key_required" });
                if (request?.SelectedRoles is null || string.IsNullOrWhiteSpace(request.Reason))
                    return Results.BadRequest(new { code = "authorization.binding_invalid" });

                var id = new AuthorizationCapabilityDefinitionId(definitionId);
                if (await definitions.FindAsync(tenant, id, ct).ConfigureAwait(false) is null)
                    return Results.NotFound(new { code = "authorization.definition_not_found" });

                try
                {
                    var selected = RoleBindingSet.From(request.SelectedRoles.Select(role =>
                        new RoleReference(role.Vocabulary, role.Name)));
                    // One server-derived actor and instant: the gate decides on them and the
                    // revision is stamped with them (ticket 199 slice 2).
                    var actor = new ActorId(NodeCallerParty.Resolve(http).Value);
                    var at = timeProvider.GetUtcNow();
                    var result = await writer.WriteAsync(
                        new NarrowCapabilityRoleBinding(
                            tenant,
                            id,
                            selected,
                            actor,
                            at,
                            new BindingChangeReason(request.Reason)),
                        new AuthorizationWriteContext(actor, tenant, at),
                        ct).ConfigureAwait(false);
                    var change = result.BindingChange
                        ?? throw new InvalidOperationException("The writer returned no binding result.");
                    return Results.Ok(new NarrowAuthorizationBindingResponse(
                        definitionId,
                        change.Revision.Revision,
                        ToDtos(change.EffectiveRoles),
                        change.Warning?.ToString(),
                        change.Revision.ChangedBy.Value,
                        change.Revision.ChangedAt,
                        change.Revision.Reason.Value));
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { code = "authorization.binding_invalid" });
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.Contains("outside the publisher ceiling", StringComparison.Ordinal)
                    || exception.Message.Contains("re-add", StringComparison.Ordinal))
                {
                    return Results.Conflict(new { code = "authorization.binding_widening_refused" });
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.Contains("changed after", StringComparison.Ordinal)
                    || exception.Message.Contains("concurrently", StringComparison.Ordinal))
                {
                    return Results.Conflict(new { code = "authorization.binding_stale" });
                }
            });

        app.MapGet($"{RouteBase}/standing-catalogue", async (HttpContext http, CancellationToken ct) =>
        {
            if ((await SettingsAuthorityAsync(http, ct).ConfigureAwait(false)).Denied is { } denied)
                return denied;
            var result = new List<StandingDefinitionCatalogueDto>();
            await foreach (var rule in standingRules.ListAsync(ct).ConfigureAwait(false))
            {
                var fields = new List<StandingFieldCatalogueDto>();
                foreach (var field in rule.InputFields.Order(StringComparer.Ordinal))
                {
                    var carrying = await standings.ListCarryingRecordTypesAsync(rule, field, ct).ConfigureAwait(false);
                    fields.Add(new StandingFieldCatalogueDto(
                        field,
                        carrying.Select(id => id.Value).Order(StringComparer.Ordinal).ToArray()));
                }
                result.Add(new StandingDefinitionCatalogueDto(
                    rule.RuleId, rule.RuleVersion, rule.Standing.Name, rule.RecordType, fields));
            }
            return Results.Ok(result
                .OrderBy(row => row.RuleId, StringComparer.Ordinal)
                .ThenBy(row => row.RuleVersion, StringComparer.Ordinal)
                .ToArray());
        });
    }

    private static RoleDefinitionDto ToDto(RoleDefinition role) => new(
        role.RoleDefinitionId.Value,
        new RoleReferenceDto(role.Role.Vocabulary, role.Role.Name),
        role.DisplayName,
        new RoleOwnerDto(role.Owner.Kind.ToString(), role.Owner.OwnerId),
        role.IsSealed);

    private static AuthorizationDefinitionDto ToDto(AuthorizationDefinitionBindingView row) => new(
        row.Definition.DefinitionId.Value,
        row.Definition.PublisherPackageId,
        row.Definition.Revision,
        new PermissionAtomDto(
            row.Definition.Atom.Operation.Value,
            row.Definition.Atom.Scope.Type.ToString(),
            row.Definition.Atom.Scope.Value),
        ToDtos(row.Definition.OfferedRoles),
        ToBindingDto(row));

    private static AuthorizationBindingDto ToBindingDto(AuthorizationDefinitionBindingView row) =>
        new(row.BindingRevision, ToDtos(row.EffectiveRoles), row.Warning?.ToString());

    private static IReadOnlyList<RoleReferenceDto> ToDtos(RoleBindingSet roles) =>
        roles.Roles.Select(role => new RoleReferenceDto(role.Vocabulary, role.Name)).ToArray();
}
