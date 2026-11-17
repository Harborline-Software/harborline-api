using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// DES-0029 kernel-core-ck-6: every production durable write site in the host is classified against the
/// transaction-boundary obligation (record and audit atomic, rollback, replay, batch refusal). A new
/// <c>SaveChanges</c>/<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/<c>ExecuteSql</c>/<c>ExecuteNonQuery</c> site,
/// or a changed count in a classified file, fails until its row in
/// <c>durable-write-classification.tsv</c> is written or updated.
/// </summary>
public sealed class DurableWriteClassificationArchTests
{
    private static readonly Regex WriteCall = new(
        @"\b(?:SaveChanges|ExecuteUpdate|ExecuteDelete|ExecuteSqlRaw|ExecuteSqlInterpolated|ExecuteSql|ExecuteNonQuery)(?:Async)?\s*[(<]",
        RegexOptions.Compiled);

    [Fact]
    public void EveryProductionDurableWriteSiteIsClassifiedWithItsExactCount()
    {
        // The class vocabulary, not an allow-list: every row must use one of these.
        string[] classes = ["atomic-audit", "record-is-audit", "outbox-audit", "intent-audit", "post-commit-audit", "unaudited", "n/a"];
        var root = RepositoryRoot();
        var rows = ReadClassification(root);
        Assert.All(rows, row =>
        {
            Assert.True(classes.Contains(row.Value.Class), $"{row.Key}: unknown class '{row.Value.Class}'");
            Assert.False(string.IsNullOrWhiteSpace(row.Value.Evidence), $"{row.Key}: evidence is required");
        });

        var expected = rows.Select(row => $"{row.Key}\t{row.Value.Count}").Order(StringComparer.Ordinal).ToArray();
        var actual = Scan(root).Select(item => $"{item.Path}\t{item.Count}").ToArray();
        Assert.True(
            expected.SequenceEqual(actual, StringComparer.Ordinal),
            "Classify every durable write site in durable-write-classification.tsv. Missing or stale rows:" +
            Environment.NewLine + string.Join(Environment.NewLine, actual.Except(expected).Select(row => "+ " + row)
                .Concat(expected.Except(actual).Select(row => "- " + row))));
    }

