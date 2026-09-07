using System.Collections.Concurrent;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Scheduling;

namespace Harborline.Api.Blocks.FinancialAr.Services;

/// <summary>
/// In-memory implementation of <see cref="IRecurringInvoiceService"/>.
///
/// <para>
/// Idempotency is keyed on <c>(TenantId, ScheduleId, occurrenceDate)</c>.
/// The schedule's <see cref="RecurringInvoiceSchedule.GeneratedInvoices"/>
/// dictionary is the durable dedup store. A uniqueness constraint at the
/// repository layer is the fail-closed backstop for concurrent races; in
/// this in-memory implementation the <c>GeneratedInvoices</c> map held
/// on the schedule object is authoritative.
/// </para>
///
/// <para>
/// Generation always calls <see cref="IInvoicePostingService.IssueAsync"/>
/// (Draft → Issued path). Direct construction of an <c>Invoice</c> with
/// <c>Status=Issued</c> is forbidden here — it would bypass invoice
/// numbering, GL posting, tax computation, and the audit event.
/// </para>
/// </summary>
internal sealed class InMemoryRecurringInvoiceService : IRecurringInvoiceService
{
    private readonly IRruleExpansionService _rrule;
    private readonly IInvoicePostingService _posting;
    private readonly IInvoiceRepository _invoices;

    private readonly ConcurrentDictionary<RecurringInvoiceScheduleId, RecurringInvoiceSchedule>
        _schedules = new();

