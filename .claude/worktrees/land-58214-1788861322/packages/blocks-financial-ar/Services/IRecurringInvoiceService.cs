using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialAr.Services;

/// <summary>
/// Write + generation surface for <see cref="RecurringInvoiceSchedule"/>.
///
/// <para>
/// Generation uses <c>IRruleExpansionService</c> to expand the schedule's
/// RRULE into occurrence dates, then for each occurrence produces a
/// canonical AR <see cref="Invoice"/> via
/// <see cref="IInvoicePostingService.IssueAsync"/> (Draft → Issued, mints
/// number via <c>IInvoiceNumberingService</c>, posts balanced JE, emits
/// <c>Financial.InvoiceIssued</c>). Idempotent per
/// <c>(TenantId, ScheduleId, occurrenceDate)</c>.
/// </para>
///
/// <para>
/// <b>PM rent-run composition (ADR 0111 D3):</b>
/// <list type="bullet">
///   <item><description>PM pack selects the tenant's active recurring schedules (filtered to lease-linked ones if needed).</description></item>
///   <item><description>PM pack calls <see cref="GenerateAllDueAsync"/> (or the per-schedule overload).</description></item>
///   <item><description>PM pack runs payment-matching as a separate follow-on step.</description></item>
///   <item><description>The pack adds NO generation logic of its own — it composes over this service.</description></item>
/// </list>
/// </para>
/// </summary>
public interface IRecurringInvoiceService
{
    /// <summary>
    /// List all <see cref="RecurringInvoiceSchedule"/> records for a tenant,
    /// ordered by <c>StartsOn</c> ascending. Returns an empty list when none exist.
    /// </summary>
    Task<IReadOnlyList<RecurringInvoiceSchedule>> ListSchedulesAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single <see cref="RecurringInvoiceSchedule"/> by id. Returns
    /// <see langword="null"/> when not found or when the id belongs to a different
    /// tenant (uniform-404 per ADR 0092 §A3).
    /// </summary>
    Task<RecurringInvoiceSchedule?> GetScheduleAsync(
        TenantId tenantId,
        RecurringInvoiceScheduleId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new <see cref="RecurringInvoiceSchedule"/> in Active state.
    /// </summary>
    Task<RecurringInvoiceSchedule> CreateScheduleAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>Pause the schedule — generation skips it until resumed.</summary>
    Task PauseScheduleAsync(RecurringInvoiceScheduleId scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Resume from paused.</summary>
    Task ResumeScheduleAsync(RecurringInvoiceScheduleId scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Archive — terminal.</summary>
    Task ArchiveScheduleAsync(RecurringInvoiceScheduleId scheduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate invoices for all occurrence dates that fall within the
    /// RRULE expansion window anchored at <paramref name="asOf"/>. Idempotent
    /// per <c>(TenantId, ScheduleId, occurrenceDate)</c>.
    ///
    /// <para>
    /// Paused or archived schedules return a result with
    /// <see cref="ScheduleGenerationOutcome.Paused"/> or
    /// <see cref="ScheduleGenerationOutcome.Archived"/> and no invoices.
    /// </para>
    /// </summary>
    /// <param name="scheduleId">The schedule to generate against.</param>
    /// <param name="asOf">Wall-clock reference date (today).</param>
    /// <param name="authority">Boundary-authenticated tenant, principal, and instant for the run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Per-schedule generation outcome.</returns>
    Task<ScheduleGenerationResult> GenerateDueInvoicesAsync(
        RecurringInvoiceScheduleId scheduleId,
        DateOnly asOf,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch entry point for the PM rent-run: generate invoices for ALL
    /// active schedules belonging to <paramref name="tenantId"/> whose
    /// next occurrence falls on or before <paramref name="asOf"/>.
    /// Returns one <see cref="ScheduleGenerationResult"/> per schedule.
    ///
    /// <para>
    /// Re-runnable: a second call for the same <paramref name="asOf"/> window
    /// returns the already-generated invoices (idempotency preserved). The
    /// PM pack calls this and receives a per-schedule summary for operator
    /// visibility; it adds no generation logic of its own (ADR 0111 D3).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ScheduleGenerationResult>> GenerateAllDueAsync(
        TenantId tenantId,
        DateOnly asOf,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}

// ── Result types ────────────────────────────────────────────────────────────

/// <summary>Per-occurrence outcome returned by the bulk generator.</summary>
public enum ScheduleGenerationOutcome
{
    /// <summary>Invoice newly generated and posted (Draft → Issued).</summary>
    Generated,
    /// <summary>Invoice already existed for this occurrence — idempotency hit.</summary>
    AlreadyGenerated,
    /// <summary>Schedule is paused — no invoice generated.</summary>
    Paused,
    /// <summary>Schedule is archived — no invoice generated.</summary>
    Archived,
    /// <summary>Generation failed for this occurrence (post-failure details in <see cref="ScheduleGenerationResult.Error"/>).</summary>
    Failed,
}

/// <summary>
/// Per-schedule result of a <see cref="IRecurringInvoiceService.GenerateDueInvoicesAsync"/> call.
/// Surfaced to the PM rent-run so the UI can display a generation summary.
/// </summary>
public sealed record ScheduleGenerationResult(
    RecurringInvoiceScheduleId ScheduleId,
    ScheduleGenerationOutcome Outcome,
    IReadOnlyList<InvoiceGenerationEntry> Invoices,
    string? Error = null)
{
    /// <summary>
    /// Count of newly generated invoices in this run (does not include
    /// idempotency hits from prior runs).
    /// </summary>
    public int NewlyGeneratedCount =>
        Invoices.Count(e => e.Outcome == ScheduleGenerationOutcome.Generated);

    /// <summary>
    /// Count of invoices already present from a prior run (idempotency hits).
    /// </summary>
    public int AlreadyPresentCount =>
        Invoices.Count(e => e.Outcome == ScheduleGenerationOutcome.AlreadyGenerated);
}

/// <summary>Per-occurrence entry within a <see cref="ScheduleGenerationResult"/>.</summary>
public sealed record InvoiceGenerationEntry(
    DateOnly OccurrenceDate,
    InvoiceId InvoiceId,
    ScheduleGenerationOutcome Outcome);
