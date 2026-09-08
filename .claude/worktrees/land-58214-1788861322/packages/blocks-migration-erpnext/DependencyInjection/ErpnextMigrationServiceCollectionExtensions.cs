using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Migration.Erpnext.Extraction;
using Harborline.Api.Foundation.Import.Extraction;

namespace Harborline.Api.Blocks.Migration.Erpnext.DependencyInjection;

/// <summary>
/// DI registration for the ERPNext extraction adapter (ADR 0100 A0).
/// </summary>
/// <remarks>
/// The <see cref="IErpnextSourceExtractor"/> is registered as a singleton.
/// The CLI host (A7) constructs the extractor per-run by supplying the
/// CIC-provided <c>--source-dump</c> path outside the repo tree
/// (ADR 0100 C4 / C9 — real financial data stays out of the repo).
/// </remarks>
public static class ErpnextMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IErpnextSourceExtractor"/> singleton as a
    /// <see cref="MariaDbDumpExtractor"/> constructed from the already-loaded
    /// <paramref name="reader"/>. The caller (A7 CLI) performs the async
    /// <c>MariaDbDumpSourceReader.LoadAsync(path)</c> before building the host, then
    /// passes the reader here so DI registration stays synchronous.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="reader">
    /// An already-loaded <see cref="ISourceReader"/> (typically a
    /// <see cref="MariaDbDumpSourceReader"/> loaded from the CIC-supplied dump).
    /// Never echoed; never stored outside the extractor (C9).
    /// </param>
    /// <returns>The service collection (for chaining).</returns>
    public static IServiceCollection AddErpnextExtraction(
        this IServiceCollection services,
        ISourceReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        services.AddSingleton<IErpnextSourceExtractor>(
            new MariaDbDumpExtractor(reader));

        return services;
    }
}
