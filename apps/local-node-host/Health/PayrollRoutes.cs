using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Payroll;
using PayrollServices = (
    Harborline.Api.Blocks.Payroll.Services.IEmployeeRepository EmployeeRepo,
    Harborline.Api.Blocks.Payroll.Services.IPayRunRepository PayRunRepo,
    Harborline.Api.Blocks.Payroll.Services.IPayRunPostingService PostingService);

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local payroll surface — the T4 local-first sweep (ADR 0113 ABSOLUTE local-first). Drives
/// <c>PayrollEmployeesPage</c> / <c>PayrollRunsPage</c> fully offline: a single-device install
/// (signal-bridge STOPPED) lists/creates employees, lists/gets/creates pay runs, and posts/reverses
/// pay runs to the GL — all against the recoverable keyed (SQLCipher) <c>local-node.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns payroll data.</b> Payroll had NO durable persistence anywhere before T4 (the
/// block ships only in-memory repos; the Bridge wired <c>AddInMemoryPayroll()</c>). The three Node EF
/// repos over the node-exclusive <see cref="NodeLocalPayrollDbContext"/> give payroll its first durable
/// home. The pay-run post/reverse handlers call the block <c>IPayRunPostingService</c> which posts the
/// balanced double-entry journal entry through the node <c>IJournalPostingService</c> (the recoverable
/// <c>NodeEfJournalStore</c>) — so a posted pay run lands a balanced JE in the node GL, visible offline
/// in the JE grid.
/// </para>
/// <para>
/// <b>Wire shape == the FRONTEND consumer contract.</b> The handlers serve the exact camelCase fields
/// the <c>payroll.ts</c> interfaces read (<c>employeeId</c> / <c>payRunId</c> / <c>lineCount</c> /
/// <c>journalEntryId</c> / <c>lines[]</c> / …). ASP.NET Core minimal-API JSON defaults to camelCase, so
/// the PascalCase record properties serialise to the consumer field names. <b>Status casing:</b> the
/// node serves <c>"Draft" | "Posted" | "Reversed"</c> (PascalCase), matching what the page actually
/// compares (<c>status === 'Posted'</c>); this is the consumer-true value, intentionally different from
/// the legacy Bridge DTO's lower-cased <c>"posted"</c> (flagged for the SPOT-CHECK).
/// </para>
/// <para>
/// <b>Tenant + actor scoping.</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c>; the posting service reads the same active-team tenant off the
/// ambient <see cref="ActiveTeamTenantContext"/> (ADR 0032 identity layer), not a fixed <c>"local"</c>
/// sentinel. The post/reverse actor is the session-resolved caller party
/// (<see cref="NodeCallerParty"/>), falling back to the single operator when no principal is bound.
/// Opaque 404 for a missing pay run — the per-org isolation boundary.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only Kestrel listener (the Bridge CSRF +
/// AuthenticatedTenantPolicy posture does not apply on the node; <c>payroll.ts</c>'s antiforgery
/// fallback + token args become no-ops here).
/// </para>
/// <para>
/// <b>Wiring.</b> Every payroll dep is injected from the OUTER host container via
/// <see cref="PayrollServices"/> and passed to <see cref="Map"/> as a closed-over dependency —
/// NOT resolved via <c>[FromServices]</c> (bug-2849).
/// </para>
/// </remarks>
public static class PayrollRoutes
{
    /// <summary>Canonical route base for the node-local payroll surface.</summary>
    public const string RouteBase = "/api/local-node/payroll";

