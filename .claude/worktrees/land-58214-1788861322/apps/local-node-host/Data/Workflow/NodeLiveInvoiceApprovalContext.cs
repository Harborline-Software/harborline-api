using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The <b>live</b> host-side <see cref="IInvoiceApprovalContext"/> for the invoice-issue cutover (the
/// vertical-flow backend over ADR 0135 Handler A). Unlike <see cref="NodeInvoiceApprovalContext"/> — which
/// stages a self-contained single-line JE for the engine's own slice tests — THIS context's post effect
/// stages the REAL invoice's issue (the balanced Debit-AR / Credit-income JE + the Draft → Issued status)
/// directly onto the workflow advance's unit-of-work, so the over-threshold post produces the same GL +
/// status outcome the direct-post route produces — only deferred to approve-time.
/// </summary>
/// <remarks>
/// <para>
/// <b>No double-post BY CONSTRUCTION (the #1348 deep-review property).</b> The invoice-issue route routes the
/// <c>&gt; $5k</c> case to the engine and <em>never</em> posts the JE inline at issue time — the invoice stays
/// <see cref="InvoiceStatus.Draft"/>. The ONLY code path that posts an over-threshold invoice's JE is this
/// effect, reached ONLY via the approve human-action. There are not two issuers racing; there is exactly one,
/// gated behind the human approval.
/// </para>
/// <para>
/// <b>Atomic with the advance (build invariant #1) — staged on the workflow context, NOT a second
/// connection.</b> The effect stages the JE + the Draft → Issued invoice update onto the SAME
/// <see cref="LocalNodeDbContext"/> the engine's <see cref="NodeEfWorkflowStore"/> hands it as the
/// unit-of-work — so {JE + Issued invoice + workflow event + idempotency row + position} co-commit in ONE
/// SQLite transaction. (Routing the post through <see cref="Harborline.Api.Blocks.FinancialAr.Services.IInvoicePostingService"/>
/// would open a SECOND SQLite connection inside the workflow's write transaction and deadlock on SQLite's
/// single-writer lock — so the JE is staged inline, mirroring <see cref="NodeInvoiceApprovalContext"/> and
/// <see cref="NodeRecurringGenerationContext"/>.)
/// </para>
/// <para>
/// <b>Idempotent on redelivery / crash-resume.</b> Three layers: (1) the JE id + its <c>SourceReference</c>
/// (<c>invoice:{id}</c>, identical to the direct path) are derived deterministically, so a re-run collides on
/// the <c>ux_journal_entries_tenant_source_ref</c> unique index; (2) the effect re-reads the invoice and
/// no-ops if it is already <see cref="InvoiceStatus.Issued"/> (the same early-return the direct path has);
/// (3) the dispatcher's idempotency guard turns a redelivered approve into a no-op once the advance committed
/// (the instance is Completed → <see cref="WorkflowDispatchResult.Terminal"/>). A crash before the advance
/// commit leaves NOTHING (the whole transaction rolls back) and the resume re-runs cleanly. Either way:
/// EXACTLY ONE JE, EXACTLY ONE Issued invoice.
/// </para>
/// <para>
/// <b>The JE shape matches the direct path.</b> The node uses the cluster-default no-tax calculator, so the
/// issue JE is Debit AR <c>Total</c> / Credit each line's income <c>Amount</c> — byte-identical to
/// <see cref="Harborline.Api.Blocks.FinancialAr.Services.InvoicePostingService.IssueAsync"/>'s no-tax case. A Draft
/// carrying a non-zero per-line tax is REFUSED by the effect (it would need the tax-bridge's per-jurisdiction
/// split the inline path does not implement) — a defensive assert that never fires on the node today.
/// </para>
/// <para>
/// <b>The instance working state carries the issue inputs.</b> <c>StateJson</c> is
/// <c>{ "invoiceId": "...", "amount": &lt;decimal&gt;, "debitAccount": "...", "creditAccount": "...",
/// "memo": "..." }</c> — the route stamps the invoice's true total as <c>amount</c> (the threshold-rule +
/// FE-1-preview value) and the AR / first-income account for the preview text. The post effect re-resolves
/// the live invoice by id, so the JE is built from the invoice's REAL lines, not the preview.
/// </para>
/// </remarks>
public sealed class NodeLiveInvoiceApprovalContext : IInvoiceApprovalContext
{
    private readonly INodeAuditWriteEnlister? _auditEnlister;
    private readonly INodeCallerAttributionSource? _attributionSource;

    /// <summary>Construct without optional audit attribution collaborators.</summary>
    public NodeLiveInvoiceApprovalContext()
        : this(auditEnlister: null)
    {
    }

    /// <summary>
    /// Construct with the optional atomic-audit enlister (MTW-2 2612-C), and the same acting
    /// caller source used by the audit seam. The approve effect is awaited inside the approval request,
    /// so a selected-session caller remains available while the effect stages the issued invoice. A
    /// genuinely detached execution has no attribution and retains the ruled operator fallback.
    /// </summary>
    internal NodeLiveInvoiceApprovalContext(
        INodeAuditWriteEnlister? auditEnlister,
        INodeCallerAttributionSource? attributionSource = null)
    {
        _auditEnlister = auditEnlister;
        _attributionSource = attributionSource;
    }

    /// <inheritdoc />
    public decimal GetInvoiceAmount(WorkflowInstanceRecord instance) => ParseState(instance).Amount;

    /// <inheritdoc />
    public DateTimeOffset GetBusinessTime(WorkflowInstanceRecord instance) => instance.CreatedAt;

    /// <inheritdoc />
    public string RenderPostingPreview(WorkflowInstanceRecord instance, decimal amount)
    {
        var state = ParseState(instance);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Issue invoice {state.InvoiceId}: Debit {state.DebitAccount} {amount}; " +
            $"Credit {state.CreditAccount} {amount} ({state.Memo}).");
    }

    /// <inheritdoc />
    public WorkflowEffect BuildPostEffect(
        WorkflowInstanceRecord instance,
        WorkflowStepKey postStepKey,
        DateTimeOffset at,
        AuthorizationDecision? admittedDecision = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = ParseState(instance);
        var tenant = new TenantId(instance.TenantId);
        var invoiceId = new InvoiceId(state.InvoiceId);

        // The effect stages the issue onto the workflow advance's unit-of-work (ONE SQLite connection, ONE
        // transaction — build invariant #1). The approve human-action is the ONLY caller for an over-threshold
        // invoice, so this is the single issuer (no-double-post by construction).
        return new WorkflowEffect(async (uow, ct) =>
        {
            var ctx = (LocalNodeDbContext)uow;

            // Re-read the live invoice on THIS context (the same connection the advance holds). AsNoTracking
            // so the subsequent Update(issued) with the `with`-derived record is not rejected by an EF
            // identity conflict against an already-tracked original.
            var invoice = await ctx.Set<Invoice>()
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.TenantId == tenant && i.Id == invoiceId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Approve-time issue: invoice '{state.InvoiceId}' not found for tenant '{tenant.Value}'. " +
                    "The approval Process stays parked; no JE was posted.");

            // Idempotent: an already-Issued invoice is the redelivery / crash-resume no-op (mirrors the direct
            // path's early return). Stage nothing; the advance records its idempotency row + position only.
            if (invoice.Status == InvoiceStatus.Issued && invoice.JournalEntryId is not null)
            {
                return;
            }

            if (invoice.Status != InvoiceStatus.Draft)
            {
                throw new InvalidOperationException(
                    $"Approve-time issue: invoice '{state.InvoiceId}' is in status '{invoice.Status}', not Draft. " +
                    "The approval Process stays parked; no JE was posted.");
            }

            if (invoice.Lines.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Approve-time issue: invoice '{state.InvoiceId}' has no lines; nothing to post.");
            }

            // The node uses the cluster-default no-tax calculator — refuse a tax-bearing Draft (the inline
            // path does not implement the per-jurisdiction tax split). Defensive; never fires on the node.
            if (invoice.Lines.Any(l => l.TaxAmount != 0m) || invoice.TaxTotal != 0m)
            {
                throw new InvalidOperationException(
                    $"Approve-time issue: invoice '{state.InvoiceId}' carries non-zero tax, which the inline " +
                    "approval post does not support. Route tax-bearing invoices through the direct posting path.");
            }

            var total = invoice.Total;

            // The issue JE: Debit AR (total), Credit each line's income account (line.Amount). Byte-identical
            // to the direct path's no-tax JE. Deterministic JE id + the SAME SourceReference (invoice:{id}) so
            // a re-run is caught by ux_journal_entries_tenant_source_ref — the durable backstop.
            var jeLines = new List<JournalEntryLine>(invoice.Lines.Count + 1)
            {
                new(invoice.ArAccountId, debit: total, credit: 0m),
            };
            foreach (var line in invoice.Lines)
            {
                jeLines.Add(new JournalEntryLine(line.IncomeAccountId, debit: 0m, credit: line.Amount));
            }

            var sourceRef = "invoice:" + invoice.Id.Value;
            var jeId = JournalEntryIdFor(postStepKey);
            var now = new Instant(at);
            var je = new JournalEntry(
                id: jeId,
                tenantId: tenant,
                entryDate: invoice.IssueDate,
                memo: $"Invoice {invoice.InvoiceNumber}",
                lines: jeLines,
                createdAtUtc: now,
                sourceReference: sourceRef)
            {
                ChartId = invoice.ChartId,
                SourceKind = JournalEntrySource.Invoice,
                Status = JournalEntryStatus.Posted,
                PostedAtUtc = now,
            };
            ctx.Set<JournalEntry>().Add(je);

            // The posted JE is a synchronous reaction of the admitted approval act. Carry that exact
            // decision into the atomic journal-audit enlister; never reconstruct authority from caller
            // attribution. Direct-construction workflow tests may omit the audit seam, but a composed
            // audit seam fails closed if its dispatcher did not carry the decision.
            if (_auditEnlister is not null)
            {
                var decision = admittedDecision
                    ?? throw new InvalidOperationException(
                        "The approve-time journal audit requires the carried workflow admission decision.");
                await _auditEnlister.EnlistJournalPostedAsync(ctx, je, decision, ct).ConfigureAwait(false);
            }

            // MTW-2 2612-C — stage the node-signed attribution envelope for THIS approve-time JE onto the
            // SAME workflow unit-of-work (one SQLite connection, one transaction — no second write, mirrors
            // NodeEfJournalStore.StageAndSaveAsync). Because this detached effect has no bound request
            // principal, the envelope carries the operator-fallback attribution (ruled option (a)); the
            // audit row co-commits with the JE + Issued invoice + workflow event. No-op when the enlister
            // is not wired (direct-construction slice/E2E tests) — behaviour byte-unchanged there.
            // Draft → Issued, stamping the JE id — staged on the SAME context so it co-commits with the JE +
            // the advance (mirrors the F3 IssuedInvoiceWriteScope atomicity, inline here).
            var actingParty = NodeCallerParty.Resolve(_attributionSource?.TryResolveCurrent());
            var issued = invoice with
            {
                Status = InvoiceStatus.Issued,
                JournalEntryId = jeId,
                Balance = total,
                UpdatedAtUtc = now,
                UpdatedBy = actingParty,
                Version = invoice.Version + 1,
            };
            ctx.Set<Invoice>().Update(issued);
        });
    }

    /// <summary>The deterministic posted-JE identity authorized at the approval boundary.</summary>
    internal static JournalEntryId JournalEntryIdFor(WorkflowStepKey postStepKey) =>
        new("JE-" + postStepKey.ToDeterministicGuid("journal-entry").ToString("N"));

    private static LiveApprovalState ParseState(WorkflowInstanceRecord instance)
    {
        if (string.IsNullOrWhiteSpace(instance.StateJson) || instance.StateJson == "{}")
        {
            throw new InvalidOperationException(
                $"Live invoice-approval instance '{instance.Id}' has no working state — expected " +
                "{ invoiceId, amount, debitAccount, creditAccount, memo } on StateJson.");
        }

        using var doc = JsonDocument.Parse(instance.StateJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("invoiceId", out var inv) || inv.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(inv.GetString()))
        {
            throw new InvalidOperationException(
                $"Live invoice-approval instance '{instance.Id}' is missing the required string 'invoiceId' on StateJson.");
        }

        var invoiceId = inv.GetString()!;
        var amount = root.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetDecimal()
            : 0m;
        var debit = root.TryGetProperty("debitAccount", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()! : "1100";
        var credit = root.TryGetProperty("creditAccount", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()! : "4000";
        var memo = root.TryGetProperty("memo", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : "invoice approval issue";
        return new LiveApprovalState(invoiceId, amount, debit, credit, memo);
    }

    private readonly record struct LiveApprovalState(
        string InvoiceId, decimal Amount, string DebitAccount, string CreditAccount, string Memo);
}
