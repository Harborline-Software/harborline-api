using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory <see cref="IConsolidationService"/> — the terminal Wave-0 accounting node
/// (W0-6). Resolves the scope's entities to charts (<see cref="IEntityChartResolver"/>),
/// reads per-chart aggregate balances AND the counterparty-grained inter-company slice
/// through the §2 fan-out (which enforces the load-bearing fail-closed chart∈tenant
/// invariant 0105-1), applies the ADR 0105 §3 elimination rules <b>scope-relatively</b>
/// (§3.2.1), and bands the result per financial F-1 (§4.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two bands, always produced.</b> <see cref="ConsolidationBand.OwnedGroupSubtotal"/>
/// (root + owned subs) and <see cref="ConsolidationBand.CombinedTotal"/> (owned group +
/// common-control siblings) are both computed every call; they are equal when the scope
/// has no siblings. Eliminations and warnings are band-stamped because they are
/// scope-relative (§3.2.1) — a fee that eliminates in the wider CombinedTotal can stay
/// un-eliminated in the narrower OwnedGroupSubtotal when its counterparty is an
/// out-of-owned-group sibling.
/// </para>
/// <para>
/// <b>Equity rollup is structural (§3.4).</b> Equity accounts are summed across band
/// members; the <see cref="EliminationKind.InvestmentInSubsidiary"/> elimination removes
/// the parent's investment against the sub's <see cref="AccountSubtype.IntercompanyEquity"/>
/// so the sub's equity does not double-count — that IS the consolidated equity rollup. A
/// common-control sibling is never owned, so no investment-in-sub elimination fires for it
/// and its equity sums side-by-side (combined presentation, §4.2).
/// </para>
/// <para>
/// <b>Warn, never plug (financial F-3).</b> Residuals surface as
/// <see cref="ConsolidationWarning"/>s; no band is ever forced to tie by a compensating
/// entry. The balance-sheet tie check is the trial-balance identity Σ(signed balances) = 0
/// over the band's net map — the cleanest tie available over a read substrate that returns
/// signed raw balances with no normal-side normalization.
/// </para>
/// <para>
/// <b>Elimination is report-time (§3.3, §6).</b> No journal entry is posted to any chart;
/// the only trace of a removal is the <see cref="EliminationEntry"/> list.
/// </para>
/// </remarks>
public sealed class ConsolidationService : IConsolidationService
{
    private readonly IConsolidationReadModel _readModel;
    private readonly IEntityChartResolver _entityChartResolver;
    private readonly IAccountResolver _accountResolver;
    private readonly IntercompanyEliminationRuleSet _eliminationRules;

    /// <summary>
    /// Construct bound to the consolidation read fan-out, the entity→chart resolver, the
    /// account resolver (for subtype/type classification), and an optional elimination
    /// ruleset (defaults to <see cref="IntercompanyEliminationRuleSet.CanonicalV1"/>).
    /// </summary>
    public ConsolidationService(
        IConsolidationReadModel readModel,
        IEntityChartResolver entityChartResolver,
        IAccountResolver accountResolver,
        IntercompanyEliminationRuleSet? eliminationRules = null)
    {
        _readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
        _entityChartResolver = entityChartResolver ?? throw new ArgumentNullException(nameof(entityChartResolver));
        _accountResolver = accountResolver ?? throw new ArgumentNullException(nameof(accountResolver));
        _eliminationRules = eliminationRules ?? IntercompanyEliminationRuleSet.CanonicalV1;
    }

