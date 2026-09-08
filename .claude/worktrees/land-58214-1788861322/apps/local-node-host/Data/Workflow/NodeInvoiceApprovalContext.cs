using System.Globalization;
using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The host-side <see cref="IInvoiceApprovalContext"/> for Handler A (ADR 0135 slice 2 — the
/// <c>invoice &gt; $5k → approve → post</c> process). Bridges the financial-free
/// <see cref="InvoiceApprovalHandler"/> in <c>blocks-workflow</c> to the financial cluster: it reads the
/// invoice amount from the instance's working state, renders the FE-1 posting preview, and builds the post
/// <see cref="WorkflowEffect"/> that stages a real balanced <see cref="JournalEntry"/> onto the engine's
/// atomic-advance transaction (build invariant #1 — the JE commits in the SAME SQLite transaction as the
/// workflow advance).
/// </summary>
/// <remarks>
/// <para>
/// <b>Amount + the GL accounts live in the instance working state.</b> The instance's <c>StateJson</c> carries
/// <c>{ "amount": &lt;decimal&gt;, "debitAccount": "1100", "creditAccount": "4000", "memo": "..." }</c>. The
/// approval process is instantiated (by the event/route that creates it) with this state and the pinned
/// decision-table version (D7) on <see cref="WorkflowInstanceRecord.DefinitionVersion"/>.
/// </para>
/// <para>
/// <b>Deterministic JE id (build invariant #2).</b> The post effect derives the JE id + SourceReference from
/// the post step key via <see cref="WorkflowStepKey.ToDeterministicGuid"/>, so a crash-resume re-derives the
/// IDENTICAL JE — the <c>ux_journal_entries_tenant_source_ref</c> unique index is a deterministic backstop
/// independent of the atomic-commit guarantee. Mirrors the slice-1 engine test's <c>JournalPostEffect</c>,
/// promoted to a production composition.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The only persistence sink is the JE staged onto the recoverable <see cref="LocalNodeDbContext"/>
/// (the same store the node posting path writes). No kernel CRDT writer / per-team event log is touched.
/// </para>
/// </remarks>
public sealed class NodeInvoiceApprovalContext : IInvoiceApprovalContext
{
    /// <inheritdoc />
    public decimal GetInvoiceAmount(WorkflowInstanceRecord instance)
    {
        var state = ParseState(instance);
        return state.Amount;
    }

    /// <inheritdoc />
    public DateTimeOffset GetBusinessTime(WorkflowInstanceRecord instance) => instance.CreatedAt;

    /// <inheritdoc />
    public string RenderPostingPreview(WorkflowInstanceRecord instance, decimal amount)
    {
        var state = ParseState(instance);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Debit {state.DebitAccount} {amount}; Credit {state.CreditAccount} {amount} ({state.Memo}).");
    }

    /// <inheritdoc />
    public WorkflowEffect BuildPostEffect(
        WorkflowInstanceRecord instance,
        WorkflowStepKey postStepKey,
        DateTimeOffset at,
        AuthorizationDecision? admittedDecision = null)
    {
        var state = ParseState(instance);
        var tenant = new TenantId(instance.TenantId);
        var jeId = new JournalEntryId("JE-" + postStepKey.ToDeterministicGuid("journal-entry").ToString("N"));
        var sourceRef = "workflow:" + postStepKey.Value;       // deterministic — identical across a resume.
        var entryDate = DateOnly.FromDateTime(at.UtcDateTime);
        var amount = state.Amount;

        return new WorkflowEffect((uow, _) =>
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
                Status = JournalEntryStatus.Posted,
                PostedAtUtc = new Instant(at),
            };
            ctx.Set<JournalEntry>().Add(je);
            return Task.CompletedTask;
        });
    }

    private static InvoiceApprovalState ParseState(WorkflowInstanceRecord instance)
    {
        if (string.IsNullOrWhiteSpace(instance.StateJson) || instance.StateJson == "{}")
        {
            throw new InvalidOperationException(
                $"Invoice-approval instance '{instance.Id}' has no working state — expected " +
                "{ amount, debitAccount, creditAccount, memo } on StateJson.");
        }

        using var doc = JsonDocument.Parse(instance.StateJson);
        var root = doc.RootElement;
        var amount = root.GetProperty("amount").GetDecimal();
        var debit = root.TryGetProperty("debitAccount", out var d) ? d.GetString()! : "1100";
        var credit = root.TryGetProperty("creditAccount", out var c) ? c.GetString()! : "4000";
        var memo = root.TryGetProperty("memo", out var m) ? m.GetString()! : "invoice approval post";
        return new InvoiceApprovalState(amount, debit, credit, memo);
    }

    private readonly record struct InvoiceApprovalState(decimal Amount, string DebitAccount, string CreditAccount, string Memo);
}
