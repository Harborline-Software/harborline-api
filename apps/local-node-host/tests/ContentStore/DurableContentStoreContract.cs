using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.ContentStore;
using Harborline.Foundation.ContentStore.Tests;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

public sealed class DurableContentStoreContract : ContentStoreTests, IDisposable
{
    private readonly List<NodeContentStoreStorage> _opened = [];
    private readonly List<string> _directories = [];
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x52, 32).ToArray();
    protected override IContentStoreStorage CreateStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), "t521-" + Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        var storage = new NodeContentStoreStorage(path, Key);
        _opened.Add(storage);
        return storage;
    }
    protected override IContentStoreStorage RestartStorage(IContentStoreStorage storage)
    {
        var old = (NodeContentStoreStorage)storage;
        var path = old.DirectoryPath;
        old.Dispose();
        _opened.Remove(old);
        var reopened = new NodeContentStoreStorage(path, Key);
        _opened.Add(reopened);
        return reopened;
    }
    public void Dispose()
    {
        foreach (var storage in _opened) storage.Dispose();
        foreach (var directory in _directories) Directory.Delete(directory, true);
    }
}