    /// <inheritdoc />
    public async Task<ConsolidatedBalanceSet> ConsolidateAsync(
        TenantId tenantId,
        ConsolidationScope scope,
        System.DateOnly asOf,
        string snapshotMarker,
        ReportingBasis basis,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(scope);

        // F-5: one basis per scope; v1 is accrual-only. Cash is a Wave-3 build —
        // rejected loudly so the gap is disclosed, not implied as parity.
        if (basis == ReportingBasis.Cash)
        {
            throw new NotSupportedException(
                "Cash-basis consolidation is a Wave-3 build (ADR 0105 §5, financial F-5); " +
                "v1 supports the accrual basis only.");
        }

        // Scope reconciliation. The shipped W0-1 per-member-Presentation record is
        // canonical (the ADR §4.1 illustrative record is superseded). The root is always
        // Consolidated; an Equity-method member is a documented deferral and rejected.
        var presentationByEntity = new Dictionary<LegalEntityId, ConsolidationPresentation>
        {
            [scope.RootEntityId] = ConsolidationPresentation.Consolidated,
        };
        var orderedEntities = new List<LegalEntityId> { scope.RootEntityId };

        foreach (var member in scope.Members)
        {
            if (member.EntityId == scope.RootEntityId) continue; // root is always Consolidated

            if (member.Presentation == ConsolidationPresentation.Equity)
            {
                throw new NotSupportedException(
                    "Equity-method consolidation is a documented deferral (ADR 0105 §3.4); " +
                    "the W0-1 resolver never emits the Equity band.");
            }

            if (presentationByEntity.TryAdd(member.EntityId, member.Presentation))
            {
                orderedEntities.Add(member.EntityId);
            }
        }

        var ownedGroup = presentationByEntity
            .Where(kv => kv.Value == ConsolidationPresentation.Consolidated)
            .Select(kv => kv.Key)
            .ToHashSet();
        var allInScope = presentationByEntity.Keys.ToHashSet();

        // Resolve each in-scope entity → chart (one entity → one chart in v1). The
        // chart∈tenant guarantee is NOT here — it is enforced downstream by the §2
        // fan-out gate; an unresolved entity fails closed (never a silent default).
        var chartByEntity = new Dictionary<LegalEntityId, ChartOfAccountsId>();
        foreach (var entity in orderedEntities)
        {
            chartByEntity[entity] = await _entityChartResolver
                .GetChartForEntityAsync(tenantId, entity, cancellationToken)
                .ConfigureAwait(false);
        }
        var charts = chartByEntity.Values.Distinct().ToList();

        // Reads — both fan-outs enforce the SAME fail-closed chart∈tenant gate (0105-1).
        var aggregateByChart = await _readModel
            .GetBalancesForChartsAsync(tenantId, charts, asOf, snapshotMarker, cancellationToken)
            .ConfigureAwait(false);
        var intercompanyByChart = await _readModel
            .GetIntercompanyBalancesForChartsAsync(tenantId, charts, asOf, snapshotMarker, cancellationToken)
            .ConfigureAwait(false);

        var aggregateByEntity = new Dictionary<LegalEntityId, IReadOnlyDictionary<GLAccountId, decimal>>();
        var cpByEntity = new Dictionary<LegalEntityId, IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>>();
        foreach (var entity in orderedEntities)
        {
            var chart = chartByEntity[entity];
            aggregateByEntity[entity] = aggregateByChart.TryGetValue(chart, out var agg)
                ? agg
                : new Dictionary<GLAccountId, decimal>();
            cpByEntity[entity] = intercompanyByChart.TryGetValue(chart, out var cp)
                ? cp
                : new Dictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>();
        }

        // Account metadata (subtype/type) for elimination classification. Include
        // inactive accounts: a soft-deleted intercompany account can still carry posted
        // activity that must be classified, so excluding it would silently skip a real
        // elimination.
        var accountInfo = new Dictionary<GLAccountId, GLAccount>();
        foreach (var chart in charts)
        {
            foreach (var account in await _accountResolver
                         .EnumerateForChartAsync(chart, includeInactive: true, cancellationToken)
                         .ConfigureAwait(false))
            {
                accountInfo[account.Id] = account;
            }
        }

        // PerEntityColumns — raw, pre-elimination, never collapsed (F-1).
        var perEntityColumns = orderedEntities
            .Select(entity => new EntityBalanceColumn(
                entity,
                chartByEntity[entity],
                presentationByEntity[entity],
                aggregateByEntity[entity]))
            .ToList();

        var eliminations = new List<EliminationEntry>();
        var warnings = new List<ConsolidationWarning>();

        var ownedGroupSubtotal = EliminateBand(
            ConsolidationBand.OwnedGroupSubtotal, bandMembers: ownedGroup, ownedGroup: ownedGroup,
            aggregateByEntity, cpByEntity, accountInfo, eliminations, warnings);
        var combinedTotal = EliminateBand(
            ConsolidationBand.CombinedTotal, bandMembers: allInScope, ownedGroup: ownedGroup,
            aggregateByEntity, cpByEntity, accountInfo, eliminations, warnings);

        return new ConsolidatedBalanceSet(
            PerEntityColumns: perEntityColumns,
            OwnedGroupSubtotal: ownedGroupSubtotal,
            CombinedTotal: combinedTotal,
            Eliminations: eliminations,
            Warnings: warnings,
            Basis: basis);
    }

