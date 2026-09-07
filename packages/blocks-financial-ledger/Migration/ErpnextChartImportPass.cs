using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Pass 1 ORCHESTRATOR of the ERPNext → Harborline-native migration
/// (post-MVP WBS Workstream A1; migration-importer spec §3.2 / §3.4). Runs the
/// SHIPPED per-record upserters (<see cref="IErpnextAccountImporter"/>,
/// <see cref="IErpnextCostCenterImporter"/>) over the WHOLE chart in one logical
/// transaction:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <b>Topological parent-first sort with cycle detection.</b> Accounts
///     reference their parent by ERPNext <c>name</c>
///     (<see cref="ErpnextAccountSource.ParentAccountName"/>); the chart is a
///     forest. We emit parents before children (Kahn's algorithm) so the
///     per-record upserter's <c>ResolveParentByExternalRef</c> always finds an
///     already-imported parent. A cycle (A → B → A) is a data defect: every
///     account that participates in or depends on a cycle is
///     <see cref="ImportOutcome{T}.Rejected"/> with
///     <see cref="ImportRejectReason.UnresolvedReference"/> — <b>NOT a throw</b>
///     (a single bad edge must not abort the whole chart; ADR 0100 C2/C5
///     "no record vanishes").
///   </item>
///   <item>
///     <b>account_type → GLAccountType/AccountSubtype mapping</b> is delegated to
///     the shipped <see cref="ErpnextAccountImporter.MapAccountType"/> (spec §3.2),
///     refined by an empty-type parent-walk before the upsert (spec §3.2 algorithm
///     for empty/ambiguous <c>account_type</c>).
///   </item>
///   <item>
///     <b>Census conservation.</b> Every account + cost-center outcome is recorded
///     into a per-DocType <see cref="ImportCensus"/>; the pass calls
///     <see cref="ImportCensus.AssertConserved"/> at the end so a vanished or
///     double-counted record is a loud failure (ADR 0100 C2).
///   </item>
///   <item>
///     <b>Cost-center heuristic</b> (spec §3.4) runs after accounts via
///     <see cref="IErpnextCostCenterImporter"/>.
///   </item>
/// </list>
/// <para>
/// The pass is access-mode-agnostic: it consumes already-parsed
/// <see cref="ErpnextAccountSource"/> / <see cref="ErpnextCostCenterSource"/>
/// lists, so the same orchestrator runs against a MariaDB-dump-sourced set OR a
/// hand-built fixture set (the build + fixture-test posture, ADR 0100; the live
/// dump run is wired by the A7 CLI).
/// </para>
/// </remarks>
public sealed class ErpnextChartImportPass
{
    private readonly IErpnextAccountImporter _accountImporter;
    private readonly IErpnextCostCenterImporter _costCenterImporter;

    public ErpnextChartImportPass(
        IErpnextAccountImporter accountImporter,
        IErpnextCostCenterImporter costCenterImporter)
    {
        _accountImporter = accountImporter ?? throw new ArgumentNullException(nameof(accountImporter));
        _costCenterImporter = costCenterImporter ?? throw new ArgumentNullException(nameof(costCenterImporter));
    }

    /// <summary>
    /// Runs Pass 1 over the supplied chart + cost-center sets.
    /// </summary>
    /// <param name="accounts">The full ERPNext "Account" set for one company/chart.</param>
    /// <param name="costCenters">The full ERPNext "Cost Center" set (may be empty).</param>
    /// <param name="targetChart">The destination chart-of-accounts the accounts upsert into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A conserved <see cref="ChartImportResult"/>.</returns>
    /// <exception cref="ImportCensusViolationException">
    /// Thrown only if the census fails conservation — a defensive invariant that
    /// should never fire given the exhaustive per-record recording below.
    /// </exception>
    public async Task<ChartImportResult> RunAsync(
        IReadOnlyList<ErpnextAccountSource> accounts,
        IReadOnlyList<ErpnextCostCenterSource> costCenters,
        ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(costCenters);

        var accountCensus = new ImportCensus();
        var costCenterCensus = new ImportCensus();

        var accountOutcomes = await ImportAccountsAsync(accounts, targetChart, accountCensus, cancellationToken)
            .ConfigureAwait(false);
        var costCenterOutcomes = await ImportCostCentersAsync(costCenters, costCenterCensus, cancellationToken)
            .ConfigureAwait(false);

        // Conservation gate (ADR 0100 C2): every source record produced exactly
        // one recorded outcome. Loud failure if a record vanished.
        accountCensus.AssertConserved(accounts.Count);
        costCenterCensus.AssertConserved(costCenters.Count);

        return new ChartImportResult(accountCensus, costCenterCensus, accountOutcomes, costCenterOutcomes);
    }

