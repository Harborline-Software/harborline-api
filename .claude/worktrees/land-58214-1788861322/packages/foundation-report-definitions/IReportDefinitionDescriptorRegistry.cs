namespace Harborline.Api.Foundation.ReportDefinitions;

/// <summary>
/// Defines the engine-owned admission seam for report kinds and their parameter payloads.
/// Hosts implement this interface by adapting their registered report cartridges; this package
/// intentionally supplies no descriptor registry implementation.
/// </summary>
public interface IReportDefinitionDescriptorRegistry
{
    /// <summary>
    /// Admits a canonical report definition or throws a domain-specific exception before persistence.
    /// </summary>
    /// <param name="definition">The canonical definition to admit.</param>
    /// <param name="cancellationToken">A token that cancels admission.</param>
    /// <returns>A value task that completes when admission succeeds.</returns>
    ValueTask AdmitAsync(
        ReportDefinition definition,
        CancellationToken cancellationToken = default);
}
