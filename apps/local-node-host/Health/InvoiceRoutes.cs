using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local AR invoice surface — the Cohort D Step 2b AR node-flip
/// (ADR 0113 ABSOLUTE local-first). Issuing an invoice posts a balanced journal entry
/// (Debit the AR control account, Credit each line's income account) through the node-resident
/// posting service.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns AR invoice data.</b> Writes go through <see cref="IInvoicePostingService"/>
/// (which posts the issue / void / write-off JEs via the Step-2a node <c>IJournalPostingService</c>
/// over the recoverable <see cref="NodeEfJournalStore"/>); reads come from
/// <see cref="NodeEfInvoiceRepository"/>. So a single-device install no longer depends on
/// signal-bridge for AR. This is ADDITIVE — the Bridge AR path keeps working; the frontend flip and
/// the Rust <c>sc5</c> fail-close are a sequenced follow-up after the security SPOT-CHECK.
/// </para>
/// <para>
/// <b>Two-step lifecycle (vs the AP bill single-step create+record).</b> AR invoices are drafted
/// first (no GL post, editable, customer not yet billed) and posted on a separate Issue action —
/// matching the AR cluster's <see cref="InvoiceStatus.Draft"/> → <see cref="InvoiceStatus.Issued"/>
/// transition and the Bridge invoice surface.
/// <list type="bullet">
///   <item><c>GET /api/local-node/invoices</c> — list invoices in a chart. Filters: <c>?chartId=</c>
///     (required), <c>?customerId=</c>, <c>?status=open</c> (Issued/PartiallyPaid only).</item>
///   <item><c>GET /api/local-node/invoices/{id}</c> — full detail incl. lines; opaque 404 if absent /
///     other-tenant.</item>
///   <item><c>POST /api/local-node/invoices</c> — create a Draft invoice. Mints the canonical
///     <c>INV-YYYY-MM-DD-{Replica}-{NNNN}</c> number AT CREATE time (mirrors the Bridge create flow +
///     dodges the EF unique-index empty-string collision), persists the Draft, and returns it
///     (NO GL post yet). 201 Created. ExternalRef-duplicate ⇒ 409.</item>
///   <item><c>POST /api/local-node/invoices/{id}/issue</c> — Draft → Issued: computes per-line tax,
///     posts the balanced journal entry (Debit AR / Credit Income), transitions to
///     <see cref="InvoiceStatus.Issued"/>, returns the issued invoice incl. its
///     <c>journalEntryId</c>.</item>
///   <item><c>POST /api/local-node/invoices/{id}/void</c> — post a reversing journal entry +
///     transition to <see cref="InvoiceStatus.Voided"/>.</item>
///   <item><c>POST /api/local-node/invoices/{id}/write-off</c> — post a bad-debt journal entry
///     (Debit BadDebtExpense / Credit AR) + transition to <see cref="InvoiceStatus.WrittenOff"/>.</item>
///   <item><c>DELETE /api/local-node/invoices/{id}</c> — hard-delete a <b>Draft</b> invoice (true
///     row removal from <c>local-node.db</c>). Doctrine: Invoice-Draft is the ONE entity in the fleet
///     that gets a true hard-DELETE (no GL post exists; no reversing entry required). Non-Draft
///     invoices are rejected with 422 (use void/write-off to close them). TOCTOU-safe: status is
///     re-checked inside the EF transaction via <see cref="NodeEfInvoiceRepository.HardDeleteIfDraftAsync"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + write resolves the active-team-derived tenant
/// via <c>NodeTenant.Resolve(activeTeam)</c> (the same posture as <see cref="BillRoutes"/> /
/// <see cref="JournalEntryRoutes"/>), not a fixed <c>"local"</c> sentinel. The write services read the
/// ambient tenant off the node-resident <see cref="ActiveTeamTenantContext"/> (ADR 0032 identity layer —
/// active-team-bound, retiring the old <c>StaticNodeTenantContext</c> "local" literal); the repository
/// applies a <c>WHERE TenantId</c> — the per-org isolation predicate, so switching the active org
/// switches which org's invoices are visible. The invoice <c>id</c> is the only caller-supplied
/// identifier the write paths trust.
/// </para>
/// <para>
/// <b>Issue / void / write-off JEs are SCOPED to the invoice's chart.</b>
/// <see cref="InvoicePostingService"/> builds the journal entry WITH the invoice's <c>ChartId</c>, so
/// the posting service's full six-phase algorithm — including Phase-4 period-gating — applies. The
/// JE posts whenever its referenced GL accounts exist + are postable and the issue date falls in an
/// open (or soft-closed, given the node's explicit override permission) period. This is the faithful
/// cluster-default <see cref="InvoicePostingService"/> behavior; Step 2b relocates the host, it does
/// not change posting semantics.
/// </para>
/// <para>
/// <b>Financial-cluster audit-envelope durable-layer pattern (ADR 0104 §7).</b> No inline signed
/// audit event — the node IS the durable mutation layer; the invoice + JE rows' presence in the keyed
/// SQLCipher store satisfies X-AUDIT, the same deferred posture <see cref="JournalEntryRoutes"/> /
/// <see cref="BillRoutes"/> and the financial masters carry.
/// </para>
/// <para>
/// <b>SC4-C2 recoverability.</b> The whole write path persists ONLY the Store-DEK-enveloped,
/// recoverable <c>local-node.db</c> (invoices via <see cref="NodeEfInvoiceRepository"/>, the JEs via
/// <see cref="NodeEfJournalStore"/>) — no kernel CRDT / per-team event-log write, and the
/// <c>IDomainEventPublisher</c> is the Noop (no cross-cluster event bus).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1).</b> These bind the loopback-only Kestrel listener, but a loopback
/// bind authenticates the HOST not the calling PROCESS — so the LISTENER-LEVEL caller-auth
/// middleware (<c>SharedHostedWebApp</c>) gates every non-allowlisted route, including these invoice
/// writes, behind the Harborline App's per-boot session token, fail-closed 401, by default. CSRF is N/A
/// (explicit bearer, no cookie/ambient auth).
/// </para>
/// <para>
/// <b>Wiring.</b> The repository accessor + posting-service accessor are injected from the OUTER host
/// container and passed to <see cref="Map"/> as closed-over dependencies — NOT resolved via
/// <c>[FromServices]</c>, which would fail on the inner shared-app container (bug-2849).
/// </para>
/// </remarks>
public static class InvoiceRoutes
{
    /// <summary>Canonical route base for the node-local invoices surface.</summary>
    public const string RouteBase = "/api/local-node/invoices";

