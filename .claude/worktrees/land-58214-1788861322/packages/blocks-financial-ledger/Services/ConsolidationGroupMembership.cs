using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Scope-relative in-group membership for inter-company elimination
/// (ADR 0105 §3.2.1, F-2(d)). A counterparty entity is "in-group" for a given
/// consolidation ONLY if it is itself within the current consolidation scope's
/// entity set. A fee / loan / rent whose counterparty is OUT of scope is a
/// genuine third-party transaction at that reporting boundary and stays
/// UN-eliminated — the same posted line eliminates in a wider scope and remains
/// in a narrower one (ADR 0105 §3.3 worked example).
/// </summary>
/// <remarks>
/// Deliberately built from a flat entity-id set, NOT a <c>ConsolidationScope</c>
/// record, so the W0-6 reconciliation of the two scope shapes (the W0-1 record
/// vs ADR 0105 §4.1) leaves this membership primitive untouched. The
/// consolidation service computes the in-scope entity set from whichever scope
/// shape it holds and hands it here.
/// </remarks>
public sealed class ConsolidationGroupMembership
{
    private readonly HashSet<LegalEntityId> _inScopeEntities;

    /// <summary>
    /// Builds membership from the entities resolved to be in the current scope
    /// (the consolidation root plus every member of its reporting group).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="inScopeEntities"/> is null.</exception>
    /// <exception cref="ArgumentException">Any entity id is <c>default</c> (a malformed scope).</exception>
    public ConsolidationGroupMembership(IEnumerable<LegalEntityId> inScopeEntities)
    {
        ArgumentNullException.ThrowIfNull(inScopeEntities);

        _inScopeEntities = new HashSet<LegalEntityId>();
        foreach (var entity in inScopeEntities)
        {
            if (entity == default)
                throw new ArgumentException(
                    "A consolidation scope cannot contain a default LegalEntityId.",
                    nameof(inScopeEntities));
            _inScopeEntities.Add(entity);
        }
    }

    /// <summary>The distinct entities resolved into the current consolidation scope.</summary>
    public IReadOnlyCollection<LegalEntityId> InScopeEntities => _inScopeEntities;

    /// <summary>
    /// True iff <paramref name="counterparty"/> is itself within the current
    /// consolidation scope — i.e. its balances are being consolidated alongside
    /// this entity's, so an inter-company pair against it should eliminate.
    /// </summary>
    public bool IsInGroup(LegalEntityId counterparty) => _inScopeEntities.Contains(counterparty);

    /// <summary>
    /// Nullable overload for a <see cref="JournalEntryLine.CounterpartyLegalEntityId"/>
    /// stamp. A line with NO counterparty stamp is an ordinary single-entity line
    /// — there is nothing to eliminate, so it is treated as out-of-group (false).
    /// </summary>
    public bool IsInGroup(LegalEntityId? counterparty)
        => counterparty.HasValue && IsInGroup(counterparty.Value);
}