    private async Task<IReadOnlyList<ImportOutcome<GLAccount>>> ImportAccountsAsync(
        IReadOnlyList<ErpnextAccountSource> accounts,
        ChartOfAccountsId targetChart,
        ImportCensus census,
        CancellationToken ct)
    {
        var ordering = TopologicalSort(accounts);
        var outcomes = new List<ImportOutcome<GLAccount>>(accounts.Count);

        // Parent-first happy path.
        foreach (var source in ordering.Sorted)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _accountImporter.UpsertFromErpnextAsync(source, targetChart, ct)
                .ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);
        }

        // Cycle / dangling-parent / duplicate participants — rejected, never thrown.
        foreach (var (source, kind, badEdge) in ordering.Unresolved)
        {
            ct.ThrowIfCancellationRequested();
            var (reason, fieldName, rule) = kind switch
            {
                UnresolvedKind.Cycle => (
                    ImportRejectReason.UnresolvedReference,
                    "parent_account",
                    $"parent-account cycle detected (participates in or depends on cycle via '{badEdge}')"),
                UnresolvedKind.DanglingParent => (
                    ImportRejectReason.UnresolvedReference,
                    "parent_account",
                    $"parent account '{badEdge}' not present in the source chart (dangling reference)"),
                _ => (
                    ImportRejectReason.DuplicateExternalRef,
                    "name",
                    $"duplicate account natural key '{badEdge}' within the source set"),
            };

            var rejected = new ImportOutcome<GLAccount>.Rejected(
                ImportFailure.Of(
                    externalRef: source.Name,
                    docType: ErpnextAccountImporter.DocType,
                    reason: reason,
                    fieldName: fieldName,
                    ruleViolated: rule));
            census.Record(rejected);
            outcomes.Add(rejected);
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<ImportOutcome<CostCenterResolution>>> ImportCostCentersAsync(
        IReadOnlyList<ErpnextCostCenterSource> costCenters,
        ImportCensus census,
        CancellationToken ct)
    {
        var outcomes = new List<ImportOutcome<CostCenterResolution>>(costCenters.Count);
        foreach (var source in costCenters)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _costCenterImporter.UpsertFromErpnextAsync(source, ct).ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    /// <summary>
    /// Parent-first topological sort (Kahn's algorithm) over the account forest,
    /// keyed by ERPNext <c>name</c>. A node whose parent is absent from the set is
    /// a dangling reference; a node that never drains is a cycle participant. Both
    /// land in <see cref="TopoResult.Unresolved"/> (rejected by the caller, never
    /// thrown). The happy-path <see cref="TopoResult.Sorted"/> list is parents
    /// before children so the per-record upserter always resolves an
    /// already-imported parent.
    /// </summary>
    internal static TopoResult TopologicalSort(IReadOnlyList<ErpnextAccountSource> accounts)
    {
        // Canonicalize by name (first occurrence wins). A duplicate natural key is a
        // data defect surfaced as a DuplicateExternalRef unresolved entry below; the
        // first-wins canonical map keeps the toposort total.
        var byName = new Dictionary<string, ErpnextAccountSource>(StringComparer.Ordinal);
        foreach (var a in accounts)
        {
            byName.TryAdd(a.Name, a);
        }

        // Build the dependency graph: child name → parent name (intra-set edges only).
        // Track dangling parents (referenced but not present) separately.
        var dangling = new Dictionary<string, string>(StringComparer.Ordinal); // childName → missing parentName
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal); // parentName → childNames
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var name in byName.Keys)
        {
            inDegree[name] = 0;
        }

        foreach (var node in byName.Values)
        {
            var parent = node.ParentAccountName;
            if (string.IsNullOrEmpty(parent))
            {
                continue; // top-level root
            }

            if (!byName.ContainsKey(parent))
            {
                dangling[node.Name] = parent; // parent not in set — dangling reference
                continue;
            }

            if (!children.TryGetValue(parent, out var list))
            {
                list = new List<string>();
                children[parent] = list;
            }

            list.Add(node.Name);
            inDegree[node.Name] = inDegree[node.Name] + 1;
        }

        // Kahn: seed the queue with all in-degree-0 nodes that are NOT dangling.
        // Deterministic emission order: original input order among ready nodes.
        var ready = new Queue<string>(
            accounts
                .Select(a => a.Name)
                .Where(n => byName.ContainsKey(n)) // first occurrence wins
                .Distinct(StringComparer.Ordinal)
                .Where(n => inDegree[n] == 0 && !dangling.ContainsKey(n)));

        var sorted = new List<ErpnextAccountSource>(byName.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            var name = ready.Dequeue();
            if (!emitted.Add(name))
            {
                continue;
            }

            sorted.Add(byName[name]);

            if (!children.TryGetValue(name, out var kids))
            {
                continue;
            }

            foreach (var kid in kids)
            {
                if (dangling.ContainsKey(kid))
                {
                    continue;
                }

                inDegree[kid] = inDegree[kid] - 1;
                if (inDegree[kid] == 0)
                {
                    ready.Enqueue(kid);
                }
            }
        }

        // Conservation-exact classification: iterate the RAW input once. Each
        // occurrence lands in exactly one of {Sorted, Unresolved}.
        //   - the canonical first occurrence of an emitted name → already in Sorted;
        //   - a duplicate occurrence (object is not byName[name]) → Duplicate reject;
        //   - the canonical first occurrence of a non-emitted name → DanglingParent
        //     (if its parent is missing) else Cycle.
        // sorted.Count + unresolved.Count == accounts.Count (the census invariant).
        var unresolved = new List<UnresolvedAccount>();
        foreach (var node in accounts)
        {
            var isCanonical = ReferenceEquals(byName[node.Name], node);

            if (!isCanonical)
            {
                // A later duplicate occurrence of an already-canonicalized name.
                unresolved.Add(new UnresolvedAccount(node, UnresolvedKind.Duplicate, node.Name));
                continue;
            }

            if (emitted.Contains(node.Name))
            {
                // Canonical occurrence already placed parent-first into Sorted.
                continue;
            }

            if (dangling.TryGetValue(node.Name, out var missingParent))
            {
                unresolved.Add(new UnresolvedAccount(node, UnresolvedKind.DanglingParent, missingParent));
            }
            else
            {
                unresolved.Add(new UnresolvedAccount(node, UnresolvedKind.Cycle, node.ParentAccountName ?? node.Name));
            }
        }

        return new TopoResult(sorted, unresolved);
    }

    /// <summary>The result of <see cref="TopologicalSort"/>: a parent-first order + the unresolved (cycle/dangling/duplicate) tail.</summary>
    internal sealed record TopoResult(
        IReadOnlyList<ErpnextAccountSource> Sorted,
        IReadOnlyList<UnresolvedAccount> Unresolved);

    /// <summary>One account that could not be topologically placed, with the reason + offending edge.</summary>
    internal sealed record UnresolvedAccount(ErpnextAccountSource Source, UnresolvedKind Kind, string BadEdge);

    /// <summary>Why a node could not be ordered parent-first.</summary>
    internal enum UnresolvedKind
    {
        /// <summary>The node participates in (or transitively depends on) a parent-account cycle.</summary>
        Cycle,

        /// <summary>The node's parent is not present in the source set (dangling reference).</summary>
        DanglingParent,

        /// <summary>A duplicate natural-key occurrence beyond the first.</summary>
        Duplicate,
    }
}