    /// <summary>
    /// Canonical route base for the parked CP human-task surface (the ADR 0135 §2.8.2 Ask-bar Inbox). The
    /// issue route's 202 Location header points at <c>{ApprovalTasksRouteBase}/{instanceId}</c> — kept in sync
    /// with <see cref="InvoiceApprovalTaskRoutes.RouteBase"/>.
    /// </summary>
    public const string ApprovalTasksRouteBase = "/api/local-node/approval-tasks";

    /// <summary>
    /// Maps the invoice routes onto <paramref name="app"/>, closing over the <paramref name="invoices"/>
    /// repository accessor (reads + Draft create), the <paramref name="numbering"/> service accessor
    /// (mints the canonical number at create time), and the <paramref name="posting"/> service accessor
    /// (issue / void / write-off) from the outer host container.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeEfInvoiceRepository invoices,
        IInvoiceNumberingService numbering,
        IInvoicePostingService posting, IActiveTeamAccessor activeTeam,
        NodeInvoiceApprovalCutover? approvalCutover = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(numbering);
        ArgumentNullException.ThrowIfNull(posting);

        MapList(app, invoices, activeTeam);
        MapDetail(app, invoices, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapCreate(app, invoices, numbering, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapIssue(app, invoices, posting, activeTeam, approvalCutover, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapVoid(app, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapWriteOff(app, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapDeleteDraft(app, invoices, activeTeam);
    }

    // ── GET /api/local-node/invoices — list invoices in a chart ───────────────────
    private static void MapList(IEndpointRouteBuilder app, NodeEfInvoiceRepository invoices, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (
            string? chartId,
            string? customerId,
            string? status,
            HttpContext http,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsRead, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            if (string.IsNullOrWhiteSpace(chartId))
            {
                return Results.BadRequest(new { error = "chart_id_required" });
            }

            var chart = new ChartOfAccountsId(chartId);
            IReadOnlyList<Invoice> rows;

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                rows = await invoices
                    .ListByCustomerAsync(LocalTenantId, chart, new PartyId(customerId), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                rows = await invoices.ListByChartAsync(LocalTenantId, chart, ct).ConfigureAwait(false);
            }

            // ?status=open narrows to the open set (Issued / PartiallyPaid) — applied in-memory because
            // InvoiceStatus.IsOpen() is an extension method (mirrors the BillRoutes open-filter posture;
            // small single-device set).
            if (string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(i => i.Status.IsOpen()).ToList();
            }

            // Newest-first by issue date (small single-device set; in-memory sort mirrors BillRoutes).
            var ordered = rows.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.CreatedAtUtc.Value);
            return Results.Ok(new InvoiceListResponse(
                Data: ordered.Select(InvoiceSummaryWire.From).ToList()));
        });
    }

    // ── GET /api/local-node/invoices/{id} — detail incl. lines ────────────────────
    private static void MapDetail(IEndpointRouteBuilder app, NodeEfInvoiceRepository invoices, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapGet($"{RouteBase}/{{id}}", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsRead, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var invoice = await invoices.GetAsync(LocalTenantId, new InvoiceId(id), timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            return invoice is null
                ? Results.NotFound()
                : Results.Ok(new InvoiceDetailResponse(InvoiceDetailWire.From(invoice)));
        });
    }

    // ── POST /api/local-node/invoices — create a Draft (mint number; no GL post) ──
    private static void MapCreate(
        IEndpointRouteBuilder app,
        NodeEfInvoiceRepository invoices,
        IInvoiceNumberingService numbering, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost(RouteBase, async (CreateInvoiceRequest body, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsWrite, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            var admittedAt = timeProvider.GetUtcNow();
            if (body is null)
            {
                return Results.BadRequest(new { error = "request_null" });
            }
            if (string.IsNullOrWhiteSpace(body.ChartId))
            {
                return Results.BadRequest(new { error = "chart_id_required" });
            }
            if (string.IsNullOrWhiteSpace(body.CustomerId))
            {
                return Results.BadRequest(new { error = "customer_id_required" });
            }
            if (string.IsNullOrWhiteSpace(body.ArAccountId))
            {
                return Results.BadRequest(new { error = "ar_account_required" });
            }
            if (body.Lines is null || body.Lines.Count == 0)
            {
                return Results.BadRequest(new { error = "no_lines" });
            }
            if (!DateOnly.TryParse(body.IssueDate, out var issueDate))
            {
                return Results.BadRequest(new { error = "invalid_issue_date" });
            }
            if (!DateOnly.TryParse(body.DueDate, out var dueDate))
            {
                return Results.BadRequest(new { error = "invalid_due_date" });
            }
            if (body.Lines.Any(l => string.IsNullOrWhiteSpace(l.IncomeAccountId)))
            {
                return Results.BadRequest(new { error = "line_income_account_required" });
            }

            var chartId = new ChartOfAccountsId(body.ChartId!);

            // ExternalRef-based create-draft idempotency (mirrors the Bridge create flow): a stable
            // external reference (e.g. CRM opportunity id) detects double-submit via 409 rather than
            // creating a duplicate draft. Without one, two POSTs produce two independent drafts.
            var externalRef = string.IsNullOrWhiteSpace(body.ExternalRef) ? null : body.ExternalRef!.Trim();
            if (externalRef is not null)
            {
                var dup = await invoices.GetByExternalRefAsync(LocalTenantId, chartId, externalRef, ct)
                    .ConfigureAwait(false);
                if (dup is not null)
                {
                    return Results.Conflict(new { error = "external_ref_duplicate" });
                }
            }

            var invoiceId = string.IsNullOrWhiteSpace(body.Id) ? InvoiceId.NewId() : new InvoiceId(body.Id);

            // Mint the canonical number AT CREATE time (mirrors the Bridge create flow). This keeps even
            // the Draft carrying a real INV-… number, so the EF unique index on (tenant, chart, number)
            // is never asked to store two empty-string drafts. The posting service sees a non-empty
            // number on Issue and skips re-minting.
            string invoiceNumber;
            try
            {
                invoiceNumber = await numbering.NextNumberAsync(chartId, issueDate, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            // Build the invoice lines (amount = banker's-round(qty * unitPrice) per InvoiceLine.Create).
            var lines = new List<InvoiceLine>(body.Lines.Count);
            var lineNumber = 1;
            foreach (var l in body.Lines)
            {
                lines.Add(InvoiceLine.Create(
                    invoiceId:       invoiceId,
                    lineNumber:      lineNumber++,
                    description:     l.Description ?? string.Empty,
                    quantity:        l.Quantity,
                    unitPrice:       l.UnitPrice,
                    incomeAccountId: new GLAccountId(l.IncomeAccountId!),
                    taxCodeId:       l.TaxCodeId,
                    propertyId:      l.PropertyId,
                    classificationId: l.ClassificationId,
                    notes:           l.Notes));
            }

            // ADR 0122 §D4 P2 → (b): stamp the sub-ledger FK at create time when supplied (the
            // lease's SubLedgerAccountId from LeaseSubLedgerService activation). Without this the
            // ISubLedgerReadModel projection's FK-authoritative ListBySubLedgerAccountAsync matches
            // ZERO node invoices and a lease's offline history renders empty. Null for non-lease
            // invoices (additive/nullable FK — back-compat).
            var subLedgerAccountId = string.IsNullOrWhiteSpace(body.SubLedgerAccountId)
                ? (SubLedgerAccountId?)null
                : new SubLedgerAccountId(body.SubLedgerAccountId!);

            var invoice = Invoice.Create(
                tenantId:      LocalTenantId,
                chartId:       chartId,
                invoiceNumber: invoiceNumber,
                customerId:    new PartyId(body.CustomerId!),
                issueDate:     issueDate,
                dueDate:       dueDate,
                lines:         lines,
                arAccountId:   new GLAccountId(body.ArAccountId!),
                createdAtUtc:  new Instant(admittedAt),
                createdBy:     NodeCallerParty.Resolve(http),
                id:            invoiceId,
                propertyId:    body.PropertyId,
                currency:      string.IsNullOrWhiteSpace(body.Currency) ? "USD" : body.Currency!,
                notes:         body.Notes,
                termsId:       body.TermsId,
                externalRef:   externalRef,
                subLedgerAccountId: subLedgerAccountId);

            try
            {
                await invoices.UpsertAsync(LocalTenantId, invoice, admittedAt, ct).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // Cross-tenant id collision — the upsert's tenant-mismatch guard (an invoice id that
                // exists under a DIFFERENT active-team tenant). Real once multiple orgs share the store.
                return Results.Conflict(new { error = "invoice_id_conflict" });
            }
            catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
            {
                // OBS-2: a unique-constraint race against the recoverable store (e.g. a duplicate
                // (tenant, chart, invoice-number) or external-ref slipping past the pre-check) is a
                // conflict, not a server error — 409, not 500.
                return Results.Conflict(new { error = "invoice_id_conflict" });
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            // Audit-envelope: satisfied by the durable SQLCipher store presence (durable-layer pattern).
            return Results.Created(
                $"{RouteBase}/{invoice.Id.Value}",
                new InvoiceDetailResponse(InvoiceDetailWire.From(invoice)));
        });
    }

    // ── POST /api/local-node/invoices/{id}/issue — Draft → Issued ──
    //
    // CUTOVER (ADR 0135 Handler A): when the approval engine is wired AND the Draft invoice's total is
    // STRICTLY ABOVE the v1 threshold ($5k), the issue does NOT post the JE inline — it routes the invoice
    // through the engine (creates an invoice-approval Process that PARKS the approval) and returns 202. The
    // JE posts only on approve. The ≤ $5k case (and any host that has not wired the engine) is the unchanged
    // direct-post. This is no-double-post BY CONSTRUCTION: an over-threshold invoice has NO inline issuer —
    // IssueAsync is never called here for it, so the approval Process's post effect is its only issuer.
    private static void MapIssue(
        IEndpointRouteBuilder app,
        NodeEfInvoiceRepository invoices,
        IInvoicePostingService posting,
        IActiveTeamAccessor activeTeam,
        NodeInvoiceApprovalCutover? approvalCutover,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/issue", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.RecordsWrite,
                    RouteRecord.Of(id), ct) is { } denied)
                return denied;
            // Approval cutover: route an over-threshold Draft through the engine BEFORE any inline post.
            if (approvalCutover is not null)
            {
                var tenantId = NodeTenant.Resolve(activeTeam);
                Invoice? draft;
                try
                {
                    draft = await invoices.GetAsync(tenantId, new InvoiceId(id), authority.At, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }

                // Only a still-Draft invoice over the threshold is routed. An unknown / already-issued / other-
                // status invoice falls through to the direct posting service, which returns the faithful
                // 404 / idempotent-already-issued / invalid-status response (no behaviour change for them).
                if (draft is { Status: InvoiceStatus.Draft } && NodeInvoiceApprovalCutover.ShouldRouteToEngine(draft))
                {
                    InvoiceApprovalRouteResult routed;
                    try
                    {
                        routed = await approvalCutover
                            .RouteForApprovalAsync(tenantId, draft, authority.Principal, authority.At, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return Results.StatusCode(StatusCodes.Status500InternalServerError);
                    }

                    // 202 Accepted — the invoice is parked for approval; NO JE was posted; the invoice stays
                    // Draft until the parked task is approved. The body carries the resume / inbox key.
                    return Results.Accepted(
                        $"{ApprovalTasksRouteBase}/{routed.InstanceId}",
                        new IssueParkedForApprovalResponse(
                            Status:    "parked_for_approval",
                            InvoiceId: draft.Id.Value,
                            InstanceId: routed.InstanceId,
                            Amount:    (double)draft.Total,
                            Threshold: (double)routed.Threshold));
                }
            }

            IssueResult result;
            try
            {
                result = await posting.IssueAsync(new InvoiceId(id), authority, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return IssueErrorToResult(result);
            }
            return Results.Ok(new InvoiceDetailResponse(InvoiceDetailWire.From(result.Invoice!)));
        });
    }

    // ── POST /api/local-node/invoices/{id}/void — post a reversing JE ─────────────
    private static void MapVoid(IEndpointRouteBuilder app, IInvoicePostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/void", async (
            string id,
            VoidInvoiceRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.RecordsWrite,
                    RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var reason = body?.Reason ?? string.Empty;
            VoidResult result;
            try
            {
                result = await posting.VoidAsync(new InvoiceId(id), reason, authority, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return VoidErrorToResult(result);
            }
            return Results.Ok(new InvoiceDetailResponse(InvoiceDetailWire.From(result.Invoice!)));
        });
    }

    // ── POST /api/local-node/invoices/{id}/write-off — post a bad-debt JE ─────────
    private static void MapWriteOff(IEndpointRouteBuilder app, IInvoicePostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/write-off", async (
            string id,
            WriteOffInvoiceRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.RecordsWrite,
                    RouteRecord.Of(id), ct) is { } denied)
                return denied;
            if (body is null || string.IsNullOrWhiteSpace(body.BadDebtAccountId))
            {
                return Results.BadRequest(new { error = "bad_debt_account_required" });
            }

            var reason = body.Reason ?? string.Empty;
            WriteOffResult result;
            try
            {
                result = await posting.WriteOffAsync(
                        new InvoiceId(id),
                        new GLAccountId(body.BadDebtAccountId!),
                        reason,
                        authority,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return WriteOffErrorToResult(result);
            }
            return Results.Ok(new InvoiceDetailResponse(InvoiceDetailWire.From(result.Invoice!)));
        });
    }

