using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// The set of static inter-company <see cref="EliminationRule"/>s that pair
/// balance-sheet subtypes for consolidation (ADR 0105 §3.2). Resolving a posted
/// line to a rule is keyed off the line's <see cref="AccountSubtype"/>; the
/// consolidation service (W0-6) then nets the matched pair per the rule's
/// <see cref="EliminationKind"/>.
/// </summary>
/// <remarks>
/// <see cref="EliminationKind.IncomeStatementMatch"/> is deliberately NOT a member
/// of this set: intra-group revenue/expense (management fees, inter-company rent)
/// is matched off the <c>CounterpartyLegalEntityId</c> stamp plus the existing
/// revenue/expense subtypes, not a dedicated subtype pair, so it is applied by the
/// consolidation service rather than resolved from a static rule (ADR 0105 §3.2).
/// </remarks>
public sealed class IntercompanyEliminationRuleSet
{
    private readonly IReadOnlyList<EliminationRule> _rules;
    private readonly Dictionary<AccountSubtype, EliminationRule> _byDueFrom;
    private readonly Dictionary<AccountSubtype, EliminationRule> _byDueTo;

    /// <summary>
    /// Builds a ruleset from the given rules.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Two rules share a <see cref="EliminationRule.DueFromSubtype"/> or a
    /// <see cref="EliminationRule.DueToSubtype"/> — an ambiguous ruleset.
    /// </exception>
    public IntercompanyEliminationRuleSet(IEnumerable<EliminationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var list = rules.ToList();
        _byDueFrom = new Dictionary<AccountSubtype, EliminationRule>();
        _byDueTo = new Dictionary<AccountSubtype, EliminationRule>();

        foreach (var rule in list)
        {
            if (!_byDueFrom.TryAdd(rule.DueFromSubtype, rule))
                throw new ArgumentException(
                    $"Duplicate elimination rule for DueFromSubtype '{rule.DueFromSubtype}'.",
                    nameof(rules));
            if (!_byDueTo.TryAdd(rule.DueToSubtype, rule))
                throw new ArgumentException(
                    $"Duplicate elimination rule for DueToSubtype '{rule.DueToSubtype}'.",
                    nameof(rules));
        }

        _rules = list;
    }

    /// <summary>The rules in declaration order.</summary>
    public IReadOnlyList<EliminationRule> Rules => _rules;

    /// <summary>
    /// The canonical v1 ruleset (ADR 0105 §3.2): trade due-from/due-to and
    /// inter-company loans both clear on the balance sheet; the parent's
    /// investment-in-subsidiary offsets the subsidiary's eliminated equity.
    /// </summary>
    public static IntercompanyEliminationRuleSet CanonicalV1 { get; } = new(
    [
        new EliminationRule(
            AccountSubtype.IntercompanyReceivable,
            AccountSubtype.IntercompanyPayable,
            EliminationKind.BalanceSheetClearing),
        new EliminationRule(
            AccountSubtype.IntercompanyLoanReceivable,
            AccountSubtype.IntercompanyLoanPayable,
            EliminationKind.BalanceSheetClearing),
        new EliminationRule(
            AccountSubtype.InvestmentInSubsidiary,
            AccountSubtype.IntercompanyEquity,
            EliminationKind.InvestmentInSubsidiary),
    ]);

    /// <summary>Finds the rule whose <see cref="EliminationRule.DueFromSubtype"/> matches, or null.</summary>
    public EliminationRule? FindByDueFromSubtype(AccountSubtype subtype)
        => _byDueFrom.TryGetValue(subtype, out var rule) ? rule : null;

    /// <summary>Finds the rule whose <see cref="EliminationRule.DueToSubtype"/> matches, or null.</summary>
    public EliminationRule? FindByDueToSubtype(AccountSubtype subtype)
        => _byDueTo.TryGetValue(subtype, out var rule) ? rule : null;
}
