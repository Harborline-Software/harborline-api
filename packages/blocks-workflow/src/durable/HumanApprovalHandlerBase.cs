using System.Text.Json;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// Shared scaffolding for the human-task approval handlers (ADR 0135 A0 — the de-duplicated middle option
/// from the adversarial-group verdict's §3 steelman). The three v1 CP-park handlers
/// (<see cref="InvoiceApprovalHandler"/>, the grant <c>GrantIssuanceHandler</c> /
/// <c>GrantRevocationHandler</c>) share the SAME human-action funnel: a parked human-task is resumed by a
/// trigger carrying a string <c>decision</c> field that the handler dispatches on. Before A0 this parser was
/// copy-pasted <b>byte-for-byte</b> in three files — three places a parsing bug or a missing-case could
/// silently diverge in security-sensitive scaffolding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scaffolding only — NOT the control flow (deliberate, per the verdict).</b> This base provides the one
/// tested <see cref="ReadHumanAction"/> human-action parser. It does NOT data-drive the decision logic: each
/// handler stays a NAMED <see cref="IWorkflowStepHandler"/> whose CP-park is still arch-tested by name, and
/// whose per-handler logic (the invoice D7 threshold table; the grant F-1 authority re-resolve +
/// at-approve attenuation re-check) stays as host code in the handler. That is the §3 "deduplicate the
/// scaffolding without data-driving the control flow" scope — the safety property is untouched, and the
/// definition interpreter (slice A1) is deferred.
/// </para>
/// <para>
/// <b>Why a static helper, not an inheritance base with abstract decide methods.</b> The three handlers have
/// genuinely different decision surfaces (one sync + a decision table; two async + an authority provider),
/// different state payloads, and different typed-outcome sets. Forcing them under one abstract base would
/// couple unrelated logic. A shared static parser captures the byte-identical part (the real duplication the
/// verdict named) with zero coupling of the divergent part — the smallest change that retires the three copies.
/// </para>
/// </remarks>
public static class HumanApprovalHandlerBase
{
    /// <summary>
    /// Parses the required string <c>decision</c> field from a parked human-task's resume payload — the
    /// single, tested implementation of the human-action funnel shared by every CP-park handler. Returns the
    /// raw decision verb (<c>approve</c> / <c>reject</c> / <c>send-back</c> / …); the caller dispatches on it
    /// (an unknown verb is the caller's <c>default</c> case, since the valid set differs per handler).
    /// </summary>
    /// <param name="payloadJson">The human-action trigger payload (e.g. <c>{ "decision": "approve" }</c>).</param>
    /// <param name="stepName">
    /// The handler's human-task step name (e.g. <c>approve</c> / <c>confirm</c>) — woven into the error
    /// messages so a malformed payload names the step it failed for, preserving the per-handler diagnostics.
    /// </param>
    /// <returns>The decision verb (never null/empty — a blank decision throws).</returns>
    /// <exception cref="InvalidOperationException">
    /// If the payload is null/blank, is not a JSON object with a string <c>decision</c>, or the decision is empty.
    /// </exception>
    public static string ReadHumanAction(string payloadJson, string stepName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidOperationException(
                $"A human-action trigger for the {stepName} step requires a payload with a 'decision' field.");
        }

