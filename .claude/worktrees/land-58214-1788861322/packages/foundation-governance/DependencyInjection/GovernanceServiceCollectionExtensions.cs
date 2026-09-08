using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Governance.Admission;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Governance.Definitions;
using Harborline.Api.Foundation.Forms;

namespace Harborline.Api.Foundation.Governance.DependencyInjection;

/// <summary>
/// DI scaffolding for the SPINE-2 governance layer (ADR 0140 D2). Registers the resolver,
/// the fail-closed admission validator, the four-PEP enforcer, and the greenfield bridges.
/// </summary>
/// <remarks>
/// The PEP enforcer depends on the BUILT primitives (<c>IFieldEncryptor</c> /
/// <c>ISubjectFieldEncryptor</c> / <c>IRetentionPolicyResolver</c> /
/// <c>IDataResidencyEnforcer</c> / <c>IAuditLog</c> / <c>ISubjectErasureService</c>) — the
/// host must register those via their own packages' DI (recovery, security-policy,
/// mission-space-regulatory, assets-audit). This method registers ONLY the governance layer.
/// <para>
/// <see cref="IConsentGate"/> is ALWAYS <see cref="TenantConsentGate"/> — there is one consent gate and
/// it reads records (ticket 213). The replaceable part is the STORE: it defaults to the fail-closed
/// <see cref="NoConsentRecordsStore"/>, and a host that persists consent registers its own
/// <see cref="ITenantConsentStore"/> FIRST (TryAdd leaves it in place). The default is deny, never a
/// permissive null.
/// </para>
/// </remarks>
public static class GovernanceServiceCollectionExtensions
{
    /// <summary>Register the SPINE-2 governance services (all replaceable via TryAdd).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="custom">Optional custom policy bindings layered over the predefined seed.</param>
    public static IServiceCollection AddHarborlineGovernance(
        this IServiceCollection services,
        IEnumerable<PolicyBinding>? custom = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<IRestrictingDefinitionKindValidator>(RestrictingDefinitionKindValidator.Shared);
        services.TryAddSingleton<IPolicyRegistry>(sp =>
            new InMemoryPolicyRegistry(
                includePredefined: true,
                custom,
                sp.GetRequiredService<IRestrictingDefinitionKindValidator>()));
        services.TryAddSingleton<IAspectResolver, AspectResolver>();
        services.TryAddSingleton<IPolicyAdmissionValidator>(sp =>
            new PolicyAdmissionValidator(
                sp.GetRequiredService<IAspectResolver>(),
                sp.GetRequiredService<IPolicyRegistry>(),
                restrictingKinds: sp.GetRequiredService<IRestrictingDefinitionKindValidator>()));

        // Greenfield bridges.
        services.TryAddSingleton<IFieldClassAuditEventClassMap>(_ => new DefaultFieldClassAuditEventClassMap());
        services.TryAddSingleton<IResidencyEligibilityResolver, DefaultResidencyEligibilityResolver>();
        services.TryAddSingleton<ILegalHoldRegistry, InMemoryLegalHoldRegistry>();
        // Definition envelopes use the authoritative recovery hold registry. Keep a safe in-memory
        // default for lightweight compositions; the production host's durable registration wins.
        services.TryAddSingleton<Harborline.Api.Foundation.Recovery.LegalHold.ILegalHoldStore,
            Harborline.Api.Foundation.Recovery.LegalHold.InMemoryLegalHoldStore>();
        services.TryAddSingleton<Harborline.Api.Foundation.Recovery.LegalHold.ILegalHoldRegistry,
            Harborline.Api.Foundation.Recovery.LegalHold.LegalHoldRegistry>();
        services.TryAddSingleton<IRetentionShredScheduler>(sp =>
            new DefaultRetentionShredScheduler(sp.GetRequiredService<ILegalHoldRegistry>()));
        services.TryAddSingleton<IFormDefinitionEnvelopeResolver>(sp =>
            new FormDefinitionEnvelopeResolver(
                sp.GetRequiredService<IAspectResolver>(),
                sp.GetRequiredService<IFieldClassAuditEventClassMap>(),
                sp.GetRequiredService<Harborline.Api.Foundation.SecurityPolicy.Retention.IRetentionPolicyResolver>(),
                sp.GetRequiredService<Harborline.Api.Foundation.Recovery.LegalHold.ILegalHoldRegistry>()));
        services.TryAddSingleton<IFormDefinitionLegalHoldValidator, FormDefinitionLegalHoldValidator>();

        // Fail-closed consent default — a durable store overrides by registering first.
        services.TryAddSingleton<ITenantConsentStore, NoConsentRecordsStore>();
        services.TryAddSingleton<TenantConsentGate>(sp => new TenantConsentGate(
            sp.GetRequiredService<ITenantConsentStore>(),
            sp.GetRequiredService<Harborline.Api.Foundation.Assets.Audit.IAuditLog>()));
        services.TryAddSingleton<IConsentGate>(sp => sp.GetRequiredService<TenantConsentGate>());

        // The four PEPs.
        services.TryAddSingleton<IFieldPolicyEnforcer, FieldPolicyEnforcer>();

        return services;
    }
}
