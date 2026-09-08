using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.Payroll.Services;

namespace Harborline.Api.Blocks.Payroll.DependencyInjection;

/// <summary>
/// Extension methods for registering payroll services in a
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class PayrollServiceCollectionExtensions
{
    /// <summary>
    /// Registers all v1 payroll ledger services using in-memory implementations.
    /// Suitable for testing, local development, and the v1 manual-entry mode.
    /// </summary>
    /// <remarks>
    /// Services registered:
    /// <list type="bullet">
    ///   <item><see cref="IEmployeeRepository"/> → <see cref="InMemoryEmployeeRepository"/> (singleton)</item>
    ///   <item><see cref="IPayRunRepository"/> → <see cref="InMemoryPayRunRepository"/> (singleton)</item>
    ///   <item><see cref="IFilingObligationRepository"/> → <see cref="InMemoryFilingObligationRepository"/> (singleton)</item>
    ///   <item><see cref="IPayRunPostingService"/> → <see cref="PayRunPostingService"/> (scoped)</item>
    /// </list>
    /// The caller is responsible for registering <c>IJournalPostingService</c> and
    /// <c>ITenantContext</c> in the composition root — typically via
    /// <c>AddInMemoryAccounting()</c> and the Bridge tenant-context wiring.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddInMemoryPayroll(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmployeeRepository, InMemoryEmployeeRepository>();
        services.TryAddSingleton<IPayRunRepository, InMemoryPayRunRepository>();
        services.TryAddSingleton<IFilingObligationRepository, InMemoryFilingObligationRepository>();
        services.TryAddScoped<IPayRunPostingService, PayRunPostingService>();
        return services;
    }
}
