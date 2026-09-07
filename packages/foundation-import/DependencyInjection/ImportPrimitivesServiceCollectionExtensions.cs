using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Import.Census;

namespace Harborline.Api.Foundation.Import.DependencyInjection;

/// <summary>
/// DI registration for the ERPNext-import primitives (ADR 0100 A0). Mirrors the
/// <c>AddHarborlineKernelAudit</c> extension shape.
/// </summary>
/// <remarks>
/// A0 ships the outcome types (<c>ImportOutcome&lt;T&gt;</c>, <c>ImportFailure</c>),
/// the <c>ImportCensus</c> primitive, and the <c>ISourceReader</c> seam. The
/// concrete <c>MariaDbDumpSourceReader</c> is constructed per-run from a dump-file
/// path by the CLI driver (A7) rather than DI-registered as a singleton, because
/// the source path is a per-invocation flag — so this extension registers the
/// pass-scoped <see cref="ImportCensus"/> factory only. A0 deliberately keeps the
/// DI surface minimal; the orchestration wiring lands with A7.
/// </remarks>
public static class ImportPrimitivesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-pass <see cref="ImportCensus"/> as a transient factory
    /// (one census instance per pass; never shared across passes).
    /// </summary>
    public static IServiceCollection AddHarborlineImportPrimitives(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddTransient<ImportCensus>();
        return services;
    }
}
