using System.Runtime.CompilerServices;

using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1048 (DES-0029 ck-6): a subject erasure's approval evidence (approver ids, which are personal data, the legal
/// basis and the approval instant) is read only by the registry's recovery path. No route, query surface or
/// projection reads it: the IL walk over every shipped assembly finds each read of the evidence properties, and
/// no production source outside the search context's mapping names the table for raw SQL.
/// </summary>
public sealed class SubjectErasureEvidenceReadFenceTests
{
    private const string RecoveryRegistry = "apps/local-node-host/Data/Search/Vector/NodeEfSubjectErasureRegistry.cs";
    private const string SearchContext = "apps/local-node-host/Data/Search/NodeLocalSearchDbContext.cs";

    [Fact(DisplayName = "T-1048: only the erasure registry's recovery path reads the erasure evidence")]
    public void Only_the_recovery_registry_reads_the_evidence()
    {
        var sites = AuditAppendSymbolInventory.Discover(method =>
            method.DeclaringType == typeof(SubjectErasureRow)
            && method.Name is "get_ApprovingActorsJson" or "get_LegalBasis" or "get_ApprovedAtUnixMs"
                ? method.Name
                : null);

        // Anti-vacuity: the walk must find the registry's own read, or an empty result proves nothing.
        Assert.Contains(sites, site => site.File == RecoveryRegistry);
        Assert.All(sites, site => Assert.Equal(RecoveryRegistry, site.File));
    }

    [Fact(DisplayName = "T-1048: no production source outside the search context mapping names the erasure table")]
    public void No_raw_sql_reaches_the_erasure_table()
    {
        var root = RepositoryRoot();
        var naming = new[] { "apps", "packages" }
            .Select(top => Path.Combine(root, top))
            .Where(Directory.Exists)
            .SelectMany(top => Directory.EnumerateFiles(top, "*.cs", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(file => !file.Split('/').Any(segment => segment is "tests" or "bin" or "obj" or "Migrations"))
            .Where(file => File.ReadAllText(Path.Combine(root, file)).Contains("search_subject_erasures", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([SearchContext], naming);
    }

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