    [Fact(DisplayName = "ck-10 S6: every non-n/a durable inventory row has an executor coverage disposition")]
    [Trait("Holds", "kernel-core-ck-10")]
    public void EveryNonNaDurableInventoryRowHasAnExecutorCoverageDisposition()
    {
        var root = RepositoryRoot();
        var expected = ReadClassification(root).Where(row => row.Value.Class != "n/a")
            .Select(row => $"{row.Key}\t{row.Value.Count}").Order(StringComparer.Ordinal).ToArray();
        var mapped = ReadCoverageTables(root)
            .Where(columns => columns[0].StartsWith("`apps/local-node-host/", StringComparison.Ordinal)).ToArray();
        Assert.All(mapped, columns =>
        {
            Assert.Equal(8, columns.Length);
            Assert.Contains(columns[2], new[] { "executor", "mixed", "gap", "not-admitted" });
            Assert.All(columns, column => Assert.False(string.IsNullOrWhiteSpace(column)));
        });
        Assert.Equal(expected, mapped.Select(columns => $"{columns[0].Trim('`')}\t{columns[1]}")
            .Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-10")]
    public void ReviewedNaExclusionsCannotHideBusinessWrites()
    {
        // Oracle: reviewed operational exclusions, not whatever the current TSV happens to call n/a.
        string[] expected =
        [
            "apps/local-node-host/Data/Admission/DurableAdmissionTokenStore.cs|3",
            "apps/local-node-host/Data/Admission/DurableWebPairingInviteBindingStore.cs|1",
            "apps/local-node-host/Data/Audit/NodeAuditOutbox.cs|1",
            "apps/local-node-host/Data/Audit/NodeAuditTrailStore.cs|1",
            "apps/local-node-host/Data/Authorization/NodeEfAuthorizationClosureReader.cs|3",
            "apps/local-node-host/Data/Configuration/ConfigurationEvidenceOutbox.cs|2",
            "apps/local-node-host/Data/Drafts/NodeEfSubmissionDraftStore.cs|4",
            "apps/local-node-host/Data/Forms/NodeEfFormSubmitOutbox.cs|3",
            "apps/local-node-host/Data/Identity/RecoverySessionRevoker.cs|1",
            "apps/local-node-host/Data/Identity/WebAccountAccessChallengeIssuer.cs|1",
            "apps/local-node-host/Data/Identity/WebAntiforgeryStateStore.cs|2",
            "apps/local-node-host/Data/Identity/WebInstallationSessionStore.cs|3",
            "apps/local-node-host/Data/Identity/WebSelectedSessionStore.cs|2",
            "apps/local-node-host/Data/Packs/DurableChannelSequenceStore.cs|1",
            "apps/local-node-host/Data/People/ContactCrdtProjection.cs|1",
            "apps/local-node-host/Data/Search/NodeSearchIndexer.cs|3",
            "apps/local-node-host/Data/Search/Vector/NodeVecIndexer.cs|3",
            "apps/local-node-host/Data/Search/Vector/Sqlite/Vec0AccelerationSink.cs|3",
            "apps/local-node-host/Data/Search/Vector/Sqlite/Vec0KnnEngine.cs|1",
            "apps/local-node-host/Data/SqlCipherConnectionInterceptor.cs|4",
        ];
        var actual = ReadClassification(RepositoryRoot()).Where(row => row.Value.Class == "n/a")
            .Select(row => $"{row.Key}|{row.Value.Count}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("apps/local-node-host/Data/People/ContactCrdtProjection.cs", "n/a", 1)]
    [InlineData("apps/local-node-host/Health/BankAccountRoutes.cs", "unaudited", 2)]
    public void ContactProjectionAndBankFeedHaveReviewedClassifications(string path, string classification, int count)
    {
        var row = ReadClassification(RepositoryRoot())[path];
        Assert.Equal(classification, row.Class);
        Assert.Equal(count, row.Count);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-10")]
    public void CoverageDispositionsMatchTheReviewedPaths()
    {
        // Oracle: the reviewed S6 source traces and explicit rationales, pinned per path.
        string[] expected =
        [
            "apps/local-node-host/BackupRestore/SignedRosterRehostGrantProvider.cs|gap",
            "apps/local-node-host/CompromisedDeviceResponse/NodeRosterCompromisedDeviceResponseStore.cs|not-admitted",
            "apps/local-node-host/Data/AssetRegistry/NodeEfSpatialFrameDescriptorPort.cs|gap",
            "apps/local-node-host/Data/Authorization/NodeEfAuthorizationConfigurationStore.cs|executor",
            "apps/local-node-host/Data/Authorization/RetiredDesktopActorRekey.cs|not-admitted",
            "apps/local-node-host/Data/Banking/NodeEfBankAccountRepository.cs|executor",
            "apps/local-node-host/Data/Banking/NodeEfFiscalPeriodRepository.cs|gap",
            "apps/local-node-host/Data/Banking/NodeEfMatchLinkRepository.cs|gap",
            "apps/local-node-host/Data/Banking/NodeEfReconciliationRepository.cs|gap",
            "apps/local-node-host/Data/Banking/NodeEfStatementLineRepository.cs|gap",
            "apps/local-node-host/Data/Calendar/NodeEfCalendarEventStore.cs|gap",
            "apps/local-node-host/Data/Calendar/NodeEfCalendarStore.cs|gap",
            "apps/local-node-host/Data/Calendar/NodeEfResourceAvailabilityStore.cs|gap",
            "apps/local-node-host/Data/Comms/CommsCrdtProjection.cs|mixed",
            "apps/local-node-host/Data/Configuration/ConfigurationActivationTarget.cs|executor",
            "apps/local-node-host/Data/Configuration/ConfigurationProposalStore.cs|gap",
            "apps/local-node-host/Data/Configuration/ConfigurationRecoveryCommand.cs|gap",
            "apps/local-node-host/Data/ContentStore/NodeContentBookkeeping.cs|gap",
            "apps/local-node-host/Data/Definitions/NodeDefinitionArchiveWriter.cs|executor",
            "apps/local-node-host/Data/Docs/NodeEfAttachmentRepository.cs|gap",
            "apps/local-node-host/Data/Docs/NodeEfDocumentRefRepository.cs|gap",
            "apps/local-node-host/Data/Entities/NodeEntityWriter.cs|executor",
            "apps/local-node-host/Data/Financial/NodeAccountingPeriodService.cs|gap",
            "apps/local-node-host/Data/Financial/NodeEfBillRepository.cs|gap",
            "apps/local-node-host/Data/Financial/NodeEfInvoiceRepository.cs|gap",
            "apps/local-node-host/Data/Financial/NodeEfPaymentApplicationRepository.cs|gap",
            "apps/local-node-host/Data/Financial/NodeEfPaymentRepository.cs|gap",
            "apps/local-node-host/Data/Financial/NodeEfRecurringInvoiceService.cs|gap",
            "apps/local-node-host/Data/Financial/NodeJournalKernelTransactionPort.cs|gap",
            "apps/local-node-host/Data/HomeEpoch/HomeEfHomeEpochStore.cs|gap",
            "apps/local-node-host/Data/Identity/AccountCredentialRecoveryService.cs|gap",
            "apps/local-node-host/Data/Identity/AccountSetupInvitationStore.cs|gap",
            "apps/local-node-host/Data/Identity/BootstrapClaimRedemption.cs|gap",
            "apps/local-node-host/Data/Identity/InstallationFounderBindingService.cs|gap",
            "apps/local-node-host/Data/Identity/InstallationFounderBootstrapService.cs|gap",
            "apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorService.cs|gap",
            "apps/local-node-host/Data/Identity/InstallationIdentityCutoverOrchestrator.cs|not-admitted",
            "apps/local-node-host/Data/Identity/LegacyIdentityMigrationService.cs|gap",
            "apps/local-node-host/Data/Identity/NodeAdministratorAuthority.cs|gap",
            "apps/local-node-host/Data/Identity/RecoveryInvitationStore.cs|gap",
            "apps/local-node-host/Data/Identity/WebJoinerAccountMinter.cs|gap",
            "apps/local-node-host/Data/Identity/WebSelectedSessionLogoutAuthority.cs|gap",
            "apps/local-node-host/Data/Identity/WebTenantSelectionAuthority.cs|gap",
            "apps/local-node-host/Data/Identity/WebTenantSwitchAuthority.cs|gap",
            "apps/local-node-host/Data/OrgBranding/NodeEfOrgBrandingStore.cs|gap",
            "apps/local-node-host/Data/Packs/DurablePackInstallStore.cs|executor",
            "apps/local-node-host/Data/Payroll/NodeEfEmployeeRepository.cs|gap",
            "apps/local-node-host/Data/Payroll/NodeEfFilingObligationRepository.cs|gap",
            "apps/local-node-host/Data/Payroll/NodeEfPayRunRepository.cs|gap",
            "apps/local-node-host/Data/People/NodeEfPartyRepository.cs|gap",
            "apps/local-node-host/Data/Roster/RosterCrdtProjection.cs|mixed",
            "apps/local-node-host/Data/Scheduling/NodeSchedulingDraftStore.cs|gap",
            "apps/local-node-host/Data/Search/Vector/NodeEfGrantStore.cs|mixed",
            "apps/local-node-host/Data/Search/Vector/NodeEfSubjectErasureRegistry.cs|gap",
            "apps/local-node-host/Data/Search/Vector/NodeEfSubjectTombstoneStore.cs|not-admitted",
            "apps/local-node-host/Data/Workflow/NodeEfWorkflowStore.cs|gap",
            "apps/local-node-host/Data/Workflow/NodeWorkflowKernelTransactionPort.cs|gap",
            "apps/local-node-host/Health/BankAccountRoutes.cs|gap",
            "apps/local-node-host/Health/ChartOfAccountsManagementRoutes.cs|gap",
            "apps/local-node-host/Health/ChartOfAccountsRoutes.cs|gap",
            "apps/local-node-host/Health/ContactRoutes.cs|gap",
            "apps/local-node-host/Health/LeaseRoutes.cs|gap",
            "apps/local-node-host/Health/MaintenanceRoutes.cs|gap",
            "apps/local-node-host/Health/PropertyRoutes.cs|gap",
            "apps/local-node-host/Layout/LayoutDenialOutbox.cs|not-admitted",
        ];
        var actual = ReadCoverageTables(RepositoryRoot())
            .Where(columns => columns[0].StartsWith("`apps/local-node-host/", StringComparison.Ordinal))
            .Select(columns => $"{columns[0].Trim('`')}|{columns[2]}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-10")]
    public void ReviewedOutsideScannerStoresRemainMapped()
    {
        // Oracle: the seven physical/indirect stores checked in the round-two review, including their dispositions/owners.
        string[] expected =
        [
            "apps/local-node-host/Data/ContentStore/NodeContentByteStore.cs|gap|H13",
            "apps/local-node-host/Data/ContentStore/NodeContentEpochKeyStore.cs|gap|H13",
            "apps/local-node-host/Data/ContentStore/NodeContentKeyRotation.cs|gap|H13",
            "apps/local-node-host/Data/ContentStore/NodeTenantContentKeyProvider.cs|gap|H13",
            "apps/local-node-host/Data/Governance/FileTenantConsentStore.cs|gap|H53",
            "apps/local-node-host/Data/Governance/FileTenantGovernanceStateStore.cs|not-admitted|N07",
            "apps/local-node-host/Data/Identity/TenantMembershipAuthorityStore.cs|gap|H54",
        ];
        var actual = ReadCoverageTables(RepositoryRoot()).Where(columns => Regex.IsMatch(columns[0], @"^S\d{2}$"))
            .Select(columns => $"{columns[1].Trim('`')}|{columns[2]}|{columns[7]}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-10")]
    public void EveryOpenWriteReferencesAHandbackContainingItsProductionPath()
    {
        var tables = ReadCoverageTables(RepositoryRoot());
        var handbacks = tables.Where(columns => Regex.IsMatch(columns[0], @"^H\d{2}$"))
            .ToDictionary(columns => columns[0], columns => columns[1], StringComparer.Ordinal);
        var open = tables.Where(columns => columns.Length == 8 && columns[2] is "gap" or "mixed").ToArray();
        Assert.NotEmpty(open);
        foreach (var row in open)
        {
            var reference = row[7].Split(' ')[0];
            var path = row[0].StartsWith('`') ? row[0] : row[1];
            Assert.True(handbacks.TryGetValue(reference, out var paths), $"{path}: missing handback {reference}");
            Assert.Contains(path, paths!.Split("; "), StringComparer.Ordinal);
        }
    }

    private static string[][] ReadCoverageTables(string root) =>
        File.ReadLines(Path.Combine(root, "docs", "evidence", "T-519-durable-write-map.md"))
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .Select(line => line.Split('|').Skip(1).SkipLast(1).Select(column => column.Trim()).ToArray())
            .ToArray();

    [Fact]
    public void ScanReportsAPlantedUnclassifiedWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "ck6-write-scan-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "apps", "local-node-host", "Data", "Planted");
        Directory.CreateDirectory(planted);
        try
        {
            File.WriteAllText(Path.Combine(planted, "Offender.cs"),
                "sealed class Offender { async Task Go(DbContext c) { await c.SaveChangesAsync(); " +
                "await c.Set<Row>().ExecuteDeleteAsync(); /* c.SaveChanges(); */ } }");

            Assert.Equal([("apps/local-node-host/Data/Planted/Offender.cs", 2)], Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, (int Count, string Class, string Evidence)> ReadClassification(string root)
    {
        var path = Path.Combine(root, "apps", "local-node-host", "tests", "ArchTests", "durable-write-classification.tsv");
        var result = new Dictionary<string, (int, string, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var columns = line.Split('\t', 4);
            Assert.Equal(4, columns.Length);
            Assert.True(int.TryParse(columns[1], out var count) && count > 0, $"Bad count: {line}");
            Assert.True(result.TryAdd(columns[0], (count, columns[2], columns[3])), $"Duplicate row: {columns[0]}");
        }
        return result;
    }

    private static (string Path, int Count)[] Scan(string root)
    {
        var host = Path.Combine(root, "apps", "local-node-host");
        if (!Directory.Exists(host)) return [];
        return Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude" or "Migrations"))
            .Where(item => !item.Relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(item => (item.Relative, Count: WriteCall.Matches(StripComments(File.ReadAllText(item.File))).Count))
            .Where(item => item.Count > 0)
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();
    }

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