    /// <summary>
    /// Sum the <paramref name="bandMembers"/>' aggregate balances, then walk their
    /// counterparty-grained inter-company slices and eliminate matched pairs whose
    /// counterparty is ITSELF in <paramref name="bandMembers"/> (scope-relative, §3.2.1).
    /// <see cref="EliminationKind.InvestmentInSubsidiary"/> is tighter: it eliminates only
    /// when BOTH legs sit in <paramref name="ownedGroup"/> (§3.2, §4) — a common-control
    /// sibling's equity is presented side-by-side, never eliminated. Records each removal
    /// as an <see cref="EliminationEntry"/>, accumulates residuals, and appends warnings —
    /// never plugging the band to tie (F-3).
    /// </summary>
    private IReadOnlyDictionary<GLAccountId, decimal> EliminateBand(
        ConsolidationBand band,
        HashSet<LegalEntityId> bandMembers,
        HashSet<LegalEntityId> ownedGroup,
        IReadOnlyDictionary<LegalEntityId, IReadOnlyDictionary<GLAccountId, decimal>> aggregateByEntity,
        IReadOnlyDictionary<LegalEntityId, IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>> cpByEntity,
        IReadOnlyDictionary<GLAccountId, GLAccount> accountInfo,
        List<EliminationEntry> eliminations,
        List<ConsolidationWarning> warnings)
    {
        // Net = sum of the band members' raw aggregate balances. Keys are retained even
        // when an elimination zeroes them (a fully-eliminated account shows net 0m).
        var net = new Dictionary<GLAccountId, decimal>();
        foreach (var entity in bandMembers)
        {
            foreach (var (accountId, balance) in aggregateByEntity[entity])
            {
                net[accountId] = net.GetValueOrDefault(accountId) + balance;
            }
        }

        var bsResidualByRule = new Dictionary<EliminationRule, decimal>();
        var ismResidual = 0m;

        foreach (var entity in bandMembers)
        {
            foreach (var (accountId, byCounterparty) in cpByEntity[entity])
            {
                foreach (var (counterparty, amount) in byCounterparty)
                {
                    // Scope-relative: only eliminate when the counterparty is itself a
                    // member of THIS band (§3.2.1). A fee booked against an out-of-band
                    // entity stays un-eliminated here.
                    if (!bandMembers.Contains(counterparty)) continue;
                    if (!accountInfo.TryGetValue(accountId, out var account)) continue;

                    EliminationKind kind;
                    EliminationRule? matchedRule = null;
                    if (account.Subtype is { } subtype
                        && (_eliminationRules.FindByDueFromSubtype(subtype)
                            ?? _eliminationRules.FindByDueToSubtype(subtype)) is { } rule)
                    {
                        matchedRule = rule;
                        kind = rule.Kind;
                    }
                    else if (account.Type is GLAccountType.Revenue or GLAccountType.Expense)
                    {
                        // Intra-group revenue/expense match — keyed off the counterparty
                        // stamp + the P&L type, not a static subtype rule (§3.2).
                        kind = EliminationKind.IncomeStatementMatch;
                    }
                    else
                    {
                        // Counterparty-stamped but not an inter-company balance-sheet
                        // subtype and not P&L — nothing in the ruleset eliminates it.
                        continue;
                    }

                    // Investment-in-subsidiary is an OWNERSHIP elimination: it fires only
                    // when both the holder and the owned entity sit in the owned group
                    // (§3.2, §4). In a combined presentation a sibling's equity stays
                    // side-by-side — never eliminated.
                    if (kind == EliminationKind.InvestmentInSubsidiary
                        && !(ownedGroup.Contains(entity) && ownedGroup.Contains(counterparty)))
                    {
                        continue;
                    }

                    net[accountId] = net.GetValueOrDefault(accountId) - amount;
                    eliminations.Add(new EliminationEntry(band, kind, accountId, counterparty, amount));

                    switch (kind)
                    {
                        case EliminationKind.BalanceSheetClearing:
                            bsResidualByRule[matchedRule!] =
                                bsResidualByRule.GetValueOrDefault(matchedRule!) + amount;
                            break;
                        case EliminationKind.IncomeStatementMatch:
                            ismResidual += amount;
                            break;
                        // InvestmentInSubsidiary residual is NOT a separate IC imbalance;
                        // any mismatch flows into the Σ-net balance-sheet tie below.
                    }
                }
            }
        }

        foreach (var (rule, residual) in bsResidualByRule)
        {
            if (residual != 0m)
            {
                warnings.Add(new ConsolidationWarning(
                    band,
                    ConsolidationWarningKind.IntercompanyImbalance,
                    $"Inter-company balance-sheet pair [{rule.DueFromSubtype}/{rule.DueToSubtype}] did not net " +
                    $"to zero within the {band} band; residual {residual} remains.",
                    residual));
            }
        }

        if (ismResidual != 0m)
        {
            warnings.Add(new ConsolidationWarning(
                band,
                ConsolidationWarningKind.IncomeStatementTimingMismatch,
                $"Intra-group revenue/expense did not net to zero within the {band} band; residual {ismResidual} " +
                "remains (likely a period-timing mismatch — F-6).",
                ismResidual));
        }

        var bsTie = net.Values.Sum();
        if (bsTie != 0m)
        {
            warnings.Add(new ConsolidationWarning(
                band,
                ConsolidationWarningKind.BalanceSheetOutOfBalance,
                $"Consolidated balances for the {band} band do not sum to zero (Σ = {bsTie}); " +
                "rendered as-is, never plugged (F-3).",
                bsTie));
        }

        return net;
    }
}
