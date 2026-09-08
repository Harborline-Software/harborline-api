using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The host-side effect builder for the <c>ledger.post-journal-entry</c> capability — the CP effect the ADR
/// 0135 A1 interpreter reaches through the broker-PEP (never directly). It stages a real balanced
/// <see cref="JournalEntry"/> onto the engine's atomic-advance transaction (build invariant #1 — the JE
/// commits in the SAME SQLite transaction as the workflow advance), mirroring
/// <see cref="NodeInvoiceApprovalContext.BuildPostEffect"/> but driven by the DECLARATIVE interpreter rather
/// than the hand-written invoice handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered through the PUBLIC delegate seam (SC2 F-1).</b> The host hands this build closure to
/// <see cref="WorkflowEffectFactoryRegistration.AddWorkflowEffectFactory"/>; the internal effect-factory type
/// is never named here, and the closure is invoked ONLY by the broker (the sole <c>Build</c> caller). So this
/// posting effect is reachable ONLY through the broker's SoD-gated confirm path.
/// </para>
/// <para>
/// <b>Instance-derived, deterministic (build invariant #2).</b> The GL accounts + amount + memo come from the
/// instance's durable working state (<c>StateJson</c> — <c>{ amount, debitAccount, creditAccount, memo }</c>),
/// never from a client payload (ADR 0143 F-3). The JE id + SourceReference are derived from the effect step key
/// via <see cref="WorkflowStepKey.ToDeterministicGuid"/>, so a crash-resume re-derives the IDENTICAL JE and the
/// <c>ux_journal_entries_tenant_source_ref</c> unique index is a deterministic backstop.
/// </para>
/// </remarks>
public static class NodeLedgerPostingEffect
{
    /// <summary>The capability this effect is registered as (matches the ADR-0128 authority registry — CP).</summary>
    public const string CapabilityRef = "ledger.post-journal-entry";

    /// <summary>
    /// Builds the balanced-JE <see cref="WorkflowEffect"/> for <paramref name="request"/>. Pure construction —
    /// it stages nothing; the returned effect is enlisted by the atomic advance.
    /// </summary>
    public static WorkflowEffect Build(WorkflowEffectRequest request, IJournalPostingService posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        if (posting is not JournalPostingService verifyingPosting)
            throw new InvalidOperationException(
                "The workflow ledger effect requires the verification-capable JournalPostingService.");
        var decision = request.EffectDecision
            ?? throw new InvalidOperationException("A financial workflow effect requires its carried ledger decision.");
        var workflowDecision = request.OriginatingDecision
            ?? throw new InvalidOperationException("A financial workflow effect requires its originating workflow decision.");
        var state = ParseState(request.Instance);
        var tenant = new TenantId(request.Instance.TenantId);
        var sourceRef = SourceReferenceFor(request.Instance);
        var jeId = JournalEntryIdFor(request.Instance);
        var at = decision.Request.At;
        var entryDate = DateOnly.FromDateTime(at.UtcDateTime);
        var amount = state.Amount;

        return new WorkflowEffect(async (uow, ct) =>
        {
            var ctx = (LocalNodeDbContext)uow;
            var je = new JournalEntry(
                id: jeId,
                tenantId: tenant,
                entryDate: entryDate,
                memo: state.Memo,
                lines: new List<JournalEntryLine>
                {
                    new(new GLAccountId(state.DebitAccount), amount, 0m),
                    new(new GLAccountId(state.CreditAccount), 0m, amount),
                },
                createdAtUtc: new Instant(at),
                sourceReference: sourceRef)
            {
                Status = JournalEntryStatus.Draft,
            };
            var result = await verifyingPosting.PostAsync(
                je,
                decision,
                workflowDecision.Request.Principal,
                workflowDecision.Request.At,
                (posted, _) =>
                {
                    ctx.Set<JournalEntry>().Add(posted);
                    return Task.CompletedTask;
                },
                ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                throw new InvalidOperationException($"Workflow journal posting failed: {result.Error}.");
        });
    }

    /// <summary>The source-reference identity shared by pre-authorization and the staged effect.</summary>
    public static string SourceReferenceFor(WorkflowInstanceRecord instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return SourceReferenceFor(instance.Id, instance.Iteration, instance.CurrentStep);
    }

    /// <summary>The source-reference identity when only the durable instance coordinates are loaded.</summary>
    public static string SourceReferenceFor(string instanceId, int iteration, string currentStep) =>
        "workflow:" + new WorkflowStepKey(instanceId, iteration, currentStep).Value;

    /// <summary>The deterministic journal identity derived from <see cref="SourceReferenceFor"/>.</summary>
    public static JournalEntryId JournalEntryIdFor(WorkflowInstanceRecord instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return JournalEntryIdFor(instance.Id, instance.Iteration, instance.CurrentStep);
    }

    /// <summary>The deterministic journal identity when only the durable instance coordinates are loaded.</summary>
    public static JournalEntryId JournalEntryIdFor(string instanceId, int iteration, string currentStep)
    {
        var sourceReference = SourceReferenceFor(instanceId, iteration, currentStep);
        return new JournalEntryId("JE-" +
            WorkflowStepKey.DeriveV5Guid("journal-entry|" + sourceReference).ToString("N"));
    }

    private static PostingState ParseState(WorkflowInstanceRecord instance)
    {
        if (string.IsNullOrWhiteSpace(instance.StateJson) || instance.StateJson == "{}")
        {
            throw new InvalidOperationException(
                $"ledger.post-journal-entry effect: instance '{instance.Id}' has no working state — expected " +
                "{ amount, debitAccount, creditAccount, memo } on StateJson.");
        }

        using var doc = JsonDocument.Parse(instance.StateJson);
        var root = doc.RootElement;
        var amount = root.GetProperty("amount").GetDecimal();
        var debit = root.TryGetProperty("debitAccount", out var d) ? d.GetString()! : "1100";
        var credit = root.TryGetProperty("creditAccount", out var c) ? c.GetString()! : "4000";
        var memo = root.TryGetProperty("memo", out var m) ? m.GetString()! : "workflow posting";
        return new PostingState(amount, debit, credit, memo);
    }

    private readonly record struct PostingState(decimal Amount, string DebitAccount, string CreditAccount, string Memo);
}
