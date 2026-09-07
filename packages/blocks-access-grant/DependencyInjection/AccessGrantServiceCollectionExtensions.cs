using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Macaroons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Blocks.AccessGrant.DependencyInjection;

public static class AccessGrantServiceCollectionExtensions
{
    public static IServiceCollection AddAccessGrantModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InMemoryAuthorizationBootstrapFence>();
        services.TryAddSingleton<InMemoryGrantStore>(sp => new InMemoryGrantStore(
            sp.GetRequiredService<InMemoryAuthorizationBootstrapFence>()));
        services.TryAddSingleton<IGrantStore>(sp => sp.GetRequiredService<InMemoryGrantStore>());
        // One vocabulary instance behind both seams: the pack seed projector installs role names
        // through the store, and every reader (admission, gate, catalogue) sees them at once.
        services.TryAddSingleton(_ => new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions));
        services.TryAddSingleton<IRoleVocabularyStore>(sp => sp.GetRequiredService<InMemoryRoleVocabulary>());
        services.TryAddSingleton<IRoleVocabularyReader>(sp => sp.GetRequiredService<IRoleVocabularyStore>());
        services.TryAddSingleton<AuthorizationCapabilityBindingAdmission>();
        services.TryAddSingleton<AuthorizationDefinitionAdmission>();
        services.AddSingleton<RoleGateAdmission>();
        services.AddSingleton<IRoleGateAdmission>(sp => sp.GetRequiredService<RoleGateAdmission>());
        services.TryAddSingleton<InMemoryAuthorizationConfigurationStore>(sp =>
            new InMemoryAuthorizationConfigurationStore(
                sp.GetRequiredService<InMemoryAuthorizationBootstrapFence>()));
        services.TryAddSingleton<IAuthorizationConfigurationStore>(sp => sp.GetRequiredService<InMemoryAuthorizationConfigurationStore>());
        services.TryAddSingleton<AuthorizationConfigurationStateReader>(sp => sp.GetRequiredService<InMemoryAuthorizationConfigurationStore>());
        services.TryAddSingleton<IAuthorizationDefinitionReader>(sp => sp.GetRequiredService<InMemoryAuthorizationConfigurationStore>());
        services.TryAddSingleton<IAuthorizationDefinitionCatalogueReader>(sp =>
            sp.GetRequiredService<InMemoryAuthorizationConfigurationStore>());
        services.TryAddSingleton<IAuthorizationDefinitionAtomReader>(sp =>
            sp.GetRequiredService<IAuthorizationDefinitionReader>());
        services.TryAddSingleton<IHistoricalAuthorizationConfigurationReader>(sp =>
            sp.GetRequiredService<InMemoryAuthorizationConfigurationStore>());
        services.TryAddSingleton<IHistoricalAuthorizationResolver, HistoricalAuthorizationResolver>();
        // A live join IS a correct closure; materialisation is the host's optimisation, and the host registers
        // its EF reader first so it wins.
        services.TryAddSingleton<DefinitionJoinedAuthorizationReader>();
        services.TryAddSingleton<IAuthorizationClosureReader>(sp =>
            sp.GetRequiredService<DefinitionJoinedAuthorizationReader>());
        services.TryAddSingleton<IAuthorizationClosureSnapshotReader>(sp =>
            sp.GetRequiredService<DefinitionJoinedAuthorizationReader>());
        services.TryAddSingleton<IRecordStandingResolver, EmptyRecordStandingResolver>();
        services.TryAddSingleton<AuthorizationGate>();
        services.TryAddSingleton<AuthorizationDefinitionWriter>();
        services.TryAddSingleton<AccessGrantAuthorizationSeed>();
        return services;
    }

    public static IServiceProvider ValidateRoleGateAdmissionComposition(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var admissions = services.GetServices<IRoleGateAdmission>().ToArray();
        if (admissions.Length != 1
            || admissions[0] is not RoleGateAdmission admission
            || !ReferenceEquals(admission, services.GetRequiredService<RoleGateAdmission>()))
        {
            throw new InvalidOperationException(
                "authorization.role_gate.composition_invalid: IRoleGateAdmission must resolve exactly once to the shared RoleGateAdmission singleton.");
        }

        return services;
    }

    public static IServiceCollection ValidateRoleGateAdmissionRegistration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IRoleGateAdmission))
            .ToArray();
        if (registrations.Length != 1 || registrations[0].Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                "authorization.role_gate.composition_invalid: IRoleGateAdmission must resolve exactly once to the shared RoleGateAdmission singleton.");
        }

        return services;
    }

    public static IServiceCollection AddAccessGrantLifecycleHandlers(
        this IServiceCollection services,
        Func<IServiceProvider, IGrantIssuanceContext> issuanceContextFactory,
        Func<IServiceProvider, IGrantRevocationContext> revocationContextFactory)
    {
        services.AddSingleton<IWorkflowStepHandler>(sp => new GrantIssuanceHandler(
            issuanceContextFactory(sp), sp.GetRequiredService<IRoleVocabularyReader>(),
            sp.GetRequiredService<IAuthorizationClosureReader>()));
        services.AddSingleton<IWorkflowStepHandler>(sp => new GrantRevocationHandler(revocationContextFactory(sp)));
        return services;
    }

    public static IServiceCollection AddExternalAccessGrant(this IServiceCollection services, Func<TenantId, string> locationFor)
    {
        services.TryAddSingleton(sp => new ExternalGrantMinter(
            sp.GetRequiredService<IMacaroonIssuer>(), sp.GetRequiredService<IMacaroonVerifier>(), locationFor));
        return services;
    }
}
