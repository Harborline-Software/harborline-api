namespace Harborline.Api.Foundation.ScheduleDefinitions;

/// <summary>
/// Defines the engine-owned admission seam for schedule kinds and their authoring-contract payloads.
/// Hosts implement this interface by adapting their existing scheduling authoring contract; this
/// package intentionally supplies no descriptor registry implementation.
/// </summary>
public interface IScheduleDefinitionDescriptorRegistry
{
    /// <summary>
    /// Admits a canonical schedule definition or throws a domain-specific exception before persistence.
    /// </summary>
    /// <param name="definition">The canonical definition to admit.</param>
    /// <param name="cancellationToken">A token that cancels admission.</param>
    /// <returns>A value task that completes when admission succeeds.</returns>
    ValueTask AdmitAsync(
        ScheduleDefinition definition,
        CancellationToken cancellationToken = default);
}
