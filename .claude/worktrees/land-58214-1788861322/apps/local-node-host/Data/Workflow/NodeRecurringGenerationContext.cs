using System.Buffers.Binary;
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
/// The host-side <see cref="IRecurringGenerationContext"/> for Handler B (ADR 0135 slice 2 — recurring
/// generation on the <c>schedule</c> trigger). Builds the occurrence's generation
/// <see cref="WorkflowEffect"/> that stages a balanced <see cref="JournalEntry"/> onto the engine's atomic
/// advance, deriving the JE id DETERMINISTICALLY from <c>(scheduleId, occurrenceDate)</c> — reusing the
/// merged bug-1337 byte-stable derivation so a crash-resume re-derives the IDENTICAL JE (no double-post).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuses the bug-1337 derivation shape (build invariant #2).</b> The occurrence JE id is a deterministic
/// RFC-4122 v5 UUID over <c>recurring-generation|{scheduleId}|{occurrenceDate}</c>, assembled big-endian so a
/// re-derive on another CPU architecture is byte-identical (the same technique as
/// <c>NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId</c> + <c>WorkflowStepKey.DeriveV5Guid</c>). Its
/// <c>SourceReference</c> is <c>recurring:{scheduleId}:{occurrenceDate}</c> — identical across runs, so the
/// JE unique index is a deterministic backstop independent of the atomic-commit guarantee.
/// </para>
/// <para>
/// <b>The instance state carries the schedule.</b> <c>StateJson</c> is
/// <c>{ "scheduleId": "...", "amount": &lt;decimal&gt;, "debitAccount": "1100", "creditAccount": "4000" }</c>.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The only persistence sink is the JE staged onto the recoverable <see cref="LocalNodeDbContext"/>.
/// </para>
/// </remarks>
public sealed class NodeRecurringGenerationContext : IRecurringGenerationContext
{

    /// <inheritdoc />
    public WorkflowEffect? BuildGenerationEffect(
        WorkflowInstanceRecord instance,
        DateOnly occurrenceDate,
        WorkflowStepKey generateStepKey,
        DateTimeOffset at)
    {
        var state = ParseState(instance);
        var tenant = new TenantId(instance.TenantId);

        // bug-1337-shape deterministic occurrence id — IDENTICAL across a first run and a crash-resume.
        var jeId = new JournalEntryId("JE-" + DeriveOccurrenceGuid(state.ScheduleId, occurrenceDate).ToString("N"));
        var sourceRef = string.Create(
            CultureInfo.InvariantCulture,
            $"recurring:{state.ScheduleId}:{occurrenceDate:yyyy-MM-dd}");
        var amount = state.Amount;

        return new WorkflowEffect((uow, _) =>
        {
            var ctx = (LocalNodeDbContext)uow;
            var je = new JournalEntry(
                id: jeId,
                tenantId: tenant,
                entryDate: occurrenceDate,
                memo: $"recurring occurrence {occurrenceDate:yyyy-MM-dd} (schedule {state.ScheduleId})",
                lines: new List<JournalEntryLine>
                {
                    new(new GLAccountId(state.DebitAccount), amount, 0m),
                    new(new GLAccountId(state.CreditAccount), 0m, amount),
                },
                createdAtUtc: new Instant(at),
                sourceReference: sourceRef)
            {
                Status = JournalEntryStatus.Posted,
            };
            ctx.Set<JournalEntry>().Add(je);
            return Task.CompletedTask;
        });
    }

    private static RecurringState ParseState(WorkflowInstanceRecord instance)
    {
        if (string.IsNullOrWhiteSpace(instance.StateJson) || instance.StateJson == "{}")
        {
            throw new InvalidOperationException(
                $"Recurring-generation instance '{instance.Id}' has no working state — expected " +
                "{ scheduleId, amount, debitAccount, creditAccount } on StateJson.");
        }

        using var doc = JsonDocument.Parse(instance.StateJson);
        var root = doc.RootElement;
        var scheduleId = root.GetProperty("scheduleId").GetString()!;
        var amount = root.GetProperty("amount").GetDecimal();
        var debit = root.TryGetProperty("debitAccount", out var d) ? d.GetString()! : "1100";
        var credit = root.TryGetProperty("creditAccount", out var c) ? c.GetString()! : "4000";
        return new RecurringState(scheduleId, amount, debit, credit);
    }

    /// <summary>
    /// The bug-1337 deterministic occurrence-id derivation, byte-stable across CPU architectures. Mirrors
    /// <c>NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId</c> + <c>WorkflowStepKey.DeriveV5Guid</c> —
    /// kept inline so this composition takes no cross-package dependency for a 16-byte conversion.
    /// </summary>
    internal static Guid DeriveOccurrenceGuid(string scheduleId, DateOnly occurrenceDate)
    {
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"recurring-generation|{scheduleId}|{occurrenceDate:yyyy-MM-dd}");
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

    private readonly record struct RecurringState(string ScheduleId, decimal Amount, string DebitAccount, string CreditAccount);
}
