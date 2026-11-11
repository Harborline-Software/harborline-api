using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>
/// The durable backing of the ports: bookkeeping in the node's one storage model, bytes as files in <c>content/</c> beside its
/// database (ADR 0113, ADR 0114), and epoch keys as files in the node's stored-key store, <c>tenant-keys/content-epochs/</c>,
/// never in the database (ADR 0116 decision 4).
/// </summary>
public sealed class NodeContentStoreStorage : IContentStoreStorage
{
    private readonly NodeContentByteStore _bytes;

    /// <summary>Composes the ports with one backup fence for both forms of destruction.</summary>
    public NodeContentStoreStorage(IDbContextFactory<LocalNodeDbContext> contexts, string dataDirectory)
    {
        _bytes = new(Path.Combine(dataDirectory, "content"), Backups);
        Epochs = new NodeContentEpochKeyStore(Path.Combine(dataDirectory, "tenant-keys", NodeContentEpochKeyStore.DirectoryName));
        Bookkeeping = new NodeContentBookkeeping(contexts);
    }

    /// <summary>Backups hold this handle before copying any database or content file (ADR 0127).</summary>
    public ContentStoreBackupActivity Backups { get; } = new();

    /// <inheritdoc />
    public IContentEpochKeyStore Epochs { get; }

    /// <inheritdoc />
    public IContentBookkeeping Bookkeeping { get; }

    /// <inheritdoc />
    public IContentByteStore Bytes => _bytes;

    /// <summary>The content directory, the store's physical bytes for the Q22 quota base.</summary>
    public string DirectoryPath => _bytes.DirectoryPath;
}