    /// <summary>Maps the payroll routes onto <paramref name="app"/>, closing over the payroll accessor.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        PayrollServices payroll,
        IActiveTeamAccessor activeTeam,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapListEmployees(app, payroll, activeTeam);
        MapCreateEmployee(app, payroll, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapListPayRuns(app, payroll, activeTeam);
        MapGetPayRun(app, payroll, activeTeam);
        MapCreatePayRun(app, payroll, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapPostPayRun(app, payroll, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapReversePayRun(app, payroll, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
    }

    // ── GET /api/local-node/payroll/employees ───────────────────────────────────
    private static void MapListEmployees(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/employees", async (CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var employees = await p.EmployeeRepo.ListActiveAsync(LocalTenantId, ct).ConfigureAwait(false);
            return Results.Ok(new EmployeeListWire(employees.Select(ToEmployeeSummary).ToArray()));
        });
    }

    // ── POST /api/local-node/payroll/employees ──────────────────────────────────
    private static void MapCreateEmployee(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/employees", async (CreateEmployeeBody body, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var at = new Instant(timeProvider.GetUtcNow());
            if (body is null)
                return Results.BadRequest(new { detail = "request_body_required" });
            if (string.IsNullOrWhiteSpace(body.PartyId))
                return Results.BadRequest(new { detail = "party_id_required" });
            if (string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { detail = "display_name_required" });
            if (string.IsNullOrWhiteSpace(body.WageExpenseAccountId))
                return Results.BadRequest(new { detail = "wage_expense_account_required" });
            if (string.IsNullOrWhiteSpace(body.WagesPayableAccountId))
                return Results.BadRequest(new { detail = "wages_payable_account_required" });

            Employee employee;
            try
            {
                employee = Employee.Create(
                    tenantId: LocalTenantId,
                    partyId: new PartyId(body.PartyId.Trim()),
                    displayName: body.DisplayName.Trim(),
                    wageExpenseAccountId: new GLAccountId(body.WageExpenseAccountId.Trim()),
                    wagesPayableAccountId: new GLAccountId(body.WagesPayableAccountId.Trim()),
                    createdAtUtc: at,
                    costCentreId: body.CostCentreId.HasValue
                        ? new ClassificationId(body.CostCentreId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        : (ClassificationId?)null);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { detail = "validation_error", title = ex.Message });
            }

            await p.EmployeeRepo.UpsertAsync(LocalTenantId, employee, ct).ConfigureAwait(false);
            return Results.Created($"{RouteBase}/employees/{employee.Id.Value}", ToEmployeeDetail(employee));
        });
    }

    // ── GET /api/local-node/payroll/pay-runs ────────────────────────────────────
    private static void MapListPayRuns(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/pay-runs", async (CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var runs = await p.PayRunRepo.ListAsync(LocalTenantId, ct).ConfigureAwait(false);
            return Results.Ok(new PayRunListWire(runs.Select(ToPayRunSummary).ToArray()));
        });
    }

    // ── GET /api/local-node/payroll/pay-runs/{id} ───────────────────────────────
    private static void MapGetPayRun(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/pay-runs/{{id}}", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var run = await p.PayRunRepo.GetAsync(LocalTenantId, new PayRunId(id), ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : Results.Ok(ToPayRunDetail(run));
        });
    }

    // ── POST /api/local-node/payroll/pay-runs ───────────────────────────────────
    private static void MapCreatePayRun(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/pay-runs", async (CreatePayRunBody body, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var at = new Instant(timeProvider.GetUtcNow());
            if (body is null)
                return Results.BadRequest(new { detail = "request_body_required" });
            if (string.IsNullOrWhiteSpace(body.Label))
                return Results.BadRequest(new { detail = "label_required" });
            if (!DateOnly.TryParse(body.PeriodStart, out var periodStart))
                return Results.BadRequest(new { detail = "period_start_invalid" });
            if (!DateOnly.TryParse(body.PeriodEnd, out var periodEnd))
                return Results.BadRequest(new { detail = "period_end_invalid" });
            if (!DateOnly.TryParse(body.PostingDate, out var postingDate))
                return Results.BadRequest(new { detail = "posting_date_invalid" });
            if (string.IsNullOrWhiteSpace(body.DefaultTaxWithheldAccountId))
                return Results.BadRequest(new { detail = "tax_withheld_account_required" });
            if (string.IsNullOrWhiteSpace(body.DefaultDeductionPayableAccountId))
                return Results.BadRequest(new { detail = "deduction_payable_account_required" });
            if (string.IsNullOrWhiteSpace(body.DefaultEmployerLiabilityExpenseAccountId))
                return Results.BadRequest(new { detail = "employer_liability_expense_account_required" });
            if (string.IsNullOrWhiteSpace(body.DefaultEmployerLiabilityPayableAccountId))
                return Results.BadRequest(new { detail = "employer_liability_payable_account_required" });

            // Build lines if supplied at create time (allowed but not required).
            IReadOnlyList<PayRunLine> lines = Array.Empty<PayRunLine>();
            if (body.Lines is { Count: > 0 })
            {
                var built = new List<PayRunLine>(body.Lines.Count);
                for (var i = 0; i < body.Lines.Count; i++)
                {
                    var ln = body.Lines[i];
                    if (string.IsNullOrWhiteSpace(ln.EmployeeId))
                        return Results.BadRequest(new { detail = "line_employee_id_required", title = $"Line {i + 1}: EmployeeId is required." });
                    try
                    {
                        built.Add(new PayRunLine(
                            employeeId: new EmployeeId(ln.EmployeeId.Trim()),
                            grossWage: ln.GrossWage,
                            taxWithheld: ln.TaxWithheld,
                            employeeDeductions: ln.EmployeeDeductions,
                            employerLiabilityAmount: ln.EmployerLiabilityAmount,
                            taxWithheldAccountId: ResolveAccount(ln.TaxWithheldAccountId, body.DefaultTaxWithheldAccountId!),
                            deductionPayableAccountId: ResolveAccount(ln.DeductionPayableAccountId, body.DefaultDeductionPayableAccountId!),
                            employerLiabilityExpenseAccountId: ResolveAccount(ln.EmployerLiabilityExpenseAccountId, body.DefaultEmployerLiabilityExpenseAccountId!),
                            employerLiabilityPayableAccountId: ResolveAccount(ln.EmployerLiabilityPayableAccountId, body.DefaultEmployerLiabilityPayableAccountId!))
                        {
                            Notes = ln.Notes,
                        });
                    }
                    catch (ArgumentException ex)
                    {
                        return Results.BadRequest(new { detail = "line_validation_error", title = $"Line {i + 1}: {ex.Message}" });
                    }
                }
                lines = built;
            }

            PayRun run;
            try
            {
                run = PayRun.Create(
                    tenantId: LocalTenantId,
                    label: body.Label.Trim(),
                    periodStart: periodStart,
                    periodEnd: periodEnd,
                    postingDate: postingDate,
                    defaultTaxWithheldAccountId: new GLAccountId(body.DefaultTaxWithheldAccountId!.Trim()),
                    defaultDeductionPayableAccountId: new GLAccountId(body.DefaultDeductionPayableAccountId!.Trim()),
                    defaultEmployerLiabilityExpenseAccountId: new GLAccountId(body.DefaultEmployerLiabilityExpenseAccountId!.Trim()),
                    defaultEmployerLiabilityPayableAccountId: new GLAccountId(body.DefaultEmployerLiabilityPayableAccountId!.Trim()),
                    createdAtUtc: at)
                    with { Lines = lines };
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { detail = "validation_error", title = ex.Message });
            }

            await p.PayRunRepo.UpsertAsync(LocalTenantId, run, ct).ConfigureAwait(false);
            return Results.Created($"{RouteBase}/pay-runs/{run.Id.Value}", ToPayRunDetail(run));
        });
    }

    // ── POST /api/local-node/payroll/pay-runs/{id}/post ──────────────────────────
    private static void MapPostPayRun(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/pay-runs/{{id}}/post", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var payRunId = new PayRunId(id);
            var existing = await p.PayRunRepo.GetAsync(LocalTenantId, payRunId, ct).ConfigureAwait(false);
            if (existing is null)
                return Results.NotFound();

            var authority = FinancialRouteWriteAuthority.Create(http, LocalTenantId, timeProvider);
            var result = await p.PostingService.PostAsync(payRunId, authority, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return Results.UnprocessableEntity(new { detail = result.Detail, title = result.Error.ToString() });

            return Results.Ok(ToPayRunDetail(result.PayRun!));
        });
    }

    // ── POST /api/local-node/payroll/pay-runs/{id}/reverse ───────────────────────
    private static void MapReversePayRun(IEndpointRouteBuilder app, PayrollServices p, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/pay-runs/{{id}}/reverse", async (string id, ReversePayRunBody? body, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var payRunId = new PayRunId(id);
            var existing = await p.PayRunRepo.GetAsync(LocalTenantId, payRunId, ct).ConfigureAwait(false);
            if (existing is null)
                return Results.NotFound();

            var reason = string.IsNullOrWhiteSpace(body?.Reason) ? "Reversed via local node" : body!.Reason!;
            var authority = FinancialRouteWriteAuthority.Create(http, LocalTenantId, timeProvider);
            var result = await p.PostingService.ReverseAsync(payRunId, reason, authority, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return Results.UnprocessableEntity(new { detail = result.Detail, title = result.Error.ToString() });

            return Results.Ok(ToPayRunDetail(result.PayRun!));
        });
    }

    // ── Account-override resolution ───────────────────────────────────────────────
    private static GLAccountId ResolveAccount(string? overrideValue, string defaultValue) =>
        string.IsNullOrWhiteSpace(overrideValue)
            ? new GLAccountId(defaultValue.Trim())
            : new GLAccountId(overrideValue.Trim());

    // ── Projection helpers — the consumer-true (payroll.ts) wire shape ──────────────
    private static EmployeeSummaryWire ToEmployeeSummary(Employee e) => new(
        EmployeeId: e.Id.Value,
        DisplayName: e.DisplayName,
        PartyId: e.PartyId.Value,
        IsActive: e.IsActive,
        WageExpenseAccountId: e.WageExpenseAccountId.Value,
        WagesPayableAccountId: e.WagesPayableAccountId.Value,
        CostCentreId: e.CostCentreId?.Value);

    private static EmployeeDetailWire ToEmployeeDetail(Employee e) => new(
        EmployeeId: e.Id.Value,
        DisplayName: e.DisplayName,
        PartyId: e.PartyId.Value,
        IsActive: e.IsActive,
        WageExpenseAccountId: e.WageExpenseAccountId.Value,
        WagesPayableAccountId: e.WagesPayableAccountId.Value,
        CostCentreId: e.CostCentreId?.Value,
        CreatedAtUtc: e.CreatedAtUtc.ToString(),
        UpdatedAtUtc: e.UpdatedAtUtc?.ToString());

    private static PayRunSummaryWire ToPayRunSummary(PayRun r) => new(
        PayRunId: r.Id.Value,
        Label: r.Label,
        PeriodStart: r.PeriodStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        PeriodEnd: r.PeriodEnd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        PostingDate: r.PostingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Status: r.Status.ToString(),
        LineCount: r.Lines.Count,
        JournalEntryId: r.JournalEntryId?.Value);

    private static PayRunDetailWire ToPayRunDetail(PayRun r) => new(
        PayRunId: r.Id.Value,
        Label: r.Label,
        PeriodStart: r.PeriodStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        PeriodEnd: r.PeriodEnd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        PostingDate: r.PostingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Status: r.Status.ToString(),
        DefaultTaxWithheldAccountId: r.DefaultTaxWithheldAccountId.Value,
        DefaultDeductionPayableAccountId: r.DefaultDeductionPayableAccountId.Value,
        DefaultEmployerLiabilityExpenseAccountId: r.DefaultEmployerLiabilityExpenseAccountId.Value,
        DefaultEmployerLiabilityPayableAccountId: r.DefaultEmployerLiabilityPayableAccountId.Value,
        JournalEntryId: r.JournalEntryId?.Value,
        ReversalEntryId: r.ReversalEntryId?.Value,
        Lines: r.Lines.Select(ToPayRunLineSummary).ToArray(),
        CreatedAtUtc: r.CreatedAtUtc.ToString(),
        UpdatedAtUtc: r.UpdatedAtUtc?.ToString(),
        Version: r.Version);

    private static PayRunLineSummaryWire ToPayRunLineSummary(PayRunLine l) => new(
        EmployeeId: l.EmployeeId.Value,
        GrossWage: l.GrossWage,
        TaxWithheld: l.TaxWithheld,
        EmployeeDeductions: l.EmployeeDeductions,
        NetWage: l.NetWage,
        EmployerLiabilityAmount: l.EmployerLiabilityAmount,
        TaxWithheldAccountId: l.TaxWithheldAccountId.Value,
        DeductionPayableAccountId: l.DeductionPayableAccountId.Value,
        EmployerLiabilityExpenseAccountId: l.EmployerLiabilityExpenseAccountId.Value,
        EmployerLiabilityPayableAccountId: l.EmployerLiabilityPayableAccountId.Value,
        Notes: l.Notes);
}

