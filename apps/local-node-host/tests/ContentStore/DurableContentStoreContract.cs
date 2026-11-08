using Harborline.Foundation.ContentStore;
using Harborline.Foundation.ContentStore.Tests;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// The producer's section 9 and restart tests on the node's backing (clause 5). It reads a process-wide meter, so it runs apart.
[Collection("Node content store")]
public sealed class DurableContentStoreContract : ContentStoreTests, IDisposable
{
    private readonly List<NodeContentTestStore> _stores = [];

    protected override IContentStoreStorage CreateStorage()
    {
        var store = NodeContentTestStore.Create();
        _stores.Add(store);
        return store.Storage;
    }

    protected override IContentStoreStorage RestartStorage(IContentStoreStorage storage)
    {
        var index = _stores.FindIndex(store => ReferenceEquals(store.Storage, storage));
        _stores[index] = _stores[index].Restart();
        return _stores[index].Storage;
    }

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
    }
}

[CollectionDefinition("Node content store", DisableParallelization = true)]
public sealed class NodeContentStoreCollection;
