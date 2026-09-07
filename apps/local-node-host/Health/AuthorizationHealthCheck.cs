using Harborline.Api.Foundation.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Reports active declarative gates whose role ownership no longer matches this installation.</summary>
public sealed class AuthorizationHealthCheck(IRoleGateAdmission admission) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var findings = await admission.InspectActiveAsync(cancellationToken).ConfigureAwait(false);
        if (findings.Count == 0)
            return HealthCheckResult.Healthy("All active declarative role gates resolve to permitted owners.");

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["authorizationFindings"] = findings.Select(finding => new Dictionary<string, object?>
            {
                ["code"] = finding.Code,
                ["definitionKind"] = finding.DefinitionKind,
                ["definitionId"] = finding.DefinitionId,
                ["version"] = finding.Version,
                ["gate"] = finding.Gate,
                ["role"] = finding.Subject,
                ["rule"] = finding.Rule,
                ["definitionOwnerKind"] = finding.DefinitionOwnerKind.ToString(),
                ["packageId"] = finding.PackageId,
                ["tenantId"] = finding.TenantId,
            }).ToArray(),
        };
        return HealthCheckResult.Degraded(
            $"Authorization catalogue contains {findings.Count} invalid active role reference(s).",
            data: data);
    }
}