// ── Node wire DTOs — match the camelCase payroll.ts consumer contract ──────────────

/// <summary>Envelope for <c>GET /api/local-node/payroll/employees</c>.</summary>
public sealed record EmployeeListWire(IReadOnlyList<EmployeeSummaryWire> Employees);

/// <summary>One row in the employee-list response (== payroll.ts <c>EmployeeSummary</c>).</summary>
public sealed record EmployeeSummaryWire(
    string EmployeeId,
    string DisplayName,
    string PartyId,
    bool IsActive,
    string WageExpenseAccountId,
    string WagesPayableAccountId,
    string? CostCentreId);

/// <summary>Full employee detail (== payroll.ts <c>EmployeeDetail</c>).</summary>
public sealed record EmployeeDetailWire(
    string EmployeeId,
    string DisplayName,
    string PartyId,
    bool IsActive,
    string WageExpenseAccountId,
    string WagesPayableAccountId,
    string? CostCentreId,
    string CreatedAtUtc,
    string? UpdatedAtUtc);

/// <summary>Request body for <c>POST /api/local-node/payroll/employees</c>.</summary>
public sealed record CreateEmployeeBody(
    string? PartyId,
    string? DisplayName,
    string? WageExpenseAccountId,
    string? WagesPayableAccountId,
    int? CostCentreId = null);

