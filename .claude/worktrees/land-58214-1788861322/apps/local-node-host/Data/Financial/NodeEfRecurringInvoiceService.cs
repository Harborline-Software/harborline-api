using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Node-resident <see cref="IRecurringInvoiceService"/> over the recoverable, Store-DEK-enveloped
/// <see cref="LocalNodeDbContext"/> (ADR 0113 ABSOLUTE local-first; T2b recurring-invoice node-flip).
///
/// <para>
/// <b>The generation ENGINE is the Harborline block, not bridge-local.</b> RRULE expansion comes from
/// <see cref="IRruleExpansionService"/> (<c>foundation-scheduling</c>); the draft → issue → balanced-JE
/// flow goes through the node-resident <see cref="IInvoicePostingService"/> (the Step-2b
/// <see cref="InvoicePostingService"/> wired by <see cref="NodeInvoiceWriteComposition.AddNodeInvoiceWrites"/>,
/// which posts over the recoverable <see cref="NodeEfJournalStore"/>); drafts are upserted via the
/// node-resident <see cref="IInvoiceRepository"/> (<see cref="NodeEfInvoiceRepository"/>). This service is
/// a near-verbatim port of the Bridge <c>EfRecurringInvoiceService</c> with the SOLE difference being the
/// backing store (<see cref="LocalNodeDbContext"/> vs the Bridge Postgres context) — the schedule
/// persistence, the RRULE engine, the posting engine, and the domain-model idempotency are identical.
/// </para>
///
/// <para>
/// <b>Idempotency (ADR 0122 SourceReference posture, satisfied at the schedule-occurrence level).</b>
/// Re-running generation never double-issues: the engine checks
/// <see cref="RecurringInvoiceSchedule.GeneratedInvoices"/> (occurrenceDate → InvoiceId) BEFORE creating
/// a draft and short-circuits to <see cref="ScheduleGenerationOutcome.AlreadyGenerated"/> for occurrences
/// already present. The persisted <c>generated_invoices_json</c> column IS the dedupe state (recoverable
/// local-node.db), never a seed-keyed KV.
/// </para>
///
/// <para>
/// <b>Crash-resume integrity (bug-1337 / ADR 0135 SC1 — NO double-post).</b> Two reinforcing measures
/// close the crash-between-effect-and-idempotency double-post window the prior two-transaction shape had:
/// <list type="number">
///   <item><b>Stable occurrence key.</b> The draft invoice id is derived DETERMINISTICALLY from
///     <c>(scheduleId, occurrenceDate)</c> (<see cref="DeriveOccurrenceInvoiceId"/>), NOT a freshly-minted
///     <c>InvoiceId.NewId()</c>. So the issue JE's <c>SourceReference</c> (<c>invoice:{id}</c>) is identical
///     across a first run and a post-crash resume, and the
///     <c>ux_journal_entries_tenant_source_ref</c> unique index is a deterministic backstop that rejects a
///     re-post even if the idempotency map somehow missed.</item>
///   <item><b>Atomic co-commit.</b> The idempotency record (the <c>GeneratedInvoices</c> map update) is
///     committed IN THE SAME SQLite transaction as the issue JE, via
///     <see cref="INodeRecurringInvoiceWriteEnlister"/> riding the existing
///     <see cref="NodeEfJournalStore"/> single-save chokepoint (the same shared-unit-of-work mechanism the
///     audit append uses, ADR 0126 §D2). A committed JE can never lack its idempotency record, so a resume
///     short-circuits on the persisted map. No separate loop-end persist of the idempotency map remains.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Tenant isolation (ADR 0092).</b> Reads + writes are scoped to the active-team-derived
/// <see cref="TenantId"/> the caller supplies (<c>NodeTenant.Resolve(activeTeam)</c> /
/// <c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), not a fixed <c>"local"</c> sentinel;
/// list/detail use <c>IgnoreQueryFilters()</c> + an explicit TenantId WHERE as the per-org isolation
/// predicate, mirroring the Bridge service and the other node EF repositories.
/// </para>
///
/// <para>
/// <b>SC4-C2 recoverability.</b> Every persistence sink is the recoverable <c>local-node.db</c> — schedule
/// rows via this context, drafts via <see cref="NodeEfInvoiceRepository"/>, the issue JEs (transitively,
/// through the node posting service) via <see cref="NodeEfJournalStore"/>. No kernel CRDT writer / per-team
/// event log is reachable, so the SC4-T9(b) gate stays green.
/// </para>
/// </summary>
public sealed class NodeEfRecurringInvoiceService : IRecurringInvoiceService
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly IRruleExpansionService _rrule;
    private readonly IInvoicePostingService _posting;
    private readonly IInvoiceRepository _invoices;

    /// <summary>Construct with required node-resident dependencies.</summary>
    public NodeEfRecurringInvoiceService(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule,
        IInvoicePostingService posting,
        IInvoiceRepository invoices)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _rrule          = rrule          ?? throw new ArgumentNullException(nameof(rrule));
        _posting        = posting        ?? throw new ArgumentNullException(nameof(posting));
        _invoices       = invoices       ?? throw new ArgumentNullException(nameof(invoices));
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecurringInvoiceSchedule>> ListSchedulesAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.StartsOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RecurringInvoiceSchedule?> GetScheduleAsync(
        TenantId tenantId,
        RecurringInvoiceScheduleId id,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<RecurringInvoiceSchedule> CreateScheduleAsync(
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

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        ctx.Set<RecurringInvoiceSchedule>().Add(schedule);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return schedule;
    }

    /// <inheritdoc />
    public async Task PauseScheduleAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var schedule = await LoadForMutationAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        schedule.Pause();
        await PersistScheduleAsync(schedule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResumeScheduleAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var schedule = await LoadForMutationAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        schedule.Resume();
        await PersistScheduleAsync(schedule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ArchiveScheduleAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var schedule = await LoadForMutationAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        schedule.Archive();
        await PersistScheduleAsync(schedule, cancellationToken).ConfigureAwait(false);
    }

    // ── Generation ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ScheduleGenerationResult> GenerateDueInvoicesAsync(
        RecurringInvoiceScheduleId scheduleId,
        DateOnly asOf,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        var schedule = await LoadForMutationAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        if (authority.Tenant != schedule.TenantId)
            throw new ArgumentException("The recurring-invoice tenant does not match the boundary authority.", nameof(authority));
        var actor = new PartyId(authority.Principal.Value);

        if (schedule.Status == RecurringScheduleStatus.Paused)
            return new ScheduleGenerationResult(scheduleId, ScheduleGenerationOutcome.Paused, []);
        if (schedule.Status == RecurringScheduleStatus.Archived)
            return new ScheduleGenerationResult(scheduleId, ScheduleGenerationOutcome.Archived, []);

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

            // Idempotency: an occurrence already generated short-circuits BEFORE any new draft is
            // created, so a re-run never double-issues. The persisted GeneratedInvoices map IS the
            // dedupe state (recoverable local-node.db). After a crash mid-loop, the resumed run sees
            // the SAME map because each occurrence's idempotency record committed atomically with its
            // issue JE (bug-1337) — there is no orphaned-JE / missing-record window any more.
            if (schedule.GeneratedInvoices.TryGetValue(occurrenceDate, out var existingId))
            {
                entries.Add(new InvoiceGenerationEntry(
                    occurrenceDate, existingId, ScheduleGenerationOutcome.AlreadyGenerated));
                continue;
            }

            // bug-1337 fix (a): the draft id (and therefore the issue JE's SourceReference,
            // invoice:{id}) is DETERMINISTIC over (scheduleId, occurrenceDate), so a post-crash resume
            // produces the SAME SourceReference and the JE unique index is a deterministic backstop.
            var draftId = DeriveOccurrenceInvoiceId(scheduleId, occurrenceDate);
            var lines = BuildLines(draftId, schedule.LineTemplates);
            var draft = Invoice.Create(
                tenantId: schedule.TenantId,
                chartId: schedule.ChartId,
                invoiceNumber: string.Empty,
                customerId: schedule.CustomerId,
                issueDate: occurrenceDate,
                dueDate: occurrenceDate,
                lines: lines,
                arAccountId: schedule.ArAccountId,
                createdAtUtc: new Instant(authority.At),
                createdBy: actor,
                id: draftId);

            await _invoices.UpsertAsync(schedule.TenantId, draft, authority.At, cancellationToken)
                .ConfigureAwait(false);

            // bug-1337 fix (b): open an ambient write-scope carrying the pending idempotency record
            // BEFORE IssueAsync. IssueAsync posts the balanced JE through the node posting service over
            // NodeEfJournalStore; the recurring enlister, invoked inside that store's single
            // SaveChangesAsync, stages the schedule's GeneratedInvoices update onto the SAME context so
            // the issue JE and the idempotency record commit in ONE SQLite transaction. A crash before
            // the commit rolls back BOTH (no orphan JE); a crash after it leaves a durable record the
            // resume short-circuits on.
            var pending = new PendingRecurringIdempotencyRecord(
                ScheduleId: scheduleId,
                TenantId: schedule.TenantId,
                OccurrenceDate: occurrenceDate,
                InvoiceId: draftId,
                SourceReference: $"invoice:{draftId.Value}",
                ActAt: authority.At);

            IssueResult issueResult;
            using (RecurringInvoiceWriteScope.Enter(pending))
            {
                issueResult = await _posting.IssueAsync(draftId, authority, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!issueResult.IsSuccess)
            {
                entries.Add(new InvoiceGenerationEntry(
                    occurrenceDate, draftId, ScheduleGenerationOutcome.Failed));
                // No loop-end persist is needed: every prior occurrence's idempotency record was already
                // committed atomically with its JE. Return the partial result as-is.
                return new ScheduleGenerationResult(
                    scheduleId, ScheduleGenerationOutcome.Failed, entries,
                    $"IssueAsync failed for occurrence {occurrenceDate:yyyy-MM-dd}: {issueResult.Error} — {issueResult.Detail}");
            }

            var issuedId = issueResult.Invoice!.Id;

            // Keep the IN-MEMORY loop copy in sync so subsequent occurrences this run see the updated
            // dedupe map. This is NOT re-persisted (the durable write already happened atomically inside
            // IssueAsync via the enlister) — re-persisting here would re-introduce the second transaction
            // the bug-1337 fix removes.
            schedule.RecordGeneratedInvoice(occurrenceDate, issuedId, authority.At);
            entries.Add(new InvoiceGenerationEntry(
                occurrenceDate, issuedId, ScheduleGenerationOutcome.Generated));
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
        var schedules = await ListSchedulesAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var active = schedules.Where(s => s.Status == RecurringScheduleStatus.Active).ToList();

        var results = new List<ScheduleGenerationResult>(active.Count);
        foreach (var s in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await GenerateDueInvoicesAsync(s.Id, asOf, authority, cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<RecurringInvoiceSchedule> LoadForMutationAsync(
        RecurringInvoiceScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var schedule = await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken)
            .ConfigureAwait(false);
        if (schedule is null)
            throw new InvalidOperationException($"RecurringInvoiceSchedule '{scheduleId}' not found.");
        return schedule;
    }

    private async Task PersistScheduleAsync(
        RecurringInvoiceSchedule schedule,
        CancellationToken cancellationToken)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        ctx.Set<RecurringInvoiceSchedule>().Attach(schedule);
        ctx.Entry(schedule).State = EntityState.Modified;
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Derives a STABLE invoice id for an occurrence deterministically from
    /// <c>(scheduleId, occurrenceDate)</c> (bug-1337 fix (a)). The same occurrence on a first run and on a
    /// post-crash resume yields the SAME id, so the issue JE's <c>SourceReference</c>
    /// (<c>invoice:{id}</c>) is identical across runs and the <c>ux_journal_entries_tenant_source_ref</c>
    /// unique index is a deterministic backstop against a re-post — independent of the atomic-commit fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is a deterministic RFC-4122 <b>version-5</b> (name-based, SHA-256) UUID, so it round-trips
    /// losslessly through the <see cref="InvoiceId"/> string column exactly like a
    /// <c>Guid.NewGuid().ToString()</c> value — the only structural difference is that it is reproducible.
    /// </para>
    /// <para>
    /// <b>Cross-architecture byte-stability (bug-1337 deep-review F1).</b> The version/variant nibbles are
    /// stamped on the <b>big-endian</b> RFC-4122 byte positions and the 16 bytes are assembled into the
    /// <see cref="Guid"/> with an explicit big-endian read (<see cref="System.Buffers.Binary.BinaryPrimitives"/>,
    /// NOT the host-endianness <c>BitConverter</c> + mixed-endian <c>new Guid(int, short, short, …)</c> ctor).
    /// That makes the rendered string identical on a little-endian and a big-endian host. This matters because
    /// the derived id is the recurring-invoice idempotency key: in the planned multi-machine tenant an offline
    /// client and the home node must derive the SAME <c>SourceReference</c> for the same occurrence, on
    /// possibly-different CPU architectures (Mac ARM64 + Windows x64) — otherwise dedup breaks and bug-1337
    /// re-opens. The big-endian assembly mirrors the fleet's vetted
    /// <c>Harborline.Api.Blocks.CrewComms.Protocol.RFC4122GuidFormatter.ReadBigEndian</c> (same technique; kept
    /// inline here to avoid coupling the financial node host to the comms block for a 16-byte conversion).
    /// </para>
    /// </remarks>
    internal static InvoiceId DeriveOccurrenceInvoiceId(
        RecurringInvoiceScheduleId scheduleId,
        DateOnly occurrenceDate)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"recurring-invoice-occurrence|{scheduleId.Value}|{occurrenceDate:yyyy-MM-dd}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        // Take the first 16 hash bytes as the RFC-4122 big-endian layout and stamp the version (5) + variant
        // nibbles in place — these positions are byte-order-independent.
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC-4122 variant

        // Assemble the Guid from the big-endian layout with explicit fixed byte order (deterministic across
        // CPU architectures). Guid's in-memory layout is mixed-endian (Data1/2/3 little-endian, Data4
        // big-endian), so reverse the first three groups when reading the big-endian source — exactly the
        // RFC4122GuidFormatter.ReadBigEndian conversion.
        Span<byte> ms = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(ms[..4], BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]));
        BinaryPrimitives.WriteUInt16LittleEndian(ms.Slice(4, 2), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(ms.Slice(6, 2), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2)));
        bytes.Slice(8, 8).CopyTo(ms.Slice(8, 8));

        return new InvoiceId(new Guid(ms).ToString());
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
                invoiceId:       invoiceId,
                lineNumber:      i + 1,
                description:     t.Description,
                quantity:        t.Quantity,
                unitPrice:       t.UnitPrice,
                incomeAccountId: t.IncomeAccountId,
                taxCodeId:       t.TaxCodeId,
                propertyId:      t.PropertyId));
        }
        return lines;
    }
}