    // ── DELETE /api/local-node/invoices/{id} — hard-delete a Draft invoice ────────
    private static void MapDeleteDraft(IEndpointRouteBuilder app, NodeEfInvoiceRepository invoices, IActiveTeamAccessor activeTeam)
    {
        app.MapDelete($"{RouteBase}/{{id}}", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsWrite, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            bool? result;
            try
            {
                result = await invoices.HardDeleteIfDraftAsync(
                        LocalTenantId, new InvoiceId(id), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            // null = invoice exists + belongs to the tenant but is NOT Draft (concurrent Issue beat us).
            // Doctrine: only Draft invoices may be hard-deleted; issued/voided/written-off invoices have
            // a GL footprint and must be closed via void / write-off.
            if (result is null)
            {
                return Results.UnprocessableEntity(new { error = "not_draft", detail = "Only Draft invoices may be deleted. Use void or write-off to close an issued invoice." });
            }

            // false = unknown id OR foreign-tenant (uniform-404 invariant — no diagnostic leak).
            if (!result.Value)
            {
                return Results.NotFound();
            }

            return Results.NoContent();
        });
    }

    // ── Posting-result → HTTP response mapping ───────────────────────────────────

    private static IResult IssueErrorToResult(IssueResult result) => result.Error switch
    {
        IssueError.UnknownInvoice        => Results.NotFound(),
        IssueError.InvalidStatusForIssue => Results.BadRequest(new { error = "invalid_status_for_issue", detail = result.Detail }),
        IssueError.NoLines               => Results.BadRequest(new { error = "no_lines", detail = result.Detail }),
        IssueError.JournalRejected       => Results.BadRequest(new { error = "journal_rejected", detail = result.Detail }),
        _                                => Results.BadRequest(new { error = "issue_rejected", detail = result.Detail }),
    };

    private static IResult VoidErrorToResult(VoidResult result) => result.Error switch
    {
        VoidError.UnknownInvoice           => Results.NotFound(),
        VoidError.InvalidStatusForVoid     => Results.BadRequest(new { error = "invalid_status_for_void", detail = result.Detail }),
        VoidError.NoJournalEntryToReverse  => Results.BadRequest(new { error = "no_journal_entry_to_reverse", detail = result.Detail }),
        VoidError.JournalRejected          => Results.BadRequest(new { error = "journal_rejected", detail = result.Detail }),
        _                                  => Results.BadRequest(new { error = "void_rejected", detail = result.Detail }),
    };

    private static IResult WriteOffErrorToResult(WriteOffResult result) => result.Error switch
    {
        WriteOffError.UnknownInvoice            => Results.NotFound(),
        WriteOffError.InvalidStatusForWriteOff  => Results.BadRequest(new { error = "invalid_status_for_write_off", detail = result.Detail }),
        WriteOffError.InvalidBadDebtAccount     => Results.BadRequest(new { error = "bad_debt_account_required", detail = result.Detail }),
        WriteOffError.JournalRejected           => Results.BadRequest(new { error = "journal_rejected", detail = result.Detail }),
        _                                       => Results.BadRequest(new { error = "write_off_rejected", detail = result.Detail }),
    };
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>Summary row in the node invoice list response (camelCase JSON).</summary>
public sealed record InvoiceSummaryWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("invoiceNumber")] string InvoiceNumber,
    [property: JsonPropertyName("customerId")] string CustomerId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("issueDate")] string IssueDate,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("subtotal")] double Subtotal,
    [property: JsonPropertyName("taxTotal")] double TaxTotal,
    [property: JsonPropertyName("total")] double Total,
    [property: JsonPropertyName("amountPaid")] double AmountPaid,
    [property: JsonPropertyName("balance")] double Balance,
    [property: JsonPropertyName("journalEntryId")] string? JournalEntryId,
    [property: JsonPropertyName("propertyId")] string? PropertyId)
{
    /// <summary>Projects a domain <see cref="Invoice"/> onto the summary wire shape.</summary>
    public static InvoiceSummaryWire From(Invoice i) => new(
        Id:             i.Id.Value,
        InvoiceNumber:  i.InvoiceNumber,
        CustomerId:     i.CustomerId.Value,
        ChartId:        i.ChartId.Value,
        Status:         i.Status.ToString(),
        IssueDate:      i.IssueDate.ToString("O"),
        DueDate:        i.DueDate.ToString("O"),
        Subtotal:       (double)i.Subtotal,
        TaxTotal:       (double)i.TaxTotal,
        Total:          (double)i.Total,
        AmountPaid:     (double)i.AmountPaid,
        Balance:        (double)i.Balance,
        JournalEntryId: i.JournalEntryId?.Value,
        PropertyId:     i.PropertyId);
}

