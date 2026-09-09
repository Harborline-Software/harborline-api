using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Transitional direct-token inventory for hosted-web identity and tenant authority.
/// </summary>
/// <remarks>
/// <para>
/// This inventory freezes literal spellings of the current process-global active-team and
/// static-operator seams until ADR 0160 Revision 3's R3-D request-principal retrofit and R3-I
/// readiness gate are satisfied. A listed path is known debt, not an approved exception. Counts must
/// match exactly, so remediation must lower the reviewed baseline in the same change.
/// </para>
/// <para>
/// This inventory is transitional. It retires when <c>identity.multi-tenant-web/v1</c> is admitted
/// under ADR 0160 R3-I: specifically, when item 4's manifest-bound fences prove every consequential
/// web route is fenced from the legacy process-global authorities and item 10's full isolation
/// evidence passes. At that point this text scanner is deleted, not zeroed. A passing empty
/// inventory is not readiness evidence, as
/// <see cref="CurrentHostedWebInventory_RemainsNonEmpty_AndIsNotReadinessEvidence"/> already proves.
/// </para>
/// <para>
/// Passing these tests does <b>not</b> prove multi-tenant web readiness or even discover every use of
/// legacy authority. This is a conservative text inventory, including comments and string literals;
/// aliases and reuse of an already-declared ambient authority can evade it. It is intentionally
/// bounded to the node's hosted <c>Health/</c> and <c>Feed/</c> surfaces and cannot prove downstream
/// database, cache, search, file, report, job, audit, client-state, or concurrent-session isolation.
/// In particular, <c>MultiTeamBootstrapHostedService.cs</c> and
/// <c>Data/Financial/ActiveTeamAuthorizationContext.cs</c> sit outside the scan. Both are
/// correct-as-is desktop-plane sites; their presence here documents the scope hole rather than a
/// complete remediation work list. Widening the scanner is separate work.
/// </para>
/// </remarks>
public sealed class HostedWebLegacyAuthorityDebtArchTests
{
    private const string BudgetLedgerFileName = "hosted-web-legacy-authority-debt.tsv";
    private const string BaselineShaKey = "# baseline-sha";
    private const string ReviewedAggregateDebtCeilingKey = "# reviewed-aggregate-debt-ceiling";

