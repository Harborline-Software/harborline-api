namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// The resolved set of entities that participate in a reporting group rooted at
/// <see cref="RootEntityId"/> (ADR 0104 §5). The consolidation reader resolves
/// in-scope charts from this scope — derived from the ownership graph — and
/// NEVER by following per-line <c>CounterpartyLegalEntityId</c> stamps
/// (ADR 0104 §3.1 no-read invariant; ADR 0105 §6).
/// </summary>
public sealed record ConsolidationScope(
    LegalEntityId RootEntityId,
    IReadOnlyList<ConsolidationMember> Members);

/// <summary>
/// One member of a <see cref="ConsolidationScope"/> and how it rolls up.
/// </summary>
public sealed record ConsolidationMember(
    LegalEntityId EntityId,
    ConsolidationPresentation Presentation);
