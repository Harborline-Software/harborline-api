using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// The single validate-stage admission for declarative role gates. It resolves both qualified
/// vocabularies through <see cref="IRoleVocabularyReader"/>. Health re-reads the published definition
/// stores, so persisted state — never process-local observation — is the source of truth.
/// </summary>
public sealed class RoleGateAdmission(
    IRoleVocabularyReader roles,
    IFormDefinitionStore? forms = null,
    IWorkflowDefinitionStore? workflows = null) : IRoleGateAdmission
{
    public async ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var findings = await InspectAsync(definition, ct).ConfigureAwait(false);
        if (findings.Count > 0) throw new RoleGateAdmissionException(findings[0]);
    }

    public async ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default)
    {
        var definitions = new List<RoleGatedDefinition>();
        if (forms is not null)
        {
            await foreach (var definition in forms.ListPublishedAsync(ct).ConfigureAwait(false))
            {
                definitions.Add(AuthorizedFormDefinitionLifecycle.ToRoleGated(definition));
            }
        }
        if (workflows is not null)
        {
            await foreach (var definition in workflows.ListPublishedAsync(ct).ConfigureAwait(false))
            {
                definitions.Add(AuthorizedWorkflowDefinitionLifecycle.ToRoleGated(definition));
            }
        }
        var findings = new List<RoleGateFinding>();
        foreach (var definition in definitions
                     .OrderBy(item => item.DefinitionKind, StringComparer.Ordinal)
                     .ThenBy(item => item.DefinitionId, StringComparer.Ordinal)
                     .ThenBy(item => item.Version, StringComparer.Ordinal))
        {
            findings.AddRange(await InspectAsync(definition, ct).ConfigureAwait(false));
        }

        return findings;
    }

    private async ValueTask<IReadOnlyList<RoleGateFinding>> InspectAsync(
        RoleGatedDefinition definition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DefinitionKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);
        ArgumentNullException.ThrowIfNull(definition.Owner);
        ArgumentNullException.ThrowIfNull(definition.Gates);

        var findings = new List<RoleGateFinding>();
        foreach (var gate in definition.Gates)
        {
            ct.ThrowIfCancellationRequested();
            if (gate.Standing is not null)
            {
                // Standings are record-derived facts, so platform and the record-owning tenant may
                // declare them. A vendor package cannot claim a tenant record fact as package content.
                if (definition.Owner.Kind == RoleGatedDefinitionOwnerKind.VendorPackage)
                    findings.Add(Finding(definition, gate, RoleGateAdmissionRules.StandingPlatformOnly));
                continue;
            }

            var roleReference = gate.Role
                ?? throw new ArgumentException("A declarative gate must name exactly one role or standing.", nameof(definition));
            var role = await roles.ResolveAsync(roleReference, ct).ConfigureAwait(false);
            if (role is null)
            {
                findings.Add(Finding(definition, gate, RoleGateAdmissionRules.UnresolvedRole));
                continue;
            }

            var allowed = definition.Owner.Kind switch
            {
                RoleGatedDefinitionOwnerKind.Platform => role.Owner.Kind == RoleOwnerKind.Platform,
                RoleGatedDefinitionOwnerKind.VendorPackage =>
                    (role.Owner.Kind == RoleOwnerKind.Tenant
                     && string.Equals(role.Owner.OwnerId, definition.Owner.Tenant.Value, StringComparison.Ordinal))
                    || (role.Owner.Kind == RoleOwnerKind.Package
                        && string.Equals(role.Owner.OwnerId, definition.Owner.PackageId, StringComparison.Ordinal)),
                RoleGatedDefinitionOwnerKind.Tenant =>
                    role.Owner.Kind == RoleOwnerKind.Platform
                    || (role.Owner.Kind == RoleOwnerKind.Tenant
                        && string.Equals(role.Owner.OwnerId, definition.Owner.Tenant.Value, StringComparison.Ordinal)),
                _ => false,
            };
            if (!allowed)
            {
                findings.Add(Finding(
                    definition,
                    gate,
                    definition.Owner.Kind switch
                    {
                        RoleGatedDefinitionOwnerKind.Platform => RoleGateAdmissionRules.PlatformRoleOrStandingOnly,
                        RoleGatedDefinitionOwnerKind.VendorPackage => RoleGateAdmissionRules.VendorOwnOrTenantRoleOnly,
                        _ => RoleGateAdmissionRules.TenantOrPlatformRoleOnly,
                    }));
            }
        }

        return findings;
    }

    private static RoleGateFinding Finding(
        RoleGatedDefinition definition,
        DeclarativeGateReference gate,
        string rule) => new(
            $"authorization.role_gate.{rule}",
            definition.DefinitionKind,
            definition.DefinitionId,
            definition.Version,
            gate.Gate,
            gate.Subject,
            rule,
            definition.Owner.Kind,
            definition.Owner.PackageId,
            definition.Owner.Tenant.Value);
}
