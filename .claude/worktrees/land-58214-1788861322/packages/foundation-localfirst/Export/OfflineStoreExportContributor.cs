using System.Runtime.CompilerServices;

namespace Harborline.Api.Foundation.LocalFirst.Export;

/// <summary>
/// Contributes entries read directly from an <see cref="IOfflineStore"/>: the
/// scope string is used as a key prefix, keys are listed once (in
/// <see cref="IOfflineStore.ListKeysAsync"/>'s stable Ordinal order — see
/// <see cref="InMemoryOfflineStore.ListKeysAsync"/>), and each key's value is
/// read and emitted as one <see cref="ExportEntry"/>.
///
/// <para>
/// This is a read-only snapshot enumeration. It does not lock the store
/// against concurrent writes, so a value changed between the key listing and
/// the corresponding read is reflected as-of the read, not as-of the list —
/// callers wanting a stronger snapshot guarantee must provide a store that
/// enforces one.
/// </para>
/// </summary>
public sealed class OfflineStoreExportContributor : IExportContributor
{
    private readonly IOfflineStore _store;

    /// <summary>Creates a contributor over the given offline store.</summary>
    public OfflineStoreExportContributor(IOfflineStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public string StableKey => "offline-store";

    /// <inheritdoc />
    public async IAsyncEnumerable<ExportEntry> ReadAsync(
        string scope,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var keys = await _store.ListKeysAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                // Deleted between listing and read; skip rather than emit a null payload.
                continue;
            }

            yield return new ExportEntry(key, value);
        }
    }
}
