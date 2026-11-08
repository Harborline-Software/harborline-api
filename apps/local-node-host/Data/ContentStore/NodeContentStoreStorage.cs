using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>
/// The durable backing of the ports: bookkeeping in the node's one storage model, bytes as files in <c>content/</c> beside its
/// database (ADR 0113, ADR 0114), and epoch keys as files in the node's stored-key store, <c>tenant-keys/content-epochs/</c>,
/// never in the database (ADR 0116 decision 4).
/// </summary>
public sealed class NodeContentStoreStorage(IDbContextFactory<LocalNodeDbContext> contexts, string dataDirectory) : IContentStoreStorage
{
    private readonly NodeContentByteStore _bytes = new(Path.Combine(dataDirectory, "content"));

    /// <inheritdoc />
    public IContentEpochKeyStore Epochs { get; } = new NodeContentEpochKeyStore(Path.Combine(dataDirectory, "tenant-keys", NodeContentEpochKeyStore.DirectoryName));

    /// <inheritdoc />
    public IContentBookkeeping Bookkeeping { get; } = new NodeContentBookkeeping(contexts);

    /// <inheritdoc />
    public IContentByteStore Bytes => _bytes;

    /// <summary>The content directory, the store's physical bytes for the Q22 quota base.</summary>
    public string DirectoryPath => _bytes.DirectoryPath;
}