/// <summary>Envelope for <c>GET /api/local-node/payroll/pay-runs</c>.</summary>
public sealed record PayRunListWire(IReadOnlyList<PayRunSummaryWire> PayRuns);

/// <summary>One row in the pay-run list (== payroll.ts <c>PayRunSummary</c>).</summary>
public sealed record PayRunSummaryWire(
    string PayRunId,
    string Label,
    string PeriodStart,
    string PeriodEnd,
    string PostingDate,
    string Status,
    int LineCount,
    string? JournalEntryId);

/// <summary>Full pay-run detail (== payroll.ts <c>PayRunDetail</c>).</summary>
public sealed record PayRunDetailWire(
    string PayRunId,
    string Label,
    string PeriodStart,
    string PeriodEnd,
    string PostingDate,
    string Status,
    string DefaultTaxWithheldAccountId,
    string DefaultDeductionPayableAccountId,
    string DefaultEmployerLiabilityExpenseAccountId,
    string DefaultEmployerLiabilityPayableAccountId,
    string? JournalEntryId,
    string? ReversalEntryId,
    IReadOnlyList<PayRunLineSummaryWire> Lines,
    string CreatedAtUtc,
    string? UpdatedAtUtc,
    int Version);

/// <summary>One pay-run line (== payroll.ts <c>PayRunLineSummary</c>).</summary>
public sealed record PayRunLineSummaryWire(
    string EmployeeId,
    decimal GrossWage,
    decimal TaxWithheld,
    decimal EmployeeDeductions,
    decimal NetWage,
    decimal EmployerLiabilityAmount,
    string TaxWithheldAccountId,
    string DeductionPayableAccountId,
    string EmployerLiabilityExpenseAccountId,
    string EmployerLiabilityPayableAccountId,
    string? Notes);

/// <summary>Request body for <c>POST /api/local-node/payroll/pay-runs</c>.</summary>
public sealed record CreatePayRunBody(
    string? Label,
    string? PeriodStart,
    string? PeriodEnd,
    string? PostingDate,
    string? DefaultTaxWithheldAccountId,
    string? DefaultDeductionPayableAccountId,
    string? DefaultEmployerLiabilityExpenseAccountId,
    string? DefaultEmployerLiabilityPayableAccountId,
    IReadOnlyList<CreatePayRunLineBody>? Lines = null);

/// <summary>One line within <see cref="CreatePayRunBody"/>.</summary>
public sealed record CreatePayRunLineBody(
    string? EmployeeId,
    decimal GrossWage,
    decimal TaxWithheld = 0m,
    decimal EmployeeDeductions = 0m,
    decimal EmployerLiabilityAmount = 0m,
    string? TaxWithheldAccountId = null,
    string? DeductionPayableAccountId = null,
    string? EmployerLiabilityExpenseAccountId = null,
    string? EmployerLiabilityPayableAccountId = null,
    string? Notes = null);

/// <summary>Request body for <c>POST /api/local-node/payroll/pay-runs/{id}/reverse</c>.</summary>
public sealed record ReversePayRunBody(string? Reason);
