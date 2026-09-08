namespace Harborline.Api.Foundation.DataExchangeDefinitions;

/// <summary>
/// Defines the engine-owned admission seam for exchange kinds and their settings payloads.
/// Hosts implement this interface by adapting their registered exchange surfaces; this package
/// intentionally supplies no descriptor registry implementation.
/// </summary>
public interface IDataExchangeDefinitionDescriptorRegistry
{
    /// <summary>
    /// Admits a canonical data-exchange definition or throws a domain-specific exception before persistence.
    /// </summary>
    /// <param name="definition">The canonical definition to admit.</param>
    /// <param name="cancellationToken">A token that cancels admission.</param>
    /// <returns>A value task that completes when admission succeeds.</returns>
    ValueTask AdmitAsync(
        DataExchangeDefinition definition,
        CancellationToken cancellationToken = default);
}
