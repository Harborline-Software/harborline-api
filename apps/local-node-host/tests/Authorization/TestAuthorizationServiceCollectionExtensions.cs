using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class TestAuthorizationServiceCollectionExtensions
{
    internal static IServiceCollection AddTestKernelClock(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        return services;
    }

    internal static IServiceCollection AddFrozenKernelClock(
        this IServiceCollection services,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(clock);
        return services;
    }

    /// <summary>
    /// T-986: an in-memory kernel audit trail for test graphs that compose the shipping enrollment audit but test
    /// something else. Registered first, it wins the composition's TryAdd registrations, so the graph needs no
    /// local-node.db store. The shipping host registers no in-memory trail.
    /// </summary>
    internal static IServiceCollection AddTestInMemoryKernelAudit(this IServiceCollection services)
    {
        services.TryAddSingleton<Harborline.Api.Kernel.Audit.InMemoryAuditTrail>();
        services.TryAddSingleton<Harborline.Api.Kernel.Audit.IAuditTrail>(sp =>
            sp.GetRequiredService<Harborline.Api.Kernel.Audit.InMemoryAuditTrail>());
        services.TryAddSingleton<Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail>(sp =>
            sp.GetRequiredService<Harborline.Api.Kernel.Audit.InMemoryAuditTrail>());
        services.TryAddSingleton<Harborline.Api.Kernel.Audit.ICapturedAuditTrail>(sp =>
            sp.GetRequiredService<Harborline.Api.Kernel.Audit.InMemoryAuditTrail>());
        services.TryAddSingleton<Harborline.Api.Kernel.Audit.IAuditEventReader>(sp =>
            new Harborline.Api.Kernel.Audit.InMemoryAuditEventReader(
                sp.GetRequiredService<Harborline.Api.Kernel.Audit.InMemoryAuditTrail>(),
                sp.GetRequiredService<Harborline.Api.Kernel.Audit.IAuditTrail>(),
                sp.GetRequiredService<IOperationSigner>()));
        return services;
    }

    internal static IServiceCollection AddTestAuthorizationGate(this IServiceCollection services)
    {
        services.AddTestKernelClock();
        services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var roleGate = Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.RoleGate();
        services.AddSingleton(roleGate);
        services.AddSingleton<Harborline.Api.Foundation.Authorization.IRoleGateAdmission>(roleGate);
        return services;
    }

    /// <summary>
    /// Composes the production forms graph for tests with the same kernel-audit module ordering as
    /// the shipping host. Existing test-specific audit-trail registrations win because the SoD
    /// composition uses TryAdd for the trail interfaces.
    /// </summary>
    internal static IServiceCollection AddTestNodeForms(
        this IServiceCollection services,
        string? hostJurisdiction = null,
        Action<IServiceCollection,
            Func<IServiceProvider, Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore>,
            Func<IServiceProvider, Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork>>?
            configureWriters = null)
    {
        services.TryAddSingleton(_ => new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));
        services.TryAddSingleton<IOperationSigner>(sp =>
            sp.GetRequiredService<NodePrincipalSigner>().Signer);
        services.AddTestInMemoryKernelAudit();
        services.AddEnrollmentCompensatingControlAudit();
        return services.AddNodeForms(hostJurisdiction, configureWriters);
    }
}
