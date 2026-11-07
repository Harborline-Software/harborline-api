using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Api.LocalNodeHost.Data.Definitions;
using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// The node's production registration over a migrated, SQLCipher-keyed local-node.db in its own directory. The migrated file is
// made once and copied, because every section 9 test needs a fresh backing.
internal sealed class NodeContentTestStore : IDisposable
{
    private static readonly byte[] Dek = Enumerable.Repeat((byte)0x52, 32).ToArray();
    private static readonly Lazy<string> Template = new(MakeTemplate);
    private static readonly ConditionalWeakTable<IContentBookkeeping, IDbContextFactory<LocalNodeDbContext>> Factories = new();
    private readonly ServiceProvider _provider;

    private NodeContentTestStore(string directory)
    {
        DataDirectory = directory;
        _provider = Compose(directory);
        Contexts = _provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        Storage = new NodeContentStoreStorage(Contexts, directory);
        Factories.Add(Storage.Bookkeeping, Contexts);
    }

    public string DataDirectory { get; }
    public IDbContextFactory<LocalNodeDbContext> Contexts { get; }
    public NodeContentStoreStorage Storage { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "local-node.db");

    public static NodeContentTestStore Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t521-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.Copy(Template.Value, Path.Combine(directory, "local-node.db"));
        return new(directory);
    }

    public NodeContentTestStore Restart()
    {
        _provider.Dispose();
        return new(DataDirectory);
    }

    public static IDbContextFactory<LocalNodeDbContext> ContextsOf(IContentBookkeeping bookkeeping) =>
        Factories.TryGetValue(bookkeeping, out var contexts) ? contexts : throw new InvalidOperationException("That backing is not a test store's.");

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
    }

    internal static ServiceProvider Compose(string directory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContextWithStoreDek(Dek, Path.Combine(directory, "local-node.db"), pooling: false);
        services.AddSingleton<IHarborlineEntityModule, ContentStoreEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, DefinitionArchiveEntityModule>();
        return services.BuildServiceProvider();
    }

    private static string MakeTemplate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t521-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(directory, true);
        using var provider = Compose(directory);
        using var context = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContext();
        context.Database.Migrate();
        return Path.Combine(directory, "local-node.db");
    }
}
