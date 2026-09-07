using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.FinancialSubLedger.Services;

namespace Harborline.Api.Blocks.FinancialSubLedger.DependencyInjection;

/// <summary>
/// Extension methods for registering sub-ledger services in a
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class SubLedgerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sub-ledger identity services. The <see cref="ISubLedgerReadModel"/>
    /// implementation is provided by the projection assembly
    /// (<c>blocks-financial-subledger-projection</c> PR-B); this registration
    /// is a placeholder that throws if the projection assembly is not wired up.
    ///
    /// <para>
    /// Composition roots that wire the full projection assembly should call
    /// <c>AddSubLedgerProjection()</c> from that package after this call.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSubLedgerIdentity(this IServiceCollection services)
    {
        // No concrete registration here — the identity assembly carries only the
        // master record and the ISubLedgerReadModel interface. The projection
        // assembly (PR-B) registers the concrete implementation.
        return services;
    }
}
