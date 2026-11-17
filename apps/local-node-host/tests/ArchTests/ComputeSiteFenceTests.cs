using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Freezes member-call folds in the host and block packages. Hand loops, static calls such as
/// Enumerable.Sum, and kernel and foundation packages are outside this fence.
/// </summary>
public sealed class ComputeSiteFenceTests
{
    internal static readonly string[] Allowed =
    [
        "apps/local-node-host/Data/Audit/NodeAuditSignatureClassifier.cs#1", // structure: latest epoch boundary
        "apps/local-node-host/Data/Configuration/ConfigurationRecoveryCommand.cs#1", // structure: latest prepared row
        "apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs#1", // structure: latest validity end
        "apps/local-node-host/Data/Workflow/NodeWorkflowInstantiationService.cs#1", // debt T-523: total of schedule line templates
        "apps/local-node-host/Health/BankAccountRoutes.cs#5", // debt T-523: balances, cleared total and last activity
        "apps/local-node-host/Health/ErpnextImportPreviewRoutes.cs#4", // debt T-523: import-preview row counts
        "apps/local-node-host/Health/JournalEntryRoutes.cs#4", // debt T-523: debit and credit totals
        "apps/local-node-host/Health/SyncStatusRoutes.cs#1", // structure: most recently reached peer
        "packages/blocks-banking/Matching/AcceptMatchService.cs#1", // retiring family: blocks-banking
        "packages/blocks-banking/Matching/UnMatchService.cs#1", // retiring family: blocks-banking
        "packages/blocks-docs/Services/InMemoryAttachmentRepository.cs#1", // retiring family: blocks-docs
        "packages/blocks-financial-ap/Data/ApEntityModule.cs#1", // retiring family: blocks-financial-ap
        "packages/blocks-financial-ap/Migration/ErpnextPurchaseInvoiceImporter.cs#1", // retiring family: blocks-financial-ap
        "packages/blocks-financial-ap/Models/Bill.cs#2", // retiring family: blocks-financial-ap
        "packages/blocks-financial-ar/Data/ArEntityModule.cs#1", // retiring family: blocks-financial-ar
        "packages/blocks-financial-ar/Migration/ErpnextSalesInvoiceImporter.cs#1", // retiring family: blocks-financial-ar
        "packages/blocks-financial-ar/Models/Invoice.cs#2", // retiring family: blocks-financial-ar
        "packages/blocks-financial-ledger/Data/FinancialLedgerEntityModule.cs#1", // retiring family: blocks-financial-ledger
        "packages/blocks-financial-ledger/Models/JournalEntry.cs#2", // retiring family: blocks-financial-ledger
        "packages/blocks-financial-ledger/Services/ConsolidationService.cs#1", // retiring family: blocks-financial-ledger
        "packages/blocks-financial-ledger/Services/InMemoryJournalEntryQueryReadModel.cs#2", // retiring family: blocks-financial-ledger
        "packages/blocks-financial-payments/Services/DefaultPaymentPostingService.cs#1", // retiring family: blocks-financial-payments
        "packages/blocks-financial-subledger-projection/Services/SubLedgerReadModel.cs#3", // retiring family: blocks-financial-subledger-projection
        "packages/blocks-financial-tax/Services/TaxCalculationService.cs#2", // retiring family: blocks-financial-tax
        "packages/blocks-people-foundation/Data/PeopleEntityModule.cs#1", // retiring family: blocks-people-foundation
        "packages/blocks-reports/Cartridges/ProfitAndLoss/ProfitAndLossCartridge.cs#2", // retiring family: blocks-reports
        "packages/blocks-reports/Cartridges/ProfitAndLossByProperty/ProfitAndLossByPropertyCartridge.cs#4", // retiring family: blocks-reports
        "packages/blocks-reports/Cartridges/RentRoll/RentRollCartridge.cs#2", // retiring family: blocks-reports
    ];

