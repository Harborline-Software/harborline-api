using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The host-side <see cref="IKgActionApprovalContext"/> for Handler C (ADR 0135 KG-search Slice 2-actions — a
/// proposed CP action from a grounded GraphRAG proposal → human-CP-gate → execute). Builds the FE-1 basis from
/// the instance working state (the parked proposal + its action + the grounding-path basis + provenance + the
/// TAINT label) and the execute <see cref="WorkflowEffect"/> that runs the EXISTING CP path "as the human" on
/// approve — for the v1 <c>draft-journal-entry</c> kind, that path stages a <see cref="JournalEntryStatus.Draft"/>
/// JE onto the workflow advance's unit-of-work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drafting is the CP action — the human approves the DRAFT creation (not a post).</b> The v1 proposed
/// action is "draft this journal entry": on approve, the effect creates a <see cref="JournalEntryStatus.Draft"/>
/// JE (still requiring the normal post gate to ever hit the ledger). So the proposed-action approve is the gate
/// on the model's PROPOSAL becoming a real draft — a second, existing gate stands between a draft and a post.
/// </para>
/// <para>
/// <b>G-G4 — there is no path here that executes WITHOUT the engine's approve.</b> This context is constructed
/// by the composition and resolved ONLY by <see cref="GraphRagProposalHandler"/> at the approve step. Nothing
/// calls <see cref="BuildExecuteEffect"/> at decide time (the handler parks at decide; it builds the effect
/// only on the approve human-action). The execute effect itself does NOT trust the proposal: it re-validates
/// the action kind + re-reads the structured payload from the durable instance state (the route stamped it),
/// never an inbound argument, and refuses an unknown kind.
/// </para>
/// <para>
/// <b>Atomic with the advance (build invariant #1) — staged on the workflow context, NOT a second
/// connection.</b> The effect stages the Draft JE onto the SAME <see cref="LocalNodeDbContext"/> the engine's
/// <c>NodeEfWorkflowStore</c> hands it as the unit-of-work — so {Draft JE + workflow event + idempotency row +
/// position} co-commit in ONE SQLite transaction (mirrors <see cref="NodeRecurringGenerationContext"/> /
/// <see cref="NodeLiveInvoiceApprovalContext"/>).
/// </para>
/// <para>
/// <b>Idempotent on redelivery / crash-resume.</b> The Draft JE id + its <c>SourceReference</c>
/// (<c>kg-action:{instanceId}</c>) are derived DETERMINISTICALLY from the instance id (the bug-1337 byte-stable
/// v5 derivation), so a re-run collides on the JE unique index; the dispatcher's idempotency guard turns a
/// redelivered approve into a no-op once the advance committed. Either way: EXACTLY ONE Draft JE.
/// </para>
/// <para>
/// <b>The instance state carries the proposal basis + the structured action input.</b> <c>StateJson</c> is
/// <c>{ "proposalText", "actionKind", "actionSummary", "groundingRecordIds": [...], "groundedOnInferredEdge",
/// "taint", "action": { "debitAccount", "creditAccount", "amount", "memo" } }</c> — the cutover stamps it from
/// the parked <c>KgGenerationProposal</c> + its <c>KgProposedAction</c>.
/// </para>
/// </remarks>
public sealed class NodeKgActionApprovalContext : IKgActionApprovalContext
{
    /// <inheritdoc />
    public KgActionApprovalBasis BuildBasis(WorkflowInstanceRecord instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = ParseState(instance);
        return new KgActionApprovalBasis(
            ProposalText: state.ProposalText,
            ActionKind: state.ActionKind,
            ActionSummary: state.ActionSummary,
            GroundingRecordIds: state.GroundingRecordIds,
            GroundedOnInferredEdge: state.GroundedOnInferredEdge,
            Taint: state.Taint);
    }

    /// <inheritdoc />
    public WorkflowEffect BuildExecuteEffect(
        WorkflowInstanceRecord instance,
        WorkflowStepKey executeStepKey,
        DateTimeOffset admittedAt)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = ParseState(instance);
        var tenant = new TenantId(instance.TenantId);

        // Re-validate the proposed action kind at execute-time (defence in depth — the proposal is UNTRUSTED).
        // An unknown kind aborts the advance (NOTHING executes); the v1 host routes only draft-journal-entry.
        if (!string.Equals(state.ActionKind, "draft-journal-entry", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"kg-action-approval instance '{instance.Id}' proposes an unsupported action kind " +
                $"'{state.ActionKind}' — the v1 host executes only 'draft-journal-entry'. Nothing was executed.");
        }

        if (state.Action is not { } je)
        {
            throw new InvalidOperationException(
                $"kg-action-approval instance '{instance.Id}' is a draft-journal-entry action but carries no " +
                "structured action payload — nothing was executed.");
        }

        // The Draft JE id is derived deterministically from the instance id (the bug-1337 byte-stable v5
        // technique) so a crash-resume re-derives the IDENTICAL JE and collides on the unique source-ref index.
        var jeId = new JournalEntryId("JE-" + DeriveActionJeGuid(instance.Id).ToString("N"));
        var sourceRef = "kg-action:" + instance.Id;

        var entryDate = DateOnly.FromDateTime(admittedAt.UtcDateTime);

        return new WorkflowEffect((uow, _) =>
        {
            var ctx = (LocalNodeDbContext)uow;
            var entry = new JournalEntry(
                id: jeId,
                tenantId: tenant,
                entryDate: entryDate,
                memo: je.Memo,
                lines: new List<JournalEntryLine>
                {
                    new(new GLAccountId(je.DebitAccount), je.Amount, 0m),
                    new(new GLAccountId(je.CreditAccount), 0m, je.Amount),
                },
                createdAtUtc: new Instant(admittedAt),
                sourceReference: sourceRef)
            {
                // The CP action is "DRAFT this JE" — the effect creates a Draft (the normal post gate still
                // stands between this draft and the ledger). The human approved the model's PROPOSAL becoming
                // a draft, not a post.
                Status = JournalEntryStatus.Draft,
            };
            ctx.Set<JournalEntry>().Add(entry);
            return Task.CompletedTask;
        });
    }

    private static KgActionState ParseState(WorkflowInstanceRecord instance)
    {
        if (string.IsNullOrWhiteSpace(instance.StateJson) || instance.StateJson == "{}")
        {
            throw new InvalidOperationException(
                $"kg-action-approval instance '{instance.Id}' has no working state — expected the proposal " +
                "basis + the structured action on StateJson.");
        }

        using var doc = JsonDocument.Parse(instance.StateJson);
        var root = doc.RootElement;

        var proposalText = root.TryGetProperty("proposalText", out var pt) && pt.ValueKind == JsonValueKind.String
            ? pt.GetString()! : string.Empty;
        var actionKind = root.TryGetProperty("actionKind", out var ak) && ak.ValueKind == JsonValueKind.String
            ? ak.GetString()! : string.Empty;
        var actionSummary = root.TryGetProperty("actionSummary", out var asum) && asum.ValueKind == JsonValueKind.String
            ? asum.GetString()! : string.Empty;
        var taint = root.TryGetProperty("taint", out var tt) && tt.ValueKind == JsonValueKind.String
            ? tt.GetString()! : "untrusted-derived";
        var inferred = root.TryGetProperty("groundedOnInferredEdge", out var ie)
            && (ie.ValueKind == JsonValueKind.True);

        var ids = new List<string>();
        if (root.TryGetProperty("groundingRecordIds", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in idArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    ids.Add(el.GetString()!);
                }
            }
        }

        KgActionJe? action = null;
        if (root.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.Object)
        {
            var debit = actEl.TryGetProperty("debitAccount", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()! : "1100";
            var credit = actEl.TryGetProperty("creditAccount", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()! : "4000";
            var amount = actEl.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetDecimal() : 0m;
            var memo = actEl.TryGetProperty("memo", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : actionSummary;
            action = new KgActionJe(debit, credit, amount, memo);
        }

        return new KgActionState(proposalText, actionKind, actionSummary, ids, inferred, taint, action);
    }

    /// <summary>
    /// Deterministic, byte-stable v5 UUID over <c>kg-action|{instanceId}</c> for the Draft JE id (so a
    /// crash-resume re-derives the identical JE). Same big-endian assembly as the bug-1337 derivation in
    /// <see cref="NodeRecurringGenerationContext.DeriveOccurrenceGuid"/>.
    /// </summary>
    internal static Guid DeriveActionJeGuid(string instanceId)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"kg-action|{instanceId}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));

        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC-4122 variant

        Span<byte> ms = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(ms[..4], BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]));
        BinaryPrimitives.WriteUInt16LittleEndian(ms.Slice(4, 2), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(ms.Slice(6, 2), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2)));
        bytes.Slice(8, 8).CopyTo(ms.Slice(8, 8));

        return new Guid(ms);
    }

    private readonly record struct KgActionState(
        string ProposalText,
        string ActionKind,
        string ActionSummary,
        IReadOnlyList<string> GroundingRecordIds,
        bool GroundedOnInferredEdge,
        string Taint,
        KgActionJe? Action);

    private readonly record struct KgActionJe(string DebitAccount, string CreditAccount, decimal Amount, string Memo);
}