        using var doc = JsonDocument.Parse(payloadJson);
        if (!doc.RootElement.TryGetProperty("decision", out var decisionEl)
            || decisionEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"A human-action {stepName}-step payload must carry a string 'decision' field.");
        }

        var decision = decisionEl.GetString();
        if (string.IsNullOrEmpty(decision))
        {
            throw new InvalidOperationException(
                $"A human-action {stepName}-step payload's 'decision' field must be a non-empty string.");
        }

        return decision;
    }

    /// <summary>
    /// The CP separation-of-duties gate for the human-action approve funnel (ADR 0143 D-INV-5),
    /// pinned into the code floor as the .NET mirror of the harborline-sdk broker's confirm-side check.
    /// It is the ONE place a CP <c>approve</c> is admitted, so every CP-park handler that resumes on a
    /// human approve funnels the confirmer/proposer through this SAME rule (no per-handler drift).
    ///
    /// <para><b>Reconciled rule (matches the TS broker + the security-engineering F2 amendment):</b></para>
    /// <list type="number">
    ///   <item>A genuine CP confirmation MUST be made by a HUMAN principal — a non-interactive
    ///     agent/service principal may PROPOSE a CP op but can never CONFIRM one.</item>
    ///   <item>An AGENT-proposed CP op MUST be confirmed by a principal DISTINCT from the proposer
    ///     (the on-the-loop guard). A HUMAN proposer MAY self-confirm on the single-user desktop —
    ///     the accountable-human compensating control (§4.4) — so self-approval is refused ONLY when
    ///     the proposer is non-human.</item>
    /// </list>
    ///
    /// <para><b>Principal source (server-derived, never body-supplied):</b> the confirmer party is
    /// resolved from the ambient <c>IPartyContext</c> at the confirm boundary (per its confused-deputy
    /// contract), and the proposer party from the parked proposal — NOT from a request body. The
    /// invocation of this gate belongs to the human-task confirm route that resumes a parked CP
    /// approve; that route is part of the deferred A1 / broker-PEP build (wf-key §3.6 / ADR 0143 F1
    /// names the .NET PEP as deferred). This method is the reusable, arch-tested rule that route calls.</para>
    /// </summary>
    /// <param name="proposerPartyId">The party that PROPOSED / originated the parked CP op.</param>
    /// <param name="proposerIsHuman">Whether the proposing principal is a human (vs an agent/service host).</param>
    /// <param name="confirmerPartyId">The party CONFIRMING (resolved from the ambient IPartyContext).</param>
    /// <param name="confirmerIsHuman">Whether the confirming principal is a human.</param>
    /// <param name="stepName">The human-task step name (woven into the diagnostic).</param>
    /// <exception cref="WorkflowSodViolationException">If the confirmer is non-human, or an agent-proposed CP op is self-confirmed.</exception>
    public static void EnsureApproverSeparationOfDuties(
        Guid proposerPartyId,
        bool proposerIsHuman,
        Guid confirmerPartyId,
        bool confirmerIsHuman,
        string stepName)
    {
        // (1) A CP confirmation must be made by a human principal.
        if (!confirmerIsHuman)
        {
            throw new WorkflowSodViolationException(
                $"separation-of-duties (D-INV-5): the {stepName} confirmation must be made by a human principal; " +
                $"confirmer party '{confirmerPartyId}' is not human — an agent may propose, only a human confirms.");
        }

        // (2) An agent-proposed CP op requires a DISTINCT confirmer (a human proposer may self-confirm).
        if (!proposerIsHuman && confirmerPartyId == proposerPartyId)
        {
            throw new WorkflowSodViolationException(
                $"separation-of-duties (D-INV-5): party '{confirmerPartyId}' cannot confirm a {stepName} CP op it " +
                "proposed as a non-human actor — an agent-proposed CP op requires a distinct human confirmer.");
        }
    }
}

/// <summary>Stable, locale-independent approval-gate violation codes (a client localizes off these).</summary>
public static class WorkflowApprovalCodes
{
    /// <summary>A CP approval violates separation of duties (non-human confirmer, or agent self-approval).</summary>
    public const string SeparationOfDutiesViolation = "workflow.approval.separation_of_duties_violation";
}

/// <summary>
/// Thrown when a CP human-action approval violates separation of duties (ADR 0143 D-INV-5): a non-human
/// principal attempted to confirm a CP op, or an agent-proposed CP op was self-confirmed. Carries the
/// stable <see cref="WorkflowApprovalCodes.SeparationOfDutiesViolation"/> code.
/// </summary>
public sealed class WorkflowSodViolationException(string message) : InvalidOperationException(message)
{
    /// <summary>The stable violation code (a localizing client keys off this).</summary>
    public string Code => WorkflowApprovalCodes.SeparationOfDutiesViolation;
}
