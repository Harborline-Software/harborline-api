using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.FinancialSubLedger.Projection.Services;
using Harborline.Api.Blocks.FinancialSubLedger.Services;

namespace Harborline.Api.Blocks.FinancialSubLedger.Projection.DependencyInjection;

/// <summary>
/// Extension methods for registering the sub-ledger projection services (ADR 0120 PR-B).
/// </summary>
public static class SubLedgerProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sub-ledger projection (HIGH-tier) services:
    /// <list type="bullet">
    ///   <item><see cref="InMemorySubLedgerAccountRepository"/> as <see cref="ISubLedgerAccountRepository"/> (singleton).</item>
    ///   <item><see cref="SubLedgerReadModel"/> as <see cref="ISubLedgerReadModel"/> (scoped).</item>
    /// </list>
    ///
    /// <para>
    /// Call this from the composition root AFTER <c>AddSubLedgerIdentity()</c> from the
    /// LOW identity assembly (<c>blocks-financial-subledger</c>), and AFTER the AR / AP /
    /// Payments repositories are registered (the projection depends on them).
    /// </para>
    ///
    /// <para>
    /// <b>Tenant isolation:</b> <see cref="SubLedgerReadModel"/> takes
    /// <c>TenantId</c> as an explicit parameter on every method — it does NOT
    /// rely on an ambient <c>ITenantContext</c> to prevent the per-request
    /// context from leaking across tenant reads (PR-B SPOT-CHECK requirement).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSubLedgerProjection(this IServiceCollection services)
    {
        services.AddSingleton<ISubLedgerAccountRepository, InMemorySubLedgerAccountRepository>();
        services.AddScoped<ISubLedgerReadModel, SubLedgerReadModel>();
        return services;
    }
}