    public InMemoryRecurringInvoiceService(
        IRruleExpansionService rrule,
        IInvoicePostingService posting,
        IInvoiceRepository invoices)
    {
        _rrule = rrule;
        _posting = posting;
        _invoices = invoices;
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<RecurringInvoiceSchedule>> ListSchedulesAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        var result = _schedules.Values
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.StartsOn)
            .ToList();
        return Task.FromResult<IReadOnlyList<RecurringInvoiceSchedule>>(result);
    }

    public Task<RecurringInvoiceSchedule?> GetScheduleAsync(
        TenantId tenantId,
        RecurringInvoiceScheduleId id,
        CancellationToken cancellationToken = default)
    {
        _schedules.TryGetValue(id, out var schedule);
        // Opaque null on cross-tenant (uniform-404 per ADR 0092 §A3).
        if (schedule is not null && schedule.TenantId != tenantId)
            return Task.FromResult<RecurringInvoiceSchedule?>(null);
        return Task.FromResult(schedule);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public Task<RecurringInvoiceSchedule> CreateScheduleAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId customerId,
        GLAccountId arAccountId,
        string recurrenceRule,
        DateOnly startsOn,
        string timezone,
        IReadOnlyList<RecurringInvoiceLineTemplate> lineTemplates,
        DateOnly? endsOn = null,
        int lookaheadHorizonDays = 90,
        int generateLeadDays = 0,
        CancellationToken cancellationToken = default)
    {
        var schedule = RecurringInvoiceSchedule.Create(
            tenantId, chartId, customerId, arAccountId,
            recurrenceRule, startsOn, timezone, lineTemplates,
            endsOn, lookaheadHorizonDays, generateLeadDays);

        _schedules[schedule.Id] = schedule;
        return Task.FromResult(schedule);
    }

    public Task PauseScheduleAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var schedule = GetScheduleOrThrow(scheduleId);
        schedule.Pause();
        return Task.CompletedTask;
    }

    public Task ResumeScheduleAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var schedule = GetScheduleOrThrow(scheduleId);
        schedule.Resume();
        return Task.CompletedTask;
    }

    public Task ArchiveScheduleAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var schedule = GetScheduleOrThrow(scheduleId);
        schedule.Archive();
        return Task.CompletedTask;
    }

    // ── Generation ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ScheduleGenerationResult> GenerateDueInvoicesAsync(
        RecurringInvoiceScheduleId scheduleId,
        DateOnly asOf,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        var schedule = GetScheduleOrThrow(scheduleId);
        if (authority.Tenant != schedule.TenantId)
            throw new ArgumentException("The recurring-invoice tenant does not match the boundary authority.", nameof(authority));
        var actor = new PartyId(authority.Principal.Value);

        if (schedule.Status == RecurringScheduleStatus.Paused)
            return new ScheduleGenerationResult(scheduleId, ScheduleGenerationOutcome.Paused, []);

        if (schedule.Status == RecurringScheduleStatus.Archived)
            return new ScheduleGenerationResult(scheduleId, ScheduleGenerationOutcome.Archived, []);

        // Delegate window computation and lead-day filtering to
        // IRruleExpansionService — it accepts lookaheadDays + leadDays
        // so we don't re-implement window arithmetic here.
        // EndsOn is passed as the hard upper bound when set; otherwise
        // ExpandOccurrences uses (asOf + lookaheadDays) internally.
        var dueOccurrences = _rrule.ExpandOccurrences(
            rrule: schedule.RecurrenceRule,
            start: schedule.StartsOn,
            end: schedule.EndsOn,
            lookaheadDays: schedule.LookaheadHorizonDays,
            leadDays: schedule.GenerateLeadDays,
            today: asOf,
            timezone: schedule.Timezone);

        var entries = new List<InvoiceGenerationEntry>(dueOccurrences.Count);

        foreach (var occurrenceDate in dueOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Idempotency check: (ScheduleId, occurrenceDate) — TenantId is
            // implicit on the schedule.
            if (schedule.GeneratedInvoices.TryGetValue(occurrenceDate, out var existingId))
            {
                entries.Add(new InvoiceGenerationEntry(occurrenceDate, existingId, ScheduleGenerationOutcome.AlreadyGenerated));
                continue;
            }

            // Build a Draft Invoice from the line templates, then
            // call IssueAsync to run the canonical AR posting path
            // (Draft → Issued, mint number, post balanced JE, emit event).
            var draftId = InvoiceId.NewId();
            var lines = BuildLines(draftId, schedule.LineTemplates);
            var draft = Invoice.Create(
                tenantId: schedule.TenantId,
                chartId: schedule.ChartId,
                // Placeholder number — IInvoicePostingService.IssueAsync
                // (or InvoiceNumberingService called by it) replaces this
                // with the canonical minted number.
                invoiceNumber: string.Empty,
                customerId: schedule.CustomerId,
                issueDate: occurrenceDate,
                dueDate: occurrenceDate,
                lines: lines,
                arAccountId: schedule.ArAccountId,
                createdAtUtc: new Instant(authority.At),
                createdBy: actor,
                id: draftId);

            await _invoices.UpsertAsync(schedule.TenantId, draft, authority.At, cancellationToken);

            var issueResult = await _posting.IssueAsync(draftId, authority, cancellationToken);

            if (!issueResult.IsSuccess)
            {
                entries.Add(new InvoiceGenerationEntry(
                    occurrenceDate,
                    draftId,
                    ScheduleGenerationOutcome.Failed));
                // Return early on first failure with the partial result set.
                return new ScheduleGenerationResult(
                    scheduleId,
                    ScheduleGenerationOutcome.Failed,
                    entries,
                    $"IssueAsync failed for occurrence {occurrenceDate:yyyy-MM-dd}: {issueResult.Error} — {issueResult.Detail}");
            }

            var issuedId = issueResult.Invoice!.Id;
            schedule.RecordGeneratedInvoice(occurrenceDate, issuedId, authority.At);
            entries.Add(new InvoiceGenerationEntry(occurrenceDate, issuedId, ScheduleGenerationOutcome.Generated));
        }

        return new ScheduleGenerationResult(scheduleId, ScheduleGenerationOutcome.Generated, entries);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduleGenerationResult>> GenerateAllDueAsync(
        TenantId tenantId,
        DateOnly asOf,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        if (authority.Tenant != tenantId)
            throw new ArgumentException("The recurring-invoice tenant does not match the boundary authority.", nameof(authority));
        var tenantSchedules = _schedules.Values
            .Where(s => s.TenantId == tenantId && s.Status == RecurringScheduleStatus.Active)
            .ToList();

        var results = new List<ScheduleGenerationResult>(tenantSchedules.Count);

        foreach (var schedule in tenantSchedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await GenerateDueInvoicesAsync(schedule.Id, asOf, authority, cancellationToken);
            results.Add(result);
        }

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private RecurringInvoiceSchedule GetScheduleOrThrow(RecurringInvoiceScheduleId scheduleId)
    {
        if (!_schedules.TryGetValue(scheduleId, out var schedule))
            throw new InvalidOperationException($"RecurringInvoiceSchedule '{scheduleId}' not found.");
        return schedule;
    }

    private static IReadOnlyList<InvoiceLine> BuildLines(
        InvoiceId invoiceId,
        IReadOnlyList<RecurringInvoiceLineTemplate> templates)
    {
        var lines = new List<InvoiceLine>(templates.Count);
        for (var i = 0; i < templates.Count; i++)
        {
            var t = templates[i];
            lines.Add(InvoiceLine.Create(
                invoiceId: invoiceId,
                lineNumber: i + 1,
                description: t.Description,
                quantity: t.Quantity,
                unitPrice: t.UnitPrice,
                incomeAccountId: t.IncomeAccountId,
                taxCodeId: t.TaxCodeId,
                propertyId: t.PropertyId));
        }
        return lines;
    }
}