    [Fact]
    public void Set_folds_in_the_host_and_block_packages_match_the_commented_allow_list_exactly()
    {
        var found = Discover();
        var listed = Allowed.ToHashSet(StringComparer.Ordinal);
        var actual = found.ToHashSet(StringComparer.Ordinal);
        var differences = listed.Except(actual).Concat(actual.Except(listed))
            .Select(row => row[..row.LastIndexOf('#')]).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{path}: found {Count(found, path)}, listed {Count(Allowed, path)}");
        Assert.True(listed.SetEquals(actual), string.Join(Environment.NewLine, differences));
    }

    private static string Count(IEnumerable<string> rows, string path) =>
        rows.FirstOrDefault(row => row.StartsWith(path + "#", StringComparison.Ordinal))?.Split('#')[1] ?? "0";

    [Theory]
    [InlineData("opening + lines.Sum(l => l.Amount)")]
    [InlineData("rows?.Sum(r => r.Amount)")]
    [InlineData("rows.Where(r => r.Open).Average(r => r.Days)")]
    [InlineData("entries.Aggregate(0m, (sum, e) => sum + e.Amount)")]
    [InlineData("lines.Max(l => l.CreatedAtUtc)")]
    [InlineData("entries\n.Where(entry => entry.Open)\n.Sum(entry => (long)entry.SourceRowCount)")]
    public void Canary_rejects_a_planted_fold_over_rows(string expression)
    {
        var root = Path.Combine(Path.GetTempPath(), "t523-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "apps", "local-node-host", "Health");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Canary.cs"), $"class Canary {{ object M() => {expression}; }}");
            Assert.Equal(["apps/local-node-host/Health/Canary.cs#1"], Discover(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Scalar_math_calls_are_not_folds()
    {
        const string source = "class C { void M() { Math.Max(a, b); Math.Min(60, Math.Pow(2, n)); BigInteger.Max(a, b); TimeSpan.FromTicks(Math.Min(a, b)); System.Math.Max(a, b); } }";
        Assert.Equal(0, FoldCount(source));
    }

    internal static string[] Discover() => Discover(RepositoryRoot());

    private static string[] Discover(string root)
    {
        var host = Path.Combine(root, "apps", "local-node-host");
        var packages = Path.Combine(root, "packages");
        var roots = (Directory.Exists(host) ? new[] { host } : [])
            .Concat(Directory.Exists(packages) ? Directory.EnumerateDirectories(packages, "blocks-*") : []);
        return roots.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: file, Path: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Path.Split('/').Any(segment => segment is "bin" or "obj" or "tests"))
            .Select(item => (item.Path, Count: FoldCount(File.ReadAllText(item.File))))
            .Where(item => item.Count > 0)
            .Select(item => $"{item.Path}#{item.Count}")
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static int FoldCount(string source) => CSharpSyntaxTree.ParseText(source).GetRoot()
        .DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Count(call => call.Expression switch
        {
            MemberAccessExpressionSyntax member => IsFold(member.Name) && !IsScalar(member.Expression),
            MemberBindingExpressionSyntax binding => IsFold(binding.Name),
            _ => false,
        });

    private static bool IsFold(SimpleNameSyntax name) => name.Identifier.ValueText is
        "Sum" or "Average" or "Aggregate" or "Min" or "Max" or "MinBy" or "MaxBy";

    private static bool IsScalar(ExpressionSyntax receiver) => receiver.ToString() is
        "Math" or "MathF" or "BigInteger" or "TimeSpan" or "decimal" or "DateTimeOffset" or "DateTime" or "Interlocked"
        or "System.Math" or "System.MathF" or "System.Numerics.BigInteger" or "System.TimeSpan"
        or "System.DateTimeOffset" or "System.DateTime" or "System.Threading.Interlocked"
        || receiver is GenericNameSyntax generic && generic.Identifier.ValueText == "Vector";

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