/// <summary>Detail wire shape incl. lines (mirrors the invoice aggregate).</summary>
public sealed record InvoiceDetailWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("invoiceNumber")] string InvoiceNumber,
    [property: JsonPropertyName("customerId")] string CustomerId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("issueDate")] string IssueDate,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("subtotal")] double Subtotal,
    [property: JsonPropertyName("taxTotal")] double TaxTotal,
    [property: JsonPropertyName("total")] double Total,
    [property: JsonPropertyName("amountPaid")] double AmountPaid,
    [property: JsonPropertyName("balance")] double Balance,
    [property: JsonPropertyName("arAccountId")] string ArAccountId,
    [property: JsonPropertyName("subLedgerAccountId")] string? SubLedgerAccountId,
    [property: JsonPropertyName("journalEntryId")] string? JournalEntryId,
    [property: JsonPropertyName("voidedByEntryId")] string? VoidedByEntryId,
    [property: JsonPropertyName("writtenOffByEntryId")] string? WrittenOffByEntryId,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("externalRef")] string? ExternalRef,
    [property: JsonPropertyName("lines")] IReadOnlyList<InvoiceLineWire> Lines)
{
    /// <summary>Projects a domain <see cref="Invoice"/> onto the detail wire shape.</summary>
    public static InvoiceDetailWire From(Invoice i) => new(
        Id:                  i.Id.Value,
        InvoiceNumber:       i.InvoiceNumber,
        CustomerId:          i.CustomerId.Value,
        ChartId:             i.ChartId.Value,
        Status:              i.Status.ToString(),
        IssueDate:           i.IssueDate.ToString("O"),
        DueDate:             i.DueDate.ToString("O"),
        Currency:            i.Currency,
        Subtotal:            (double)i.Subtotal,
        TaxTotal:            (double)i.TaxTotal,
        Total:               (double)i.Total,
        AmountPaid:          (double)i.AmountPaid,
        Balance:             (double)i.Balance,
        ArAccountId:         i.ArAccountId.Value,
        SubLedgerAccountId:  i.SubLedgerAccountId?.Value,
        JournalEntryId:      i.JournalEntryId?.Value,
        VoidedByEntryId:     i.VoidedByEntryId?.Value,
        WrittenOffByEntryId: i.WrittenOffByEntryId?.Value,
        PropertyId:          i.PropertyId,
        Notes:               i.Notes,
        ExternalRef:         i.ExternalRef,
        Lines:               i.Lines.Select(InvoiceLineWire.From).ToList());
}

