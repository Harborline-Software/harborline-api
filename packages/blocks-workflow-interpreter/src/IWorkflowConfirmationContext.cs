using Harborline.Api.Blocks.Workflow.Durable;

namespace Harborline.Api.Blocks.Workflow.Interpreter;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0143 D-INV-5 / D-INV-8 / R1-E — the server-side identity seam the interpreter's
//  CP confirm path uses. Both principals a CP confirm needs are sourced HERE, never from
//  the confirm request body:
//    • the CONFIRMER   — resolved from the ambient authenticated principal (party via
//      IPartyContext, human/agent via IPrincipalKindResolver) at the confirm boundary;
//    • the PROPOSER    — the autonomous engine that reached + parked the CP action. An
//      autonomously-parked CP action is treated as AGENT-proposed (IsHuman = false), the
//      security-conservative default: separation-of-duties then ALWAYS requires a distinct
//      human confirmer (an engine can never self-confirm its own CP proposal).
//
//  The host implements this over its real principal context. The interpreter takes it as a
//  dependency so "who confirms" is a server fact, giving the broker's ConfirmAndBuildEffect
//  a confirmer/proposer it can run the shipped SoD rule against.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves the server-derived principals a CP workflow confirm requires (ADR 0143). The interpreter injects
/// this so the confirmer's party + human/agent kind — and the engine proposer's identity — are sourced from
/// the authenticated server context, never from the confirm payload.
/// </summary>
public interface IWorkflowConfirmationContext
{
    /// <summary>
    /// The proposer of an autonomously-parked CP action — the workflow engine's own service principal, with
    /// <see cref="WorkflowProposerIdentity.IsHuman"/> = <see langword="false"/>. Because the engine is a
    /// non-human proposer, the broker's SoD rule requires a DISTINCT human confirmer (the engine can never
    /// confirm the CP op it proposed).
    /// </summary>
    WorkflowProposerIdentity EngineProposer { get; }

    /// <summary>
    /// Resolves the CURRENT confirmer server-side: the party from the ambient <c>IPartyContext</c> and the
    /// human/agent bit from <c>IPrincipalKindResolver</c>. Never body-supplied — a client cannot assert an
    /// <c>isHuman</c>/<c>partyId</c> to launder an agent confirmation past SoD.
    /// </summary>
    ValueTask<WorkflowConfirmerIdentity> ResolveConfirmerAsync(CancellationToken ct = default);
}
