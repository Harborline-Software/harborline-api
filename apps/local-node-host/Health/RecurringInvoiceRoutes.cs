using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local recurring-invoice surface — the T2b recurring-invoice node-flip (ADR 0113 ABSOLUTE
/// local-first; drives the RecurringInvoices / RentRun pages offline). Schedules + on-demand generation
/// run fully on the embedded node so a single-device install (signal-bridge STOPPED) can manage rent-run
/// schedules and generate the lease's open AR invoice without the Bridge.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns recurring-invoice data + generation.</b> Schedule persistence is the recoverable
/// <see cref="LocalNodeDbContext"/> (RecurringInvoiceSchedule is mapped by the shared <c>ArEntityModule</c>
/// — the <c>recurring_invoice_schedules</c> table already exists in the node InitialSchema migration, so
/// this flip adds NO new schema). Generation uses the Harborline block engine: RRULE expansion +
/// draft → issue → balanced JE through the node-resident <see cref="IInvoicePostingService"/> over the
/// recoverable <see cref="NodeEfJournalStore"/>. So a generated invoice appears in the node AR + JE read
/// surfaces. ADDITIVE — the Bridge recurring path is untouched; the frontend flip + Rust <c>sc5</c>
/// fail-close are a sequenced follow-up after the security SPOT-CHECK.
/// <list type="bullet">
///   <item><c>GET  /api/local-node/recurring-invoices</c> — list schedules (StartsOn asc).</item>
///   <item><c>GET  /api/local-node/recurring-invoices/{id}</c> — full schedule detail; opaque 404 if absent.</item>
///   <item><c>POST /api/local-node/recurring-invoices</c> — create an Active schedule. 201 Created.</item>
///   <item><c>POST /api/local-node/recurring-invoices/{id}/pause|resume|archive</c> — lifecycle. 204.</item>
///   <item><c>POST /api/local-node/recurring-invoices/{id}/generate</c> — generate due invoices.
///     Idempotent: a re-run for the same asOf window returns already-generated invoices without
///     duplicating (the schedule's GeneratedInvoices map is the dedupe state).</item>
/// </list>
/// </para>
/// <para>
/// <b>Wire shape == the Bridge contract.</b> These DTOs are field-identical to the Bridge
/// <c>RecurringInvoicesEndpoints</c> DTOs (camelCase from ASP.NET Core: <c>scheduleId</c>, <c>customerId</c>,
/// …). The frontend client therefore repoints IN PLACE (the node base URL replaces <c>/api/v1</c>) with no
/// field remapping — the existing frontend types match verbatim.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), not a
/// fixed <c>"local"</c> sentinel; the generation actor is the session-resolved caller party
/// (<see cref="NodeCallerParty"/>), falling back to the single operator when no principal is bound.
/// The chart is resolved server-side from the create body's <c>chartId</c> (caller passes the node-resident
/// chart). The explicit <c>WHERE TenantId</c> is the per-org isolation predicate.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): they bind to the loopback-only Kestrel listener (the same posture
/// as every other <c>/api/local-node/*</c> route; the Bridge CSRF + AuthenticatedTenantPolicy posture does
/// not apply on the loopback node).
/// </para>
/// <para>
/// <b>Wiring.</b> The service accessor is injected from the OUTER host container and passed to
/// <see cref="Map"/> as a closed-over dependency — NOT resolved via <c>[FromServices]</c>, which would fail
/// on the inner shared-app container (bug-2849).
/// </para>
/// </remarks>
public static class RecurringInvoiceRoutes
{
    /// <summary>Canonical route base for the node-local recurring-invoices surface.</summary>
    public const string RouteBase = "/api/local-node/recurring-invoices";

    /// <summary>
    /// Maps the recurring-invoice routes onto <paramref name="app"/>, closing over the
    /// <paramref name="recurring"/> service accessor from the outer host container.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IRecurringInvoiceService recurring,
        IActiveTeamAccessor activeTeam,
        TimeProvider? timeProvider = null)
    {
        System.ArgumentNullException.ThrowIfNull(app);
        System.ArgumentNullException.ThrowIfNull(recurring);

        MapList(app, recurring, activeTeam);
        MapDetail(app, recurring, activeTeam);
        MapCreate(app, recurring, activeTeam);
        MapLifecycle(app, recurring, "pause", activeTeam);
        MapLifecycle(app, recurring, "resume", activeTeam);
        MapLifecycle(app, recurring, "archive", activeTeam);
        MapGenerate(app, recurring, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
    }

    // ── GET /api/local-node/recurring-invoices ────────────────────────────────────
    private static void MapList(IEndpointRouteBuilder app, IRecurringInvoiceService recurring, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var schedules = await recurring.ListSchedulesAsync(LocalTenantId, ct).ConfigureAwait(false);
            return Results.Ok(new RecurringScheduleListResponse(
                Schedules: schedules.Select(ToSummary).ToArray()));
        });
    }

    // ── GET /api/local-node/recurring-invoices/{id} ───────────────────────────────
    private static void MapDetail(IEndpointRouteBuilder app, IRecurringInvoiceService recurring, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{id}}", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var schedule = await recurring
                .GetScheduleAsync(LocalTenantId, new RecurringInvoiceScheduleId(id), ct)
                .ConfigureAwait(false);
            return schedule is null
                ? Results.NotFound()
                : Results.Ok(ToDetail(schedule));
        });
    }

    // ── POST /api/local-node/recurring-invoices ───────────────────────────────────
    private static void MapCreate(IEndpointRouteBuilder app, IRecurringInvoiceService recurring, IActiveTeamAccessor activeTeam)
    {
        app.MapPost(RouteBase, async (CreateRecurringScheduleBody body, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (body is null
                || string.IsNullOrWhiteSpace(body.CustomerId)
                || string.IsNullOrWhiteSpace(body.ArAccountId)
                || string.IsNullOrWhiteSpace(body.ChartId)
                || string.IsNullOrWhiteSpace(body.RecurrenceRule)
                || string.IsNullOrWhiteSpace(body.Timezone)
                || body.Lines is null || body.Lines.Count == 0)
            {
                return Results.BadRequest(new { error = "validation_failed",
                    detail = "ChartId, CustomerId, ArAccountId, RecurrenceRule, Timezone, and at least one line are required." });
            }

            foreach (var line in body.Lines)
            {
                if (line.UnitPrice < 0m)
                {
                    return Results.BadRequest(new { error = "line_unit_price_negative",
                        detail = $"Line '{line.Description}' has a negative UnitPrice ({line.UnitPrice})." });
                }
            }

            var lineTemplates = body.Lines.Select(l => new RecurringInvoiceLineTemplate(
                Description:     l.Description,
                Quantity:        l.Quantity,
                UnitPrice:       l.UnitPrice,
                IncomeAccountId: new GLAccountId(l.IncomeAccountId),
                TaxCodeId:       l.TaxCodeId,
                PropertyId:      l.PropertyId)).ToList();

            var schedule = await recurring.CreateScheduleAsync(
                tenantId:             LocalTenantId,
                chartId:              new ChartOfAccountsId(body.ChartId),
                customerId:           new PartyId(body.CustomerId),
                arAccountId:          new GLAccountId(body.ArAccountId),
                recurrenceRule:       body.RecurrenceRule,
                startsOn:             body.StartsOn,
                timezone:             body.Timezone,
                lineTemplates:        lineTemplates,
                endsOn:               body.EndsOn,
                lookaheadHorizonDays: body.LookaheadHorizonDays ?? 90,
                generateLeadDays:     body.GenerateLeadDays ?? 0,
                cancellationToken:    ct).ConfigureAwait(false);

            return Results.Created($"{RouteBase}/{schedule.Id.Value}", ToDetail(schedule));
        });
    }

    // ── POST /api/local-node/recurring-invoices/{id}/{pause|resume|archive} ────────
    private static void MapLifecycle(
        IEndpointRouteBuilder app, IRecurringInvoiceService recurring, string action, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{id}}/{action}", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var scheduleId = new RecurringInvoiceScheduleId(id);
            var schedule = await recurring.GetScheduleAsync(LocalTenantId, scheduleId, ct).ConfigureAwait(false);
            if (schedule is null) return Results.NotFound();

            switch (action)
            {
                case "pause":   await recurring.PauseScheduleAsync(scheduleId, ct).ConfigureAwait(false); break;
                case "resume":  await recurring.ResumeScheduleAsync(scheduleId, ct).ConfigureAwait(false); break;
                case "archive": await recurring.ArchiveScheduleAsync(scheduleId, ct).ConfigureAwait(false); break;
            }
            return Results.NoContent();
        });
    }

    // ── POST /api/local-node/recurring-invoices/{id}/generate ──────────────────────
    private static void MapGenerate(
        IEndpointRouteBuilder app,
        IRecurringInvoiceService recurring,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/generate", async (string id, GenerateScheduleBody? body, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var scheduleId = new RecurringInvoiceScheduleId(id);
            var schedule = await recurring.GetScheduleAsync(LocalTenantId, scheduleId, ct).ConfigureAwait(false);
            if (schedule is null) return Results.NotFound();

            var caller = NodeCallerParty.Resolve(http);
            var authority = new AuthorizationWriteContext(
                new ActorId(caller.Value), LocalTenantId, timeProvider.GetUtcNow());
            var asOf = body?.AsOf ?? DateOnly.FromDateTime(authority.At.UtcDateTime);
            var result = await recurring
                .GenerateDueInvoicesAsync(scheduleId, asOf, authority, ct)
                .ConfigureAwait(false);

            return Results.Ok(ToGenerationResult(result));
        });
    }

    // ── Request bodies ─────────────────────────────────────────────────────────────

    /// <summary>Create body — carries ChartId explicitly (the node has no entity-header resolution).</summary>
    public sealed record CreateRecurringScheduleBody(
        string ChartId,
        string CustomerId,
        string ArAccountId,
        string RecurrenceRule,
        string Timezone,
        DateOnly StartsOn,
        IReadOnlyList<CreateRecurringLineBody> Lines,
        DateOnly? EndsOn = null,
        int? LookaheadHorizonDays = null,
        int? GenerateLeadDays = null);

    /// <summary>Line template within <see cref="CreateRecurringScheduleBody"/>.</summary>
    public sealed record CreateRecurringLineBody(
        string Description,
        decimal Quantity,
        decimal UnitPrice,
        string IncomeAccountId,
        string? TaxCodeId = null,
        string? PropertyId = null);

    /// <summary>Optional body for the generate route.</summary>
    public sealed record GenerateScheduleBody(DateOnly? AsOf = null);

    // ── Response DTOs (field-identical to the Bridge contract) ───────────────────────

    /// <summary>List envelope (mirrors the Bridge <c>{ schedules: [...] }</c>).</summary>
    public sealed record RecurringScheduleListResponse(RecurringScheduleSummaryWire[] Schedules);

    /// <summary>Summary row (camelCase JSON: scheduleId, customerId, …).</summary>
    public sealed record RecurringScheduleSummaryWire(
        string ScheduleId,
        string CustomerId,
        string ArAccountId,
        string RecurrenceRule,
        string StartsOn,
        string? EndsOn,
        string Status,
        int GeneratedInvoiceCount,
        string? LastGeneratedAtUtc);

    /// <summary>Full schedule detail (camelCase JSON).</summary>
    public sealed record RecurringScheduleDetailWire(
        string ScheduleId,
        string TenantId,
        string ChartId,
        string CustomerId,
        string ArAccountId,
        string RecurrenceRule,
        string Timezone,
        string StartsOn,
        string? EndsOn,
        string Status,
        int LookaheadHorizonDays,
        int GenerateLeadDays,
        RecurringLineTemplateWire[] Lines,
        int GeneratedInvoiceCount,
        string? LastGeneratedAtUtc);

    /// <summary>Line template DTO.</summary>
    public sealed record RecurringLineTemplateWire(
        string Description,
        decimal Quantity,
        decimal UnitPrice,
        string IncomeAccountId,
        string? TaxCodeId,
        string? PropertyId);

    /// <summary>Generation-run result.</summary>
    public sealed record ScheduleGenerationResultWire(
        string ScheduleId,
        string Outcome,
        int NewlyGeneratedCount,
        int AlreadyPresentCount,
        string? Error,
        InvoiceGenerationEntryWire[] Invoices);

    /// <summary>Per-occurrence row.</summary>
    public sealed record InvoiceGenerationEntryWire(
        string OccurrenceDate,
        string InvoiceId,
        string Outcome);

    // ── Mapping helpers ──────────────────────────────────────────────────────────────

    private static RecurringScheduleSummaryWire ToSummary(RecurringInvoiceSchedule s) => new(
        ScheduleId:            s.Id.Value,
        CustomerId:            s.CustomerId.Value,
        ArAccountId:           s.ArAccountId.Value,
        RecurrenceRule:        s.RecurrenceRule,
        StartsOn:              s.StartsOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        EndsOn:                s.EndsOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Status:                s.Status.ToString(),
        GeneratedInvoiceCount: s.GeneratedInvoices.Count,
        LastGeneratedAtUtc:    s.LastGeneratedAtUtc?.ToString("o"));

    private static RecurringScheduleDetailWire ToDetail(RecurringInvoiceSchedule s) => new(
        ScheduleId:            s.Id.Value,
        TenantId:              s.TenantId.Value,
        ChartId:               s.ChartId.Value,
        CustomerId:            s.CustomerId.Value,
        ArAccountId:           s.ArAccountId.Value,
        RecurrenceRule:        s.RecurrenceRule,
        Timezone:              s.Timezone,
        StartsOn:              s.StartsOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        EndsOn:                s.EndsOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Status:                s.Status.ToString(),
        LookaheadHorizonDays:  s.LookaheadHorizonDays,
        GenerateLeadDays:      s.GenerateLeadDays,
        Lines:                 s.LineTemplates.Select(ToLineTemplate).ToArray(),
        GeneratedInvoiceCount: s.GeneratedInvoices.Count,
        LastGeneratedAtUtc:    s.LastGeneratedAtUtc?.ToString("o"));

    private static RecurringLineTemplateWire ToLineTemplate(RecurringInvoiceLineTemplate l) => new(
        Description:     l.Description,
        Quantity:        l.Quantity,
        UnitPrice:       l.UnitPrice,
        IncomeAccountId: l.IncomeAccountId.Value,
        TaxCodeId:       l.TaxCodeId,
        PropertyId:      l.PropertyId);

    private static ScheduleGenerationResultWire ToGenerationResult(ScheduleGenerationResult r) => new(
        ScheduleId:          r.ScheduleId.Value,
        Outcome:             r.Outcome.ToString(),
        NewlyGeneratedCount: r.NewlyGeneratedCount,
        AlreadyPresentCount: r.AlreadyPresentCount,
        Error:               r.Error,
        Invoices:            r.Invoices.Select(e => new InvoiceGenerationEntryWire(
            OccurrenceDate: e.OccurrenceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            InvoiceId:      e.InvoiceId.Value,
            Outcome:        e.Outcome.ToString())).ToArray());
}