/// <summary>One line in an invoice detail response.</summary>
public sealed record InvoiceLineWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("lineNumber")] int LineNumber,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] double Quantity,
    [property: JsonPropertyName("unitPrice")] double UnitPrice,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("incomeAccountId")] string IncomeAccountId,
    [property: JsonPropertyName("taxCodeId")] string? TaxCodeId,
    [property: JsonPropertyName("taxAmount")] double TaxAmount,
    [property: JsonPropertyName("propertyId")] string? PropertyId)
{
    /// <summary>Projects a domain <see cref="InvoiceLine"/> onto the line wire shape.</summary>
    public static InvoiceLineWire From(InvoiceLine l) => new(
        Id:              l.Id.Value,
        LineNumber:      l.LineNumber,
        Description:     l.Description,
        Quantity:        (double)l.Quantity,
        UnitPrice:       (double)l.UnitPrice,
        Amount:          (double)l.Amount,
        IncomeAccountId: l.IncomeAccountId.Value,
        TaxCodeId:       l.TaxCodeId,
        TaxAmount:       (double)l.TaxAmount,
        PropertyId:      l.PropertyId);
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record InvoiceListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<InvoiceSummaryWire> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record InvoiceDetailResponse(
    [property: JsonPropertyName("data")] InvoiceDetailWire Data);

/// <summary>POST body for <c>POST /api/local-node/invoices</c> (create Draft).</summary>
public sealed record CreateInvoiceRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("customerId")] string CustomerId,
    [property: JsonPropertyName("arAccountId")] string ArAccountId,
    [property: JsonPropertyName("issueDate")] string IssueDate,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("termsId")] string? TermsId,
    [property: JsonPropertyName("externalRef")] string? ExternalRef,
    // ADR 0122 §D4 P2 → (b): the lease's sub-ledger account FK, stamped at create time so the
    // sub-ledger projection resolves this invoice into the lease's offline payment-history. Optional —
    // null for non-lease invoices.
    [property: JsonPropertyName("subLedgerAccountId")] string? SubLedgerAccountId,
    [property: JsonPropertyName("lines")] IReadOnlyList<CreateInvoiceLine> Lines);

/// <summary>One line in a create-invoice request.</summary>
public sealed record CreateInvoiceLine(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("incomeAccountId")] string IncomeAccountId,
    [property: JsonPropertyName("taxCodeId")] string? TaxCodeId,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("classificationId")] string? ClassificationId,
    [property: JsonPropertyName("notes")] string? Notes);

/// <summary>
/// 202 response when an over-threshold invoice issue is PARKED for approval (ADR 0135 Handler A). No JE was
/// posted; the invoice stays Draft. The frontend uses <c>instanceId</c> to find the task in the Inbox + to
/// resume it (approve / reject / send-back).
/// </summary>
public sealed record IssueParkedForApprovalResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("invoiceId")] string InvoiceId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("threshold")] double Threshold);

/// <summary>POST body for <c>POST /api/local-node/invoices/{id}/void</c>.</summary>
public sealed record VoidInvoiceRequest(
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>POST body for <c>POST /api/local-node/invoices/{id}/write-off</c>.</summary>
public sealed record WriteOffInvoiceRequest(
    [property: JsonPropertyName("badDebtAccountId")] string BadDebtAccountId,
    [property: JsonPropertyName("reason")] string? Reason);
