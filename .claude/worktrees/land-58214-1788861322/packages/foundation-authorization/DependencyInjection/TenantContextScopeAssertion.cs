using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.Foundation.Authorization.DependencyInjection;

/// <summary>
/// Startup assertion for ADR 0091 R2 amendment A1. Verifies that the four
/// tenant-context-related interface bindings
/// (<see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>,
/// <see cref="ICurrentUser"/>, <see cref="IAuthorizationContext"/>,
/// <see cref="ITenantContext"/> facade) all resolve to the SAME scoped
/// instance. A future DI registration that diverges any one of the bindings
/// (e.g., adds a second <c>AddScoped&lt;IAuthorizationContext, OtherImpl&gt;</c>
/// after <see cref="TenantContextServiceCollectionExtensions.AddHarborlineTenantContext{TConcrete}"/>)
/// trips the assertion at app startup — failing closed before the first
/// request can hit the confused-deputy seam.
/// </summary>
/// <remarks>
/// The assertion creates one short-lived scope at startup, resolves all four
/// interfaces from it, and compares by reference equality. Errors throw an
/// <see cref="InvalidOperationException"/> from <see cref="StartAsync"/>,
/// which halts the host (per the standard generic-host behavior on hosted-
/// service startup failure).
/// </remarks>
public sealed class TenantContextScopeAssertion : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TenantContextScopeAssertion(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var multiTenancy = scope.ServiceProvider.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationContext>();
        var facade = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        if (!ReferenceEquals(multiTenancy, currentUser)
            || !ReferenceEquals(currentUser, authorization)
            || !ReferenceEquals(authorization, facade))
        {
            throw new InvalidOperationException(
                "ADR 0091 R2 amendment A1: tenant-context interface bindings must resolve to the SAME scoped instance. "
                + $"Got: MultiTenancy={ToHandle(multiTenancy)}, CurrentUser={ToHandle(currentUser)}, "
                + $"Authorization={ToHandle(authorization)}, Facade={ToHandle(facade)}. "
                + "Use AddHarborlineTenantContext<TConcrete>() and do not register the constituent interfaces separately.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string ToHandle(object o) => $"{o.GetType().Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)}";
}
