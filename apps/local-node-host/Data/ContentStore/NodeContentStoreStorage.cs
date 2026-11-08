using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>The durable backing of both ports: bookkeeping in the node's one storage model, and bytes as files in <c>content/</c> beside its database (ADR 0113, ADR 0114).</summary>
public sealed class NodeContentStoreStorage(IDbContextFactory<LocalNodeDbContext> contexts, string dataDirectory) : IContentStoreStorage
{
    private readonly NodeContentByteStore _bytes = new(Path.Combine(dataDirectory, "content"));

    /// <inheritdoc />
    public IContentBookkeeping Bookkeeping { get; } = new NodeContentBookkeeping(contexts);

    /// <inheritdoc />
    public IContentByteStore Bytes => _bytes;

    /// <summary>The content directory, the store's physical bytes for the Q22 quota base.</summary>
    public string DirectoryPath => _bytes.DirectoryPath;
}
