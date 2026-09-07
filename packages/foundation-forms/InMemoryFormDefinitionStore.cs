using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Read-only in-memory form-definition store. Its mutation state is owned and unwrapped only by
/// <see cref="AuthorizedFormDefinitionLifecycle"/>; this facade retains an opaque handle.
/// </summary>
public sealed class InMemoryFormDefinitionStore : IFormDefinitionStore, IDisposable
{
    private readonly AuthorizedFormDefinitionLifecycle.InMemoryPersistenceHandle handle;

    /// <summary>Constructs a registry with the supplied lifecycle clock.</summary>
    public InMemoryFormDefinitionStore(TimeProvider time)
        : this(AuthorizedFormDefinitionLifecycle.CreateInMemoryPersistence(time))
    {
    }

    internal InMemoryFormDefinitionStore(
        AuthorizedFormDefinitionLifecycle.InMemoryPersistenceHandle persistenceHandle)
    {
        handle = persistenceHandle ?? throw new ArgumentNullException(nameof(persistenceHandle));
    }

    internal AuthorizedFormDefinitionLifecycle.InMemoryPersistenceHandle PersistenceHandle => handle;

    /// <inheritdoc />
    public ValueTask<FormDefinition> GetAsync(
        DefinitionCoordinates coordinates,
        CancellationToken ct = default) =>
        AuthorizedFormDefinitionLifecycle.ReadInMemoryAsync(handle, coordinates, ct);

    /// <inheritdoc />
    public ValueTask<FormDefinition?> GetCurrentPublishedAsync(
        DefinitionAddress address,
        CancellationToken ct = default) =>
        AuthorizedFormDefinitionLifecycle.ReadCurrentInMemoryAsync(handle, address, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<FormDefinition> ListByTenantAsync(
        TenantId tenant,
        CancellationToken ct = default) =>
        AuthorizedFormDefinitionLifecycle.ListInMemoryByTenantAsync(handle, tenant, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<FormDefinition> ListPublishedAsync(CancellationToken ct = default) =>
        AuthorizedFormDefinitionLifecycle.ListPublishedInMemoryAsync(handle, ct);

    /// <inheritdoc />
    public void Dispose() => AuthorizedFormDefinitionLifecycle.DisposeInMemory(handle);
}
