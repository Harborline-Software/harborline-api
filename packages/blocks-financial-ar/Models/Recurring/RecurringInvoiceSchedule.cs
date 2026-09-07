using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialAr.Models;

/// <summary>
/// Defines a repeating billing rule that generates canonical AR
/// <see cref="Invoice"/> records on each occurrence of its RRULE.
/// Additive composition with <c>blocks-recurring-billing</c>: this
/// entity is the generic-core home for RRULE-driven AR invoice
/// generation; the PM rent-collection module composes over it by
/// supplying the resolved <see cref="CustomerId"/> from the
/// lease→party lookup and calling the core batch generator.
///
/// <para>
/// <b>Generation invariant:</b> every occurrence produces a canonical
/// <c>Invoice</c> via <c>IInvoicePostingService.IssueAsync</c> (Draft
/// → Issued, mints number, posts balanced JE, emits InvoiceIssued).
/// Direct <c>Invoice</c> construction with <c>Status=Issued</c> is
/// forbidden — it would bypass numbering, JE posting, tax, and the
/// audit envelope.
/// </para>
///
/// <para>
/// <b>Idempotency:</b> generation is keyed on
/// <c>(TenantId, ScheduleId, occurrenceDate)</c>. Running the
/// generator twice over the same window returns the already-generated
/// invoices; no duplicates are created. The <c>GeneratedInvoices</c>
/// dictionary is the durable dedup store.
/// </para>
///
/// <para>
/// <b>Tenant isolation:</b> every schedule and every generated invoice
/// is <see cref="TenantId"/>-scoped. Cross-tenant generation is not
/// supported.
/// </para>
///
/// <para>
/// <b>Recurrence period vs. accounting period:</b> the occurrence
/// dates produced by the RRULE are the invoice issue/due dates.
/// Whether an issue date falls in an open accounting period is the
/// <c>IInvoicePostingService</c>'s concern, not this entity's.
/// </para>
/// </summary>
public sealed class RecurringInvoiceSchedule : IMustHaveTenant
{
    /// <summary>Stable identifier.</summary>
    public RecurringInvoiceScheduleId Id { get; private set; }

    /// <summary>Tenant scope.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>The chart-of-accounts under which generated invoices post.</summary>
    public ChartOfAccountsId ChartId { get; private set; }

    /// <summary>
    /// The Party holding the customer role. Callers (e.g. the PM pack)
    /// are responsible for resolving the lease→PartyId before creating
    /// the schedule; the core stores the resolved party and remains
    /// lease-agnostic (ADR 0111 D3-compliant).
    /// </summary>
    public PartyId CustomerId { get; private set; }

    /// <summary>AR control account.</summary>
    public GLAccountId ArAccountId { get; private set; }

    /// <summary>
    /// RFC 5545 RRULE string using the fleet bounded subset. See
    /// <c>IRruleExpansionService</c> doc for the supported components.
    /// </summary>
    public string RecurrenceRule { get; private set; }

    /// <summary>Recurrence anchor — the schedule's effective start date.</summary>
    public DateOnly StartsOn { get; private set; }

    /// <summary>Optional hard end date. <see langword="null"/> = open-ended.</summary>
    public DateOnly? EndsOn { get; private set; }

    /// <summary>
    /// IANA timezone id for occurrence date computation
    /// (e.g. <c>America/Los_Angeles</c>).
    /// </summary>
    public string Timezone { get; private set; }

    /// <summary>
    /// Line items to stamp on each generated invoice. Immutable at the
    /// schedule level — changing these requires creating a new schedule
    /// version (or a service-layer mutation, audited).
    /// </summary>
    public IReadOnlyList<RecurringInvoiceLineTemplate> LineTemplates { get; private set; }

    /// <summary>Lifecycle state.</summary>
    public RecurringScheduleStatus Status { get; private set; }

    /// <summary>
    /// Lookahead window in days used by the bulk generator when no explicit
    /// <c>asOf</c> window is provided. Default 90 matches the work-orders pattern.
    /// </summary>
    public int LookaheadHorizonDays { get; private set; }

