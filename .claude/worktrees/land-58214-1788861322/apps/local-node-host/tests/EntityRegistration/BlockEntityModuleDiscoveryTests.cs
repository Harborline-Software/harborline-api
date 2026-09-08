using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Tests.EntityRegistration;

public sealed class BlockEntityModuleDiscoveryTests
{
    public static TheoryData<string, string, string[]> FoldedModules => new()
    {
        {
            "Harborline.Api.Blocks.Banking",
            "harborline.blocks.banking",
            [
                "Harborline.Api.Blocks.Banking.Models.BankAccount",
                "Harborline.Api.Blocks.Banking.Models.MatchLink",
                "Harborline.Api.Blocks.Banking.Models.Reconciliation",
                "Harborline.Api.Blocks.Banking.Models.StatementLine",
            ]
        },
        {
            "Harborline.Api.Blocks.Docs",
            "harborline.blocks.docs",
            [
                "Harborline.Api.Blocks.Docs.Models.Attachment",
                "Harborline.Api.Blocks.Docs.Models.DocumentRef",
            ]
        },
        {
            "Harborline.Api.Blocks.FinancialAp",
            "harborline.blocks.financial-ap",
            ["Harborline.Api.Blocks.FinancialAp.Models.Bill"]
        },
        {
            "Harborline.Api.Blocks.FinancialAr",
            "harborline.blocks.financial-ar",
            [
                "Harborline.Api.Blocks.FinancialAr.Models.Invoice",
                "Harborline.Api.Blocks.FinancialAr.Models.RecurringInvoiceSchedule",
            ]
        },
        {
            "Harborline.Api.Blocks.FinancialLedger",
            "harborline.blocks.financial-ledger",
            [
                "Harborline.Api.Blocks.FinancialLedger.Data.TenantChartMappingRow",
                "Harborline.Api.Blocks.FinancialLedger.Models.ChartOfAccounts",
                "Harborline.Api.Blocks.FinancialLedger.Models.GLAccount",
                "Harborline.Api.Blocks.FinancialLedger.Models.JournalEntry",
                "Harborline.Api.Blocks.FinancialLedger.Models.LegalEntity",
                "Harborline.Api.Blocks.FinancialLedger.Models.LegalEntityOwnership",
            ]
        },
        {
            "Harborline.Api.Blocks.FinancialPeriods",
            "harborline.blocks.financial-periods",
            [
                "Harborline.Api.Blocks.FinancialPeriods.Models.FiscalPeriod",
                "Harborline.Api.Blocks.FinancialPeriods.Models.FiscalYear",
            ]
        },
        {
            "Harborline.Api.Blocks.FinancialPayments",
            "harborline.blocks.financial-payments",
            [
                "Harborline.Api.Blocks.FinancialPayments.Models.Payment",
                "Harborline.Api.Blocks.FinancialPayments.Models.PaymentApplication",
            ]
        },
        {
            "Harborline.Api.Blocks.People.Foundation",
            "harborline.blocks.people-foundation",
            [
                "Harborline.Api.Blocks.People.Foundation.Models.EmailAddress",
                "Harborline.Api.Blocks.People.Foundation.Models.Party",
                "Harborline.Api.Blocks.People.Foundation.Models.PartyAddress",
                "Harborline.Api.Blocks.People.Foundation.Models.PartyRole",
                "Harborline.Api.Blocks.People.Foundation.Models.PhoneNumber",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(FoldedModules))]
    public void Owning_block_discovers_every_registered_entity_type(
        string assemblyName,
        string moduleKey,
        string[] expectedEntityTypes)
    {
        var assembly = Assembly.Load(assemblyName);
        var module = assembly.GetExportedTypes()
            .Where(type => !type.IsAbstract && typeof(IHarborlineEntityModule).IsAssignableFrom(type))
            .Select(type => (IHarborlineEntityModule)Activator.CreateInstance(type)!)
            .Single(candidate => string.Equals(candidate.ModuleKey, moduleKey, StringComparison.Ordinal));
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var context = new LocalNodeDbContext(options, [module]);
        var discovered = context.Model.GetEntityTypes()
            .Select(entity => entity.ClrType.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = expectedEntityTypes.ToHashSet(StringComparer.Ordinal);
        var actual = discovered.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(assemblyName, module.GetType().Assembly.GetName().Name);
        Assert.Superset(expected, actual);
        Assert.Subset(expected, actual);
    }

    [Fact]
    public void Ledger_module_registrations_also_register_periods_module()
    {
        var hostRoot = LocateHostSourceRoot();
        var ledgerRegistrationFiles = Directory
            .EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => ComposesEntityModule(File.ReadAllText(file), "FinancialLedgerEntityModule"))
            .ToArray();
        var omissions = ledgerRegistrationFiles
            .Where(file => !ComposesEntityModule(File.ReadAllText(file), "FinancialPeriodsEntityModule"))
            .Select(file => Path.GetRelativePath(hostRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(ledgerRegistrationFiles);
        Assert.Empty(omissions);
    }

    [Fact]
    public void Subledger_repository_pair_is_owned_by_the_subledger_block()
    {
        var assembly = Assembly.Load("Harborline.Api.Blocks.FinancialSubLedger");
        var exportedTypes = assembly.GetExportedTypes()
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(
            "Harborline.Api.Blocks.FinancialSubLedger.Services.ISubLedgerAccountRepository",
            exportedTypes);
        Assert.Contains(
            "Harborline.Api.Blocks.FinancialSubLedger.Services.InMemorySubLedgerAccountRepository",
            exportedTypes);
    }

    private static bool ComposesEntityModule(string source, string moduleTypeName)
    {
        var qualifiedModuleType = $@"(?:[\w.]+\.)?{Regex.Escape(moduleTypeName)}";
        return Regex.IsMatch(
            source,
            $@"(?:AddSingleton\s*<\s*(?:[\w.]+\.)?IHarborlineEntityModule\s*,\s*{qualifiedModuleType}\s*>\s*\(|new\s+{qualifiedModuleType}\s*\()",
            RegexOptions.CultureInvariant);
    }

    private static string LocateHostSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harborline.LocalNodeHost.csproj")))
                return directory.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }
}
