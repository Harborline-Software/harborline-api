using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.Foundation.Authorization.DependencyInjection;

/// <summary>
/// DI helper for the ADR 0091 single-concrete-class tenant-context wiring.
/// Per Revision 2 amendment 5 / A1, sanctions the "single concrete class
/// provides all three (four) interfaces coherently" invariant in code rather
/// than relying on a doc comment.
/// </summary>
public static class TenantContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TConcrete"/> as the implementation of
    /// <see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>,
    /// <see cref="ICurrentUser"/>, <see cref="IAuthorizationContext"/>, and
    /// the facade <see cref="ITenantContext"/> — all four bindings resolve to
    /// the SAME scoped instance per request. Also installs an
    /// <see cref="IHostedService"/> startup assertion
    /// (<see cref="TenantContextScopeAssertion"/>) that verifies the
    /// same-instance invariant at app start; future DI changes that diverge
    /// the bindings fail startup immediately rather than producing the
    /// textbook confused-deputy seam (ADR 0091 R2 §"Out-of-scope-but-flagged"
    /// same-token invariant).
    /// </summary>
    public static IServiceCollection AddHarborlineTenantContext<TConcrete>(
        this IServiceCollection services)
        where TConcrete : class,
                          Harborline.Api.Foundation.MultiTenancy.ITenantContext,
                          ICurrentUser,
                          IAuthorizationContext,
                          ITenantContext
    {
        // One scoped backing registration; all four interface bindings
        // resolve through it. Uses GetRequiredService<TConcrete> to alias
        // the existing scoped instance rather than constructing a second one.
        services.AddScoped<TConcrete>();
        services.AddScoped<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(sp => sp.GetRequiredService<TConcrete>());
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<TConcrete>());
        services.AddScoped<IAuthorizationContext>(sp => sp.GetRequiredService<TConcrete>());
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TConcrete>());

        // TryAdd — host may have already wired the assertion if AddHarborlineTenantContext
        // is called more than once (which is itself a misconfiguration but doesn't
        // need to throw at registration time; the assertion will surface it).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TenantContextScopeAssertion>());

        return services;
    }
}
