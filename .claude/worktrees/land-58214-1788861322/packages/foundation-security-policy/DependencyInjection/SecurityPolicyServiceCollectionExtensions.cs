using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.SecurityPolicy.Issuance;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.SecurityPolicy.DependencyInjection;

/// <summary>
/// DI scaffolding for the security-policy package per ADR 0068 §9.
/// </summary>
/// <remarks>
/// See §GC.1 in ADR 0068 (docs/adrs/0068-tenant-security-policy.md).
/// Enforcement behavior in this package intersects HIPAA, PCI-DSS,
/// SOC 2, GDPR, and the EU AI Act. The presets and defaults are
/// informed guidance, NOT legal advice. Deployers MUST obtain
/// qualified legal counsel before configuring enforcement behavior
/// for production use.
/// <para>
/// <see cref="AddHarborlineSecurityPolicy"/> wires the §3.1
/// non-replaceable <see cref="ISecurityPolicyApprovalFloorProvider"/>
/// via <c>AddSingleton&lt;TService, TImpl&gt;()</c> (NOT
/// <c>TryAddSingleton</c>) so plugin authors cannot shadow the
/// platform floor, plus the <see cref="ISecurityPolicyIssuer"/> +
/// options surface.
/// </para>
/// <para>
/// The Phase 1 <c>policyLoader</c> Func shim mirrors PR 3's
/// <c>DefaultSecurityPolicyEnforcer</c> ctor seam; PR 3b.4 lands
/// <c>ITenantSecurityPolicyLoader</c> as a proper interface and the
/// shim falls away.
/// </para>
/// </remarks>
public static class SecurityPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Register the security-policy issuer surface.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="policyLoader">
    /// Per-tenant active-policy loader (Phase 1 shim — see ADR 0068 §9).
    /// PR 3b.4 will introduce <c>ITenantSecurityPolicyLoader</c> and
    /// supersede this parameter.
    /// </param>
    /// <param name="configure">Optional <see cref="SecurityPolicyIssuerOptions"/> configuration.</param>
    public static IServiceCollection AddHarborlineSecurityPolicy(
        this IServiceCollection services,
        Func<TenantId, CancellationToken, ValueTask<TenantSecurityPolicy>> policyLoader,
        Action<SecurityPolicyIssuerOptions>? configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (policyLoader is null) throw new ArgumentNullException(nameof(policyLoader));

        services.TryAddSingleton<IRestrictingDefinitionKindValidator>(RestrictingDefinitionKindValidator.Shared);
        services.AddOptions<SecurityPolicyIssuerOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // §2.1.1 non-replaceable floor — AddSingleton (NOT TryAdd) so plugin
        // authors cannot shadow the platform 5-invariant floor.
        services.AddSingleton<ISecurityPolicyApprovalFloorProvider, DefaultSecurityPolicyApprovalFloorProvider>();

        // The issuer itself is replaceable so hosts can compose adapters
        // (logging, metrics, retry) by registering an alternate impl FIRST
        // then calling this method.
        services.TryAddSingleton<ISecurityPolicyIssuer>(sp => new DefaultSecurityPolicyIssuer(
            validators: sp.GetServices<Validation.ISecurityPolicyValidator>(),
            standingOrderIssuer: sp.GetRequiredService<Harborline.Api.Foundation.Wayfinder.IStandingOrderIssuer>(),
            approvalFloor: sp.GetRequiredService<ISecurityPolicyApprovalFloorProvider>(),
            principalResolver: sp.GetRequiredService<Harborline.Api.Foundation.Ship.Common.IActorPrincipalResolver>(),
            roleSource: sp.GetRequiredService<Harborline.Api.Foundation.Ship.Common.IShipRoleAssignmentSource>(),
            auditTrail: sp.GetRequiredService<Harborline.Api.Kernel.Audit.IAuditTrail>(),
            signer: sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationSigner>(),
            time: sp.GetRequiredService<TimeProvider>(),
            policyLoader: policyLoader,
            options: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityPolicyIssuerOptions>>()));

        // PR 3b.2 — retention enforcement surface. Both registered as Singletons
        // since both are stateless after construction; both are replaceable via
        // TryAddSingleton so hosts can compose decorators by registering an
        // alternate impl FIRST then calling this method.
        services.TryAddSingleton<Retention.IRetentionPolicyResolver>(sp =>
            new Retention.DefaultRetentionPolicyResolver(policyLoader));
        services.TryAddSingleton<Harborline.Api.Kernel.Audit.Retention.IAuditRetentionEnforcer>(sp =>
            new Retention.DefaultAuditRetentionEnforcer(
                sp.GetRequiredService<Retention.IRetentionPolicyResolver>(),
                sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}
