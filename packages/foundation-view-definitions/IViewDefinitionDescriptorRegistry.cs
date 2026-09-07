namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>
/// Defines the engine-owned admission seam for view kinds and their parameter payloads.
/// Hosts implement this interface by adapting their registered view surfaces (the entity-type
/// registry today; Helm dashboard kinds once the host composes them); this package intentionally
/// supplies no descriptor registry implementation.
/// </summary>
public interface IViewDefinitionDescriptorRegistry
{
    /// <summary>
    /// Admits a canonical view definition or throws a domain-specific exception before persistence.
    /// </summary>
    /// <param name="definition">The canonical definition to admit.</param>
    /// <param name="cancellationToken">A token that cancels admission.</param>
    /// <returns>A value task that completes when admission succeeds.</returns>
    ValueTask AdmitAsync(
        ViewDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the record type targeted by a view so its local shape-role fields can be admitted.
    /// Implementations may return <see langword="null"/> when no mapping is present or the view kind
    /// does not target a record type.
    /// </summary>
    /// <param name="definition">The canonical definition being admitted.</param>
    /// <param name="cancellationToken">A token that cancels admission.</param>
    ValueTask<ViewRecordTypeDescriptor?> DescribeRecordTypeAsync(
        ViewDefinition definition,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ViewRecordTypeDescriptor?>(null);
}