    /// <summary>
    /// Lead days: how many days ahead of <c>today</c> to start generating.
    /// 0 means generate any occurrence on or after today; positive means
    /// generate only occurrences at least N days in the future (issue the
    /// invoice before its due date).
    /// </summary>
    public int GenerateLeadDays { get; private set; }

    /// <summary>
    /// Durable idempotency map: <c>(ScheduleId, occurrenceDate) → InvoiceId</c>.
    /// The TenantId is implicit (this entity is tenant-scoped). A second
    /// generation run for the same occurrence returns the mapped InvoiceId
    /// rather than creating a new invoice.
    ///
    /// <para>
    /// Advisory: a uniqueness constraint on <c>(TenantId, ScheduleId, occurrenceDate)</c>
    /// at the repository layer is the fail-closed backstop for concurrent
    /// rent-run races.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<DateOnly, InvoiceId> GeneratedInvoices { get; private set; }
        = new Dictionary<DateOnly, InvoiceId>();

    /// <summary>When the last generation run stamped the schedule cursor.</summary>
    public DateTimeOffset? LastGeneratedAtUtc { get; private set; }

    // Private parameterless constructor for EF-Core / deserializer materialization.
    // Non-nullable properties are initialized by the ORM; the null-forgiving
    // assignment silences CS8618 without making the public API nullable.
#pragma warning disable CS8618
    private RecurringInvoiceSchedule() { }
#pragma warning restore CS8618

    /// <summary>
    /// Construct a new <see cref="RecurringInvoiceSchedule"/> in
    /// <see cref="RecurringScheduleStatus.Active"/> state.
    /// </summary>
    public static RecurringInvoiceSchedule Create(
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
        RecurringInvoiceScheduleId? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurrenceRule);
        ArgumentException.ThrowIfNullOrWhiteSpace(timezone);
        ArgumentNullException.ThrowIfNull(lineTemplates);
        if (lineTemplates.Count == 0)
            throw new ArgumentException("A recurring schedule must have at least one line template.", nameof(lineTemplates));

        return new RecurringInvoiceSchedule
        {
            Id = id ?? RecurringInvoiceScheduleId.NewId(),
            TenantId = tenantId,
            ChartId = chartId,
            CustomerId = customerId,
            ArAccountId = arAccountId,
            RecurrenceRule = recurrenceRule,
            StartsOn = startsOn,
            EndsOn = endsOn,
            Timezone = timezone,
            LineTemplates = lineTemplates,
            Status = RecurringScheduleStatus.Active,
            LookaheadHorizonDays = lookaheadHorizonDays,
            GenerateLeadDays = generateLeadDays,
        };
    }

    /// <summary>Pause — stop generating invoices until resumed.</summary>
    public void Pause()
    {
        if (Status == RecurringScheduleStatus.Archived)
            throw new InvalidOperationException("Cannot pause an archived schedule.");
        Status = RecurringScheduleStatus.Paused;
    }

    /// <summary>Resume from paused.</summary>
    public void Resume()
    {
        if (Status != RecurringScheduleStatus.Paused)
            throw new InvalidOperationException("Resume is only valid from Paused state.");
        Status = RecurringScheduleStatus.Active;
    }

    /// <summary>Archive — terminal lifecycle transition.</summary>
    public void Archive()
    {
        if (Status == RecurringScheduleStatus.Archived)
            return; // idempotent
        Status = RecurringScheduleStatus.Archived;
    }

    /// <summary>
    /// Record a generated invoice for an occurrence date (idempotency store).
    /// Called by the recurring-invoice service (both in-memory and EF-backed
    /// implementations) after each successful <c>IssueAsync</c>. Public so
    /// EF-backed persistence adapters in sibling assemblies can update the
    /// idempotency map without requiring <c>InternalsVisibleTo</c>.
    /// </summary>
    public void RecordGeneratedInvoice(
        DateOnly occurrenceDate,
        InvoiceId invoiceId,
        DateTimeOffset generatedAtUtc)
    {
        var mutable = new Dictionary<DateOnly, InvoiceId>(GeneratedInvoices)
        {
            [occurrenceDate] = invoiceId
        };
        GeneratedInvoices = mutable;
        LastGeneratedAtUtc = generatedAtUtc;
    }
}
