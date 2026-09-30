using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// DES-0029 kernel-core-ck-6: a journal row reaches the node database only through the Platform
/// <c>KernelTransactionBoundary</c> port, whose <c>CommitAsync</c> is the one journal commit point. The workflow
/// effects that join the workflow writer's transaction are named exceptions: the boundary has no join mode.
/// </summary>
public sealed class JournalKernelBoundaryArchTests
{
    private const string Port = "apps/local-node-host/Data/Financial/NodeJournalKernelTransactionPort.cs";
    private const string Store = "apps/local-node-host/Data/Financial/NodeEfJournalStore.cs";

    private static readonly Regex JournalStage = new(
        @"Set<JournalEntry>\(\)\s*\.\s*(?:Add|AddRange|Update|UpdateRange|Attach|Remove|RemoveRange)(?:Async)?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CommitCall = new(
        @"\.\s*(?<call>(?:SaveChanges|BeginTransaction|UseTransaction|Commit|ExecuteUpdate|ExecuteDelete|ExecuteSql\w*|ExecuteNonQuery)(?:Async)?)\s*\(",
        RegexOptions.Compiled);

    // A workflow effect stages its journal row into the workflow writer's already-open transaction, which the
    // Platform boundary cannot join. Each row is a known ck-6 gap, not a blessing.
    private static readonly string[] WorkflowJoins =
    [
        "apps/local-node-host/Data/Workflow/NodeInvoiceApprovalContext.cs",
        "apps/local-node-host/Data/Workflow/NodeKgActionApprovalContext.cs",
        "apps/local-node-host/Data/Workflow/NodeLedgerPostingEffect.cs",
        "apps/local-node-host/Data/Workflow/NodeLiveInvoiceApprovalContext.cs",
        "apps/local-node-host/Data/Workflow/NodeRecurringGenerationContext.cs",
    ];

    [Fact]
    public void JournalRowsAreStagedOnlyByTheKernelPortOrANamedWorkflowJoin()
    {
        Assert.Equal(
            WorkflowJoins.Append(Port).Order(StringComparer.Ordinal),
            ScanJournalStages(RepositoryRoot()));
    }

    [Fact]
    public void TheJournalStoreCommitsOnlyThroughTheKernelBoundaryPort()
    {
        var root = RepositoryRoot();
        var store = Source(root, Store);
        Assert.Contains("KernelTransactionBoundary.ExecutePreparedAsync(", store);
        Assert.Contains("new NodeJournalKernelTransactionPort(", store);
        Assert.Empty(CommitCall.Matches(store));

        // The port opens the single BEGIN IMMEDIATE fence in BeginAsync and saves and commits only in CommitAsync.
        var port = Source(root, Port);
        Assert.Contains("HomeEpochFenceTransaction.BeginAsync(", port);
        var commit = port[port.IndexOf("CommitAsync(CancellationToken", StringComparison.Ordinal)..];
        commit = commit[..commit.IndexOf("RollbackAsync(", StringComparison.Ordinal)];
        Assert.Equal(["SaveChangesAsync", "CommitAsync"], CommitCall.Matches(commit).Select(match => match.Groups["call"].Value).ToArray());
        Assert.Equal(2, CommitCall.Matches(port).Count);
    }

    [Fact]
    public void TheScanFindsAPlantedJournalWriteOutsideThePort()
    {
        var root = Path.Combine(Path.GetTempPath(), "ck6-journal-scan-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "apps", "local-node-host", "Data", "Planted");
        Directory.CreateDirectory(planted);
        try
        {
            File.WriteAllText(Path.Combine(planted, "Writer.cs"),
                "sealed class Writer { void Go(DbContext c, JournalEntry e) { c.Set<JournalEntry>()\n.Add(e); /* c.Set<JournalEntry>().Add(e); */ } }");
            Assert.Equal(["apps/local-node-host/Data/Planted/Writer.cs"], ScanJournalStages(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] ScanJournalStages(string root)
    {
        var host = Path.Combine(root, "apps", "local-node-host");
        return Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment => segment is "tests" or "bin" or "obj" or "Migrations"))
            .Where(item => JournalStage.IsMatch(StripComments(File.ReadAllText(item.File))))
            .Select(item => item.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Source(string root, string relative) =>
        StripComments(File.ReadAllText(Path.Combine(root, relative)));

    private static string StripComments(string source) => Regex.Replace(
        source,
        @"//.*?$|/\*.*?\*/",
        string.Empty,
        RegexOptions.Multiline | RegexOptions.Singleline);

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