    private static readonly Regex GlobalTenantAuthority = new(
        @"\bIActiveTeamAccessor\b|NodeTenant\s*\.\s*Resolve\s*\(|" +
        @"ActiveTeamTenantContext\s*\.\s*ProjectTenantId\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex StaticActorAuthority = new(
        @"ActiveTeamAuthorizationContext\s*\.\s*(?:NodeOperator|LocalUserId)\b|" +
        @"CurrentPrincipalSignatureRoutes\s*\.\s*ResolveCurrentPrincipal\s*\(|" +
        @"\bEnvironment\s*\.\s*UserName\b|\bLocalOperatorUserId\s*=\s*\""local\""",
        RegexOptions.Compiled);

    [Fact(DisplayName =
        "ADR0160 R3 debt ratchet: hosted-web legacy-authority token inventory must match reviewed baseline " +
        "(transitional inventory only; NOT readiness evidence)")]
    public void HostedWebLegacyAuthorityTokenInventory_MatchesReviewedBaseline()
    {
        var actual = ScanHostedWebSurface();
        var ledger = ReadLedger();
        var failures = FindBudgetFailures(
            actual,
            ledger.Budgets,
            ledger.ReviewedAggregateDebtCeiling);

        Assert.True(
            failures.Count == 0,
            "Hosted-web legacy-authority token inventory changed. Review the source change and " +
            "ratchet the exact baseline in the same security-reviewed PR. This transitional text " +
            "inventory is a drift alarm, not multi-tenant readiness evidence.\n  " +
            string.Join("\n  ", failures));
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt ledger: the frozen migration baseline names its sha and aggregate ceiling")]
    public void DebtLedger_NamesFrozenMigrationBaseline_AndOwnsAggregateCeiling()
    {
        var ledger = ReadLedger();

        Assert.Equal("d3e730192f0478fdf9d1dffc67b2b35841ee7f07", ledger.BaselineSha);
        // Ticket 213 slice 2 moved it 436 -> 439, the first UPWARD move, and it is a reviewed one: the
        // consent-record route family is new hosted surface, and a route that must name the tenant it acts
        // for has no non-legacy tenant source until ADR 0160 R3-D lands. It is added at the MINIMUM shape
        // -- one tenant resolution point per file, the same 2/0 row AuthorizationAdminRoutes carries after
        // 205 slice 4, and the hosted endpoint at the 1/0 primary-constructor shape its siblings use --
        // rather than one resolution per handler (which would have been 7). Flagged for the security review
        // that owns this ledger: an upward move is permitted only for a NEW route family at minimum shape.
        // Ticket 205 slice 4 ratcheted 448 -> 436: converting the shared route guard to the point-of-use
        // gate collapsed AuthorizationAdminRoutes (5 -> 2) and SchedulingDefinitionRoutes (11 -> 2) onto one
        // tenant resolution point per request. The ceiling only ever moves DOWN.
        // One exception, by user ruling of 2026-09-07 (ticket 213 slice 2): until the ADR 0160 R3-D request-principal
        // retrofit lands, a genuinely new hosted route family may add its minimum inventory (one resolution point per
        // file) and this ceiling moves with it in the same commit, with the reason on the row; never for any other cause.
        // 294 s2b routes the current principal through the canonical roster key, removing
        // three static-authority tokens (two signing-route, one KG-route) and ratcheting 439 -> 436.
        Assert.Equal(436, ledger.ReviewedAggregateDebtCeiling);
        Assert.Equal(
            ledger.ReviewedAggregateDebtCeiling,
            ledger.Budgets.Values.Sum(static debt => debt.GlobalTenant + debt.StaticActor));
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt evidence: the current hosted-web inventory remains non-empty, so this test " +
        "cannot support a readiness claim")]
    public void CurrentHostedWebInventory_RemainsNonEmpty_AndIsNotReadinessEvidence()
    {
        var actual = ScanHostedWebSurface();

        Assert.True(
            actual.Values.Sum(static debt => debt.GlobalTenant + debt.StaticActor) > 0,
            "The transitional debt inventory is empty. Replace this temporary non-readiness evidence " +
            "with the strict manifest-bound ADR 0160 R3-I fences and full isolation suite.");
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt scanner bite-proof: literal seam spellings are conservatively inventoried")]
    public void DebtScanner_ConservativelyInventoriesLiteralSpellings()
    {
        var source = """
            IActiveTeamAccessor activeTeam;
            var tenant = NodeTenant.Resolve(activeTeam);
            var actor = ActiveTeamAuthorizationContext.LocalUserId;
            var installActor = ActiveTeamAuthorizationContext.NodeOperator;
            // IActiveTeamAccessor ActiveTeamAuthorizationContext.NodeOperator
            var conservativeString = "NodeTenant.Resolve( ActiveTeamAuthorizationContext.NodeOperator";
            """;

        var debt = CountDebt(source);

        Assert.Equal(4, debt.GlobalTenant);
        Assert.Equal(4, debt.StaticActor);
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt ratchet bite-proof: aggregate growth fails after per-path equality passes")]
    public void AggregateGrowth_FailsEvenWhenEveryPathMatchesItsUpdatedBaseline()
    {
        var actual = ParseBudgets("""
            Health/OneRoutes.cs	3	0
            Health/TwoRoutes.cs	1	0
            """);
        var updatedBudgets = ParseBudgets("""
            Health/OneRoutes.cs	3	0
            Health/TwoRoutes.cs	1	0
            """);

        Assert.Empty(FindPerPathFailures(actual, updatedBudgets));

        var failures = FindBudgetFailures(actual, updatedBudgets, reviewedAggregateDebtCeiling: 3);

        var failure = Assert.Single(failures);
        Assert.Contains("aggregate legacy authority GREW", failure, StringComparison.Ordinal);
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt ledger: disjoint union-merge edits retain one parseable row per path")]
    public void UnionMerge_DisjointPathEdits_ParseCleanly()
    {
        var baseline = """
            Health/OneRoutes.cs	3	0
            Health/TwoRoutes.cs	2	0
            """;
        var firstEdit = """
            Health/OneRoutes.cs	2	0
            Health/TwoRoutes.cs	2	0
            """;
        var secondEdit = """
            Health/OneRoutes.cs	3	0
            Health/TwoRoutes.cs	1	0
            """;

        var simulatedUnionResult = SimulateUnionMerge(baseline, firstEdit, secondEdit);
        var budgets = ParseBudgets(simulatedUnionResult);

        Assert.Equal(new DebtCount(2, 0), budgets["Health/OneRoutes.cs"]);
        Assert.Equal(new DebtCount(1, 0), budgets["Health/TwoRoutes.cs"]);
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt ledger: same-path union-merge edits retain duplicate keys and fail loudly")]
    public void UnionMerge_SamePathEdits_ThrowOnDuplicateKey()
    {
        const string baseline = "Health/OneRoutes.cs\t3\t0";
        const string firstEdit = "Health/OneRoutes.cs\t2\t0";
        const string secondEdit = "Health/OneRoutes.cs\t1\t0";

        var simulatedUnionResult = SimulateUnionMerge(baseline, firstEdit, secondEdit);
        Assert.Throws<ArgumentException>(() => ParseBudgets(simulatedUnionResult));
    }

    [Fact(DisplayName =
        "ADR0160 R3 debt ledger: repository merge policy uses union for the append-only data file")]
    public void DebtLedger_IsRegisteredWithUnionMergePolicy()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(NodeHostProjectRoot(), "..", ".."));
        var attributes = File.ReadAllLines(Path.Combine(repositoryRoot, ".gitattributes"));

        Assert.Contains(
            $"apps/local-node-host/tests/ArchTests/{BudgetLedgerFileName} merge=union",
            attributes);
    }

    private static Dictionary<string, DebtCount> ScanHostedWebSurface()
    {
        var hostRoot = NodeHostProjectRoot();
        var result = new Dictionary<string, DebtCount>(StringComparer.Ordinal);

        foreach (var surface in new[] { "Health", "Feed" })
        {
            var surfaceRoot = Path.Combine(hostRoot, surface);
            foreach (var file in Directory.EnumerateFiles(surfaceRoot, "*.cs", SearchOption.AllDirectories))
            {
                var debt = CountDebt(File.ReadAllText(file));
                if (debt.GlobalTenant == 0 && debt.StaticActor == 0)
                {
                    continue;
                }

                result[NormalizePath(Path.GetRelativePath(hostRoot, file))] = debt;
            }
        }

        return result;
    }

    private static DebtCount CountDebt(string source) => new(
        GlobalTenantAuthority.Matches(source).Count,
        StaticActorAuthority.Matches(source).Count);

    private static DebtLedger ReadLedger() =>
        ParseLedger(File.ReadAllText(Path.Combine(
            NodeHostProjectRoot(),
            "tests",
            "ArchTests",
            BudgetLedgerFileName)));

    private static DebtLedger ParseLedger(string tsv)
    {
        var lines = tsv.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var metadata = lines
            .Where(static line => line.StartsWith("# ", StringComparison.Ordinal))
            .Select(static line => line.Split('\t'))
            .ToDictionary(
                static parts => parts[0],
                static parts => parts[1],
                StringComparer.Ordinal);

        return new DebtLedger(
            metadata[BaselineShaKey],
            int.Parse(metadata[ReviewedAggregateDebtCeilingKey]),
            ParseBudgets(tsv));
    }

    private static List<string> FindBudgetFailures(
        IReadOnlyDictionary<string, DebtCount> actual,
        IReadOnlyDictionary<string, DebtCount> budgets,
        int reviewedAggregateDebtCeiling)
    {
        var failures = FindPerPathFailures(actual, budgets);
        var actualTotal = actual.Values.Sum(static debt => debt.GlobalTenant + debt.StaticActor);
        if (actualTotal > reviewedAggregateDebtCeiling)
        {
            failures.Add(
                $"aggregate legacy authority GREW: actual={actualTotal}, " +
                $"reviewed ceiling={reviewedAggregateDebtCeiling}");
        }

        return failures;
    }

    private static List<string> FindPerPathFailures(
        IReadOnlyDictionary<string, DebtCount> actual,
        IReadOnlyDictionary<string, DebtCount> budgets)
    {
        var failures = new List<string>();

        foreach (var (path, debt) in actual.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!budgets.TryGetValue(path, out var budget))
            {
                failures.Add($"NEW legacy-authority path: {path} " +
                             $"(tenant/global={debt.GlobalTenant}, static-actor={debt.StaticActor})");
                continue;
            }

            if (debt != budget)
            {
                var disposition = debt.GlobalTenant > budget.GlobalTenant ||
                                  debt.StaticActor > budget.StaticActor
                    ? "GREW"
                    : "was reduced without ratcheting its baseline";
                failures.Add($"legacy authority {disposition} in {path}: " +
                             $"tenant/global actual={debt.GlobalTenant}, baseline={budget.GlobalTenant}; " +
                             $"static-actor actual={debt.StaticActor}, baseline={budget.StaticActor}");
            }
        }

        foreach (var path in budgets.Keys.Except(actual.Keys, StringComparer.Ordinal))
        {
            failures.Add($"legacy authority was eliminated from {path} without removing its stale baseline");
        }

        return failures;
    }

    private static string SimulateUnionMerge(string baseline, string firstEdit, string secondEdit)
    {
        var baselineLines = LedgerLinesByPath(baseline);
        var firstLines = LedgerLinesByPath(firstEdit);
        var secondLines = LedgerLinesByPath(secondEdit);
        var mergedLines = new List<string>();

        foreach (var path in baselineLines.Keys
                     .Concat(firstLines.Keys)
                     .Concat(secondLines.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            baselineLines.TryGetValue(path, out var baselineLine);
            firstLines.TryGetValue(path, out var firstLine);
            secondLines.TryGetValue(path, out var secondLine);

            if (string.Equals(firstLine, secondLine, StringComparison.Ordinal))
            {
                if (firstLine is not null)
                {
                    mergedLines.Add(firstLine);
                }

                continue;
            }

            if (string.Equals(firstLine, baselineLine, StringComparison.Ordinal))
            {
                if (secondLine is not null)
                {
                    mergedLines.Add(secondLine);
                }

                continue;
            }

            if (string.Equals(secondLine, baselineLine, StringComparison.Ordinal))
            {
                if (firstLine is not null)
                {
                    mergedLines.Add(firstLine);
                }

                continue;
            }

            if (firstLine is not null)
            {
                mergedLines.Add(firstLine);
            }

            if (secondLine is not null)
            {
                mergedLines.Add(secondLine);
            }
        }

        return string.Join('\n', mergedLines);
    }

    private static Dictionary<string, string> LedgerLinesByPath(string tsv) => tsv
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToDictionary(
            static line => line.Split('\t')[0],
            StringComparer.Ordinal);

    private static Dictionary<string, DebtCount> ParseBudgets(string tsv) => tsv
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static line => !line.StartsWith("# ", StringComparison.Ordinal))
        .Select(static line => line.Split('\t'))
        .ToDictionary(
            static parts => parts[0],
            static parts => new DebtCount(int.Parse(parts[1]), int.Parse(parts[2])),
            StringComparer.Ordinal);

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string NodeHostProjectRoot([CallerFilePath] string thisFile = "")
    {
        var archTestsDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(archTestsDir)!;
        var hostRoot = Path.GetDirectoryName(testsDir)!;
        Assert.True(
            File.Exists(Path.Combine(hostRoot, "Harborline.LocalNodeHost.csproj")),
            $"Could not locate the node-host project root from '{thisFile}' (resolved '{hostRoot}').");
        return hostRoot;
    }

    // #3167 ratchet: Health/AdmissionRoutes.cs 3 -> 5 (tenant/global). The MTW-2 web-admitted-member
    // enrollment integration (PR 3168; MD dual review PASS x2 — council-verdict beacons
    // 2026-07-24T1344Z security + adversarial) added the mode-exclusive pairing redeem dispatch. The two
    // NEW tokens are both warranted at the DEVICE-ENROLLMENT plane (a node-local, caller-auth'd loopback
    // admin op with NO web session, so there is no SelectedSessionRequestPrincipal / bound-principal
    // equivalent): (1) MapCore's IActiveTeamAccessor param — the shared-core threading of the SAME active
    // team the route already depended on (baseline), factored so the public + pairing overloads share one
    // body rather than duplicating it; (2) NodeTenant.Resolve(pairing.ActiveTeam) — the node's tenant for
    // the R2 web-plane predicate + the R5 rate limiter, the same resolution the existing admission flow
    // uses (no plane-aware tenant exists at the device plane). A THIRD would-be token (a redundant
    // IActiveTeamAccessor param on the pairing overload) was REMOVED instead of ratcheted — the overload
    // reads the active team from the pairing bundle. No static-actor growth.
    //
    // #3228 ratchet: Health/AdmissionRoutes.cs 5 -> 4 (tenant/global). A REDUCTION, which this gate
    // deliberately also fails on so a debt budget can never silently drift out of review. PR 3235
    // (council-verdict beacon 2026-07-27T2123Z, deep review CHANGES-REQUESTED -> remedied) lifted the
    // rate limiter and the R2 web-plane predicate out of the HTTP route and into the shared
    // PairingRedeemDispatch, so both admit entry points funnel through ONE method. That move carried
    // NodeTenant.Resolve(pairing.ActiveTeam) out of AdmissionRoutes.cs with it — the token is not gone
    // from the system, it moved to the shared dispatch where it is now applied to BOTH planes rather
    // than the loopback route alone. No static-actor change.
    //
    // #3344 ratchet: two NEW tenant/global paths, +4 tokens (aggregate 407 -> 411, still under the
    // reviewed ceiling of 414). The Harborline App used to hand its renderer the string "everything" and let
    // the client decide what a user may see; the node now answers that question. GET /api/session/permissions serves BOTH planes, so it is registered unconditionally
    // and needs a tenant on each.
    //   (1) Health/WebSession/SelectedSessionIdentityRoutes.cs 3 (IActiveTeamAccessor x2 + one
    //       ActiveTeamTenantContext.ProjectTenantId call). A selected WEB session still takes its tenant
    //       from its own revalidated principal and touches none of this. The tokens are reached ONLY on
    //       the desktop bootstrap-bearer branch, which by construction has no selected principal and so
    //       has no other tenant source — the local node hosts exactly one active team.
    //   (2) Health/WebSession/HostedEffectivePermissionsApiEndpoint.cs 1 (the accessor threaded to the
    //       route above). No static-actor growth: the caller supplies neither Party id nor role, the
    //       desktop branch resolves the active roster's genesis Party, and every other audience is
    //       refused.
    // "The local node hosts exactly one active team" is true and is NOT the reassurance it reads as.
    // That one team is not necessarily this node's OWN: after a wire enrollment the active team is the
    // ADOPTED one, so the genesis party the desktop branch resolves belongs to the ADMITTING node. A
    // joined desktop node reports the admitter's owner permissions, not what its own admission granted.
    // This debt retires when the desktop plane carries a principal of its own rather than inferring one
    // from the single active team -- and until it does, the inference is wrong on every joined node,
    // not merely imprecise.
    private readonly record struct DebtLedger(
        string BaselineSha,
        int ReviewedAggregateDebtCeiling,
        IReadOnlyDictionary<string, DebtCount> Budgets);

    private readonly record struct DebtCount(int GlobalTenant, int StaticActor);
}
