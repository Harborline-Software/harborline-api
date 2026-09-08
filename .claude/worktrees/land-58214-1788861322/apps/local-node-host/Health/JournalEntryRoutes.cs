using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local journal-entry surface — the Cohort D financial-ledger node-flip
/// (ADR 0113 ABSOLUTE local-first; ADR 0121 the JE read-model this relocates).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns journal-entry data.</b> Reads compose the host-agnostic
/// <see cref="IJournalEntryQueryReadModel"/> (merged in earlier repository ticket #1161) over the node-resident
/// store; manual writes + reversals go through the node <c>IJournalStore</c>. So a single-device
/// install no longer depends on signal-bridge for the Journal Entries page (incl. the Phase-3a
/// Account column). This is ADDITIVE — the Bridge JE path keeps working; the frontend flip and
/// the Rust <c>sc5</c> fail-close are a sequenced follow-up after the security SPOT-CHECK.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/journal-entries</c> — paged summary list. Filters:
///     <c>?accountId=</c> (line-account scope; surfaces the Phase-3a <c>accountIds</c> +
///     "— Split —" inputs), <c>?chartId=</c>, <c>?from=</c>/<c>?to=</c> (ISO dates),
///     <c>?status=</c> + <c>?source=</c> (comma-separated), <c>?search=</c>, <c>?sort=</c>,
///     <c>?dir=</c>, plus keyset paging via <c>?cursor=</c> (offset <c>?skip=</c> fallback) and
///     <c>?take=</c>. Composes <see cref="IJournalEntryQueryReadModel.QueryAsync"/>.</item>
///   <item><c>GET /api/local-node/journal-entries/{id}</c> — full detail incl. lines; opaque
///     404 if absent or other-tenant.</item>
///   <item><c>POST /api/local-node/journal-entries</c> — record a MANUAL balanced entry
///     (Posted, <see cref="JournalEntrySource.Manual"/>).</item>
///   <item><c>POST /api/local-node/journal-entries/{id}/reverse</c> — create the reversing
///     entry (swapped debits/credits, <see cref="JournalEntrySource.Reversal"/>) and transition
///     the original to <see cref="JournalEntryStatus.Reversed"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + write resolves the active-team-derived tenant
/// via <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer),
/// not a fixed <c>"local"</c> sentinel (same posture as <see cref="PaymentRoutes"/> /
/// <see cref="ChartOfAccountsRoutes"/>). The tenant is server-set, never frontend-passed; the store's
/// <c>Snapshot</c> applies a <c>WHERE TenantId</c> — the per-org isolation predicate, so switching the
/// active org switches which org's journal entries are visible.
/// </para>
/// <para>
/// <b>Financial-cluster audit-envelope durable-layer pattern (ADR 0104 §7).</b> The write routes
/// emit NO inline signed audit event — the node IS the durable mutation layer, and a row's presence
/// in the keyed SQLCipher store satisfies the X-AUDIT obligation, the same deferred posture
/// <see cref="ChartOfAccountsRoutes"/> and the financial masters carry. Inline audit emission is a
/// follow-on once node-side <c>IAuditTrail</c>/<c>IOperationSigner</c> infra is wired.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1).</b> The node routes bind the loopback-only Kestrel listener, but a
/// loopback bind authenticates the HOST not the calling PROCESS — so the LISTENER-LEVEL caller-auth
/// middleware (<c>SharedHostedWebApp</c>) gates this write route (and every non-allowlisted route)
/// behind the Harborline App's per-boot session token, fail-closed 401, by default. CSRF is N/A (no
/// cookie/ambient auth — the Harborline App presents an explicit bearer, not a browser form). The Bridge
/// equivalents carry CSRF + AccountantPolicy because they are network-reachable; the node surface is
/// loopback + caller-token gated.
/// </para>
/// <para>
/// <b>Posting via the six-phase service (Cohort D Step 2a).</b> Manual creates + reversals build a
/// <see cref="JournalEntryStatus.Draft"/> <see cref="JournalEntry"/> and route it through
/// <see cref="IJournalPostingService.PostAsync"/>, which runs the full six-phase algorithm
/// (preconditions → balance → account-validity → period-gating → atomic commit → result) over the
/// node-resident <c>IAccountResolver</c>/<c>IPeriodResolver</c>/<c>IUserContext</c> (all EF reads
/// over the recoverable <c>local-node.db</c>). The service promotes the draft to
/// <see cref="JournalEntryStatus.Posted"/> and persists it via <c>IJournalStore.SaveAtomicAsync</c>
/// (== <see cref="NodeEfJournalStore"/>). The route still front-runs cheap shape validation (null /
/// min-two-lines / direction / positive-amount / balance / posting-date / memo) so those preserve
/// their existing 400 error codes BEFORE the entry is built; the service then adds the real
/// account-validity + period-gating gates, surfaced as structured 400 error codes
/// (<c>unknown_account</c> / <c>wrong_chart</c> / <c>account_not_postable</c> /
/// <c>no_period_for_date</c> / <c>period_locked</c> / <c>period_soft_closed</c>).
/// </para>
/// <para>
/// <b>SC4-C2 recoverability.</b> The whole posting path writes ONLY the Store-DEK-enveloped,
/// recoverable <c>local-node.db</c> (journal entries) — no kernel CRDT / per-team event-log write,
/// no <c>IDomainEventPublisher</c> call (security-engineering SC4-C2 verdict 2026-06-15, conditions
/// (a)-(d), enforced by <c>Sc4RecoverabilityGuardTests</c>).
/// </para>
/// <para>
/// <b>Wiring.</b> The read-model + store are injected from the OUTER host container and passed to
/// <see cref="Map"/> as closed-over dependencies — NOT resolved via <c>[FromServices]</c>, which
/// would fail because the routes are mapped onto <see cref="SharedHostedWebApp"/>'s inner
/// <c>WebApplication</c> whose service provider is a SEPARATE container (bug-2849).
/// </para>
/// </remarks>
public static class JournalEntryRoutes
{
    /// <summary>Canonical route base for the node-local journal-entries surface.</summary>
    public const string RouteBase = "/api/local-node/journal-entries";

    /// <summary>
    /// Stable single-device tenant sentinel (mirrors <see cref="PaymentRoutes"/> /
    /// <see cref="ChartOfAccountsRoutes"/>). The local node serves exactly one operator.

    /// <summary>
    /// Maps the journal-entry routes onto <paramref name="app"/>, closing over the
    /// <paramref name="readModel"/>, <paramref name="store"/>, and <paramref name="posting"/> from
    /// the outer host container. The write routes (create + reverse) route their entries through
    /// <paramref name="posting"/> for the six-phase posting algorithm; the read routes + the
    /// reverse-path original lookup / original-Reversed transition use <paramref name="store"/>.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IJournalEntryQueryReadModel readModel,
        NodeEfJournalStore store,
        IJournalPostingService posting,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(timeProvider);

        MapList(app, readModel, activeTeam);
        MapDetail(app, store, activeTeam);
        MapCreate(app, posting, activeTeam, timeProvider);
        MapReverse(app, store, posting, activeTeam, timeProvider);
    }

    // ── GET /api/local-node/journal-entries — paged summary list via the read-model ──
    private static void MapList(IEndpointRouteBuilder app, IJournalEntryQueryReadModel readModel, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (
            string? accountId,
            string? chartId,
            string? from,
            string? to,
            string? status,
            string? source,
            string? search,
            string? sort,
            string? dir,
            string? cursor,
            int? skip,
            int? take,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var query = new JournalEntryQuery
            {
                // TenantId is server-set, never frontend-passed (ADR 0092 §A1).
                TenantId     = LocalTenantId,
                // accountId goes INTO the read-model query (ADR 0121 §D5) so Total/HasMore/
                // NextCursor are computed over the filtered set — a route-side post-filter of
                // page.Items would report paging metadata for the unfiltered superset.
                AccountId    = string.IsNullOrWhiteSpace(accountId) ? (GLAccountId?)null : new GLAccountId(accountId),
                ChartId      = string.IsNullOrWhiteSpace(chartId) ? (ChartOfAccountsId?)null : new ChartOfAccountsId(chartId),
                FromDate     = TryDate(from),
                ToDate       = TryDate(to),
                Statuses     = ParseStatuses(status),
                SourceKinds  = ParseSources(source),
                Search       = string.IsNullOrWhiteSpace(search) ? null : search,
                SortField    = ParseSortField(sort),
                SortDirection = ParseSortDirection(dir),
                Cursor       = string.IsNullOrWhiteSpace(cursor) ? null : cursor,
                Skip         = skip,
                Take         = take ?? 50,
            };

            PagedResult<JournalEntrySummaryDto> page;
            try
            {
                page = await readModel.QueryAsync(query, ct).ConfigureAwait(false);
            }
            catch (InvalidCursorException)
            {
                // A cursor minted under a different filter set (D3 integrity guard).
                return Results.BadRequest(new { error = "invalid_cursor" });
            }

            return Results.Ok(new JournalEntryListResponse(
                Data:       page.Items.Select(JournalEntrySummaryWire.From).ToList(),
                Total:      page.Total,
                HasMore:    page.HasMore,
                NextCursor: page.NextCursor,
                Take:       page.Take));
        });
    }

    // ── GET /api/local-node/journal-entries/{id} — detail incl. lines ──
    private static void MapDetail(IEndpointRouteBuilder app, NodeEfJournalStore store, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{id}}", (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            // Snapshot is tenant-scoped by the store contract; opaque 404 on absent / other-tenant.
            var entry = store.Snapshot(LocalTenantId).FirstOrDefault(e => e.Id.Value == id);
            return entry is null
                ? Results.NotFound()
                : Results.Ok(new JournalEntryDetailResponse(JournalEntryDetailWire.From(entry)));
        });
    }

    // ── POST /api/local-node/journal-entries — manual entry (routed through the posting service) ──
    private static void MapCreate(
        IEndpointRouteBuilder app,
        IJournalPostingService posting,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost(RouteBase, async (CreateJournalEntryRequest body, HttpContext http, CancellationToken ct) =>
        {
            // Ticket 151 cluster: posting to the GL is the privileged accountant-grade action —
            // ledger:post (already in the vocabulary + the Owner/Admin compositions), decided BEFORE
            // any validation or persistence work. Same request-scoped mechanism as the sibling gates.
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var at = timeProvider.GetUtcNow();
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                LocalTenantId,
                at);
            // ONE authority for the whole act: the gate decides on the same actor and instant the posting
            // is stamped with, so the decision and the record cannot disagree about when it happened.
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.LedgerPost, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            if (body is null)
            {
                return Results.BadRequest(new { error = "request_null" });
            }

            // E8: minimum two lines (mirrors the Bridge write contract).
            if (body.Lines is null || body.Lines.Count < 2)
            {
                return Results.BadRequest(new { error = "minimum_two_lines" });
            }

            // E10: direction enum.
            if (body.Lines.Any(l => l.Direction != "Debit" && l.Direction != "Credit"))
            {
                return Results.BadRequest(new { error = "invalid_direction" });
            }

            // Positive amount per line.
            if (body.Lines.Any(l => l.Amount <= 0m))
            {
                return Results.BadRequest(new { error = "invalid_amount" });
            }

            // E6: DR/CR balance — exact decimal comparison (no float, no epsilon). Kept at the
            // route so the "imbalanced" wire error fires here (the posting service Phase 2 is the
            // defence-in-depth re-check; either way the response is the same 400 imbalanced).
            var totalDebits  = body.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
            var totalCredits = body.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
            if (totalDebits != totalCredits)
            {
                return Results.BadRequest(new
                {
                    error = "imbalanced",
                    debits = totalDebits,
                    credits = totalCredits,
                    difference = Math.Abs(totalDebits - totalCredits),
                });
            }

            // Posting date.
            if (!DateOnly.TryParse(body.PostingDate, out var postingDate))
            {
                return Results.BadRequest(new { error = "invalid_posting_date" });
            }

            // Memo.
            if (string.IsNullOrWhiteSpace(body.Memo))
            {
                return Results.BadRequest(new { error = "memo_required" });
            }

            // Build the lines. Each line is exclusively a debit or a credit; the
            // JournalEntryLine + JournalEntry constructors re-enforce the invariants.
            var lines = body.Lines
                .Select(l =>
                {
                    var (debit, credit) = l.Direction == "Debit" ? (l.Amount, 0m) : (0m, l.Amount);
                    return new JournalEntryLine(new GLAccountId(l.AccountCode), debit, credit);
                })
                .ToList()
                .AsReadOnly();

            // Build a DRAFT entry and route it through the six-phase posting service. The service
            // runs preconditions → balance → account-validity → period-gating → atomic commit and
            // promotes the draft to Posted. PostedAtUtc is stamped by the service (its TimeProvider),
            // NOT here, so there is one clock of record.
            JournalEntry draft;
            try
            {
                draft = new JournalEntry(
                    id:           JournalEntryId.NewId(),
                    tenantId:     LocalTenantId,
                    entryDate:    postingDate,
                    memo:         Truncate(body.Memo!, 200),
                    lines:        lines,
                    createdAtUtc: new Instant(at))
                {
                    ChartId     = string.IsNullOrWhiteSpace(body.ChartId) ? (ChartOfAccountsId?)null : new ChartOfAccountsId(body.ChartId),
                    Status      = JournalEntryStatus.Draft,
                    SourceKind  = JournalEntrySource.Manual,
                };
            }
            catch (ArgumentException)
            {
                // Defence-in-depth: the JournalEntry ctor re-validates the balance invariant.
                return Results.BadRequest(new { error = "imbalanced" });
            }

            PostResult result;
            try
            {
                result = await posting.PostAsync(draft, authority, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
            {
                // OBS-2: a SourceReference / id unique-constraint race against the recoverable store
                // (ux_journal_entries_tenant_source_ref) is a duplicate-submit conflict, not a server
                // error — return 409, not 500.
                return Results.Conflict(new { error = "duplicate_journal_entry" });
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return PostErrorToResult(result);
            }

            // Audit-envelope: satisfied by the durable SQLCipher store presence (durable-layer
            // pattern; no inline signed event on the node — see class remarks).
            var posted = result.Entry!;
            return Results.Created(
                $"{RouteBase}/{posted.Id.Value}",
                new JournalEntryDetailResponse(JournalEntryDetailWire.From(posted)));
        });
    }

    // ── POST /api/local-node/journal-entries/{id}/reverse — create reversing entry (posted) ──
    private static void MapReverse(
        IEndpointRouteBuilder app,
        NodeEfJournalStore store,
        IJournalPostingService posting,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/reverse", async (
            string id,
            ReverseJournalEntryRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Ticket 151 cluster: a reversal POSTS a new GL entry — the same ledger:post gate as create.
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var at = timeProvider.GetUtcNow();
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                LocalTenantId,
                at);
            // ONE authority for the whole act: the gate decides on the same actor and instant the posting
            // is stamped with, so the decision and the record cannot disagree about when it happened.
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.LedgerPost, RouteRecord.Of(id), ct) is { } denied)
                return denied;
            var original = store.Snapshot(LocalTenantId).FirstOrDefault(e => e.Id.Value == id);
            if (original is null)
            {
                return Results.NotFound();
            }

            // Only Posted entries can be reversed (CIC ruling 2026-06-03: JEs are reversed,
            // never hard-deleted; a second reverse must be blocked → no double-counted GL).
            if (original.Status != JournalEntryStatus.Posted)
            {
                return Results.BadRequest(new
                {
                    error = original.Status == JournalEntryStatus.Reversed ? "already_reversed" : "not_posted",
                });
            }

            // Reversal date — body override or today.
            DateOnly reversalDate;
            if (!string.IsNullOrWhiteSpace(body?.ReversalDate))
            {
                if (!DateOnly.TryParse(body.ReversalDate, out reversalDate))
                {
                    return Results.BadRequest(new { error = "invalid_reversal_date" });
                }
            }
            else
            {
                reversalDate = DateOnly.FromDateTime(at.UtcDateTime);
            }

            // Swap debit/credit on every line.
            var reversedLines = original.Lines
                .Select(l => new JournalEntryLine(l.AccountId, l.Credit, l.Debit))
                .ToList()
                .AsReadOnly();

            // Build a DRAFT reversing entry and route it through the posting service — so the
            // reversal is subject to the SAME account-validity + period-gating as a manual entry
            // (you cannot post a reversal into a Locked period, or against an account that no longer
            // resolves). The service promotes it to Posted + stamps PostedAtUtc.
            var memo = $"Reversal of {original.Id.Value}: {original.Memo}";
            var draftReversal = new JournalEntry(
                id:           JournalEntryId.NewId(),
                tenantId:     LocalTenantId,
                entryDate:    reversalDate,
                memo:         memo.Length > 200 ? memo[..200] : memo,
                lines:        reversedLines,
                createdAtUtc: new Instant(at))
            {
                ChartId     = original.ChartId,
                Status      = JournalEntryStatus.Draft,
                SourceKind  = JournalEntrySource.Reversal,
                ReversalOf  = original.Id,
            };

            PostResult result;
            try
            {
                result = await posting.PostAsync(draftReversal, authority, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                // The reversing entry failed validation (e.g. the original's period is now
                // Locked, or an account was deactivated). The original is left untouched —
                // a reversal that cannot post must NOT mark the original Reversed.
                return PostErrorToResult(result);
            }

            var reversalEntry = result.Entry!;

            // Transition the ORIGINAL to Reversed + set its ReversedBy FK (atomic EF update).
            // Without this the Posted-only guard never fires on a second reverse (F-89-A).
            var reversedOriginal = original with
            {
                Status     = JournalEntryStatus.Reversed,
                ReversedBy = reversalEntry.Id,
            };
            try
            {
                await store.ReplaceEntryAsync(LocalTenantId, reversedOriginal, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Created(
                $"{RouteBase}/{reversalEntry.Id.Value}",
                new JournalEntryDetailResponse(JournalEntryDetailWire.From(reversalEntry)));
        });
    }

    // ── Posting-result → HTTP response mapping ───────────────────────────────────
    //
    // Maps the structured PostError from JournalPostingService.PostAsync onto the node JE
    // wire error codes. Phase-1/2 errors (NotADraft / TooFewLines / Imbalanced) are already
    // guarded at the route before the entry is built — they are mapped here defensively, but
    // in practice the route's own checks fire first. Phase-3/4 errors (account-validity +
    // period-gating) are the new gates this reroute adds; they surface as 400 with a
    // machine-readable error code + the diagnostic detail the service supplied.
    private static IResult PostErrorToResult(PostResult result) => result.Error switch
    {
        PostError.Imbalanced        => Results.BadRequest(new { error = "imbalanced", detail = result.Detail }),
        PostError.TooFewLines       => Results.BadRequest(new { error = "minimum_two_lines", detail = result.Detail }),
        PostError.NotADraft         => Results.BadRequest(new { error = "not_a_draft", detail = result.Detail }),
        PostError.UnknownAccount    => Results.BadRequest(new { error = "unknown_account", detail = result.Detail }),
        PostError.WrongChart        => Results.BadRequest(new { error = "wrong_chart", detail = result.Detail }),
        PostError.AccountNotPostable => Results.BadRequest(new { error = "account_not_postable", detail = result.Detail }),
        PostError.CurrencyMismatch  => Results.BadRequest(new { error = "currency_mismatch", detail = result.Detail }),
        PostError.NoPeriodForDate   => Results.BadRequest(new { error = "no_period_for_date", detail = result.Detail }),
        PostError.PeriodLocked      => Results.BadRequest(new { error = "period_locked", detail = result.Detail }),
        PostError.PeriodSoftClosed  => Results.BadRequest(new { error = "period_soft_closed", detail = result.Detail }),
        // PostError.None is the success case (handled before this is called); any unmapped
        // future error code falls through to a generic 400 rather than leaking a 500.
        _                           => Results.BadRequest(new { error = "posting_rejected", detail = result.Detail }),
    };

    // ── Query-string parsing helpers ────────────────────────────────────────────

    private static DateOnly? TryDate(string? raw) =>
        DateOnly.TryParse(raw, out var d) ? d : null;

    private static IReadOnlyList<JournalEntryStatus>? ParseStatuses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<JournalEntryStatus>(s, ignoreCase: true, out var v) ? (JournalEntryStatus?)v : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();
        return parsed.Count > 0 ? parsed : null;
    }

    private static IReadOnlyList<JournalEntrySource>? ParseSources(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<JournalEntrySource>(s, ignoreCase: true, out var v) ? (JournalEntrySource?)v : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();
        return parsed.Count > 0 ? parsed : null;
    }

    private static JournalEntrySortField ParseSortField(string? raw) =>
        Enum.TryParse<JournalEntrySortField>(raw, ignoreCase: true, out var v) ? v : JournalEntrySortField.EntryDate;

    private static JournalEntrySortDirection ParseSortDirection(string? raw) =>
        string.Equals(raw, "asc", StringComparison.OrdinalIgnoreCase)
            ? JournalEntrySortDirection.Ascending
            : JournalEntrySortDirection.Descending;

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>Summary row in the node JE list response (camelCase JSON; mirrors the Bridge wire).</summary>
public sealed record JournalEntrySummaryWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entryDate")] string EntryDate,
    [property: JsonPropertyName("memo")] string? Memo,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("lineCount")] int LineCount,
    [property: JsonPropertyName("totalDebits")] double TotalDebits,
    [property: JsonPropertyName("totalCredits")] double TotalCredits,
    [property: JsonPropertyName("reversalOf")] string? ReversalOf,
    [property: JsonPropertyName("reversedBy")] string? ReversedBy,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("accountIds")] IReadOnlyList<string> AccountIds)
{
    /// <summary>Projects the read-model summary DTO onto the node wire shape.</summary>
    public static JournalEntrySummaryWire From(JournalEntrySummaryDto d) => new(
        d.Id, d.EntryDate, d.Memo, d.Status, d.SourceKind, d.LineCount,
        d.TotalDebits, d.TotalCredits, d.ReversalOf, d.ReversedBy, d.CreatedAt, d.AccountIds);
}

/// <summary>Detail wire shape incl. lines (mirrors the Bridge JournalEntryDetailDto).</summary>
public sealed record JournalEntryDetailWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entryDate")] string EntryDate,
    [property: JsonPropertyName("memo")] string Memo,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("totalDebits")] double TotalDebits,
    [property: JsonPropertyName("totalCredits")] double TotalCredits,
    [property: JsonPropertyName("reversalOf")] string? ReversalOf,
    [property: JsonPropertyName("reversedBy")] string? ReversedBy,
    [property: JsonPropertyName("postedAt")] string? PostedAt,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("chartId")] string? ChartId,
    [property: JsonPropertyName("accountIds")] IReadOnlyList<string> AccountIds,
    [property: JsonPropertyName("lines")] IReadOnlyList<JournalEntryLineWire> Lines)
{
    /// <summary>Projects a domain <see cref="JournalEntry"/> onto the detail wire shape.</summary>
    public static JournalEntryDetailWire From(JournalEntry e) => new(
        Id:           e.Id.Value,
        EntryDate:    e.EntryDate.ToString("O"),
        Memo:         e.Memo,
        Status:       e.Status.ToString(),
        SourceKind:   e.SourceKind.ToString(),
        TotalDebits:  (double)e.Lines.Sum(l => l.Debit),
        TotalCredits: (double)e.Lines.Sum(l => l.Credit),
        ReversalOf:   e.ReversalOf?.Value,
        ReversedBy:   e.ReversedBy?.Value,
        PostedAt:     e.PostedAtUtc?.Value.ToString("O"),
        CreatedAt:    e.CreatedAtUtc.Value.ToString("O"),
        ChartId:      e.ChartId?.Value,
        AccountIds:   e.Lines.Select(l => l.AccountId.Value).Distinct().ToList(),
        Lines:        e.Lines.Select(l => new JournalEntryLineWire(
            l.AccountId.Value, (double)l.Debit, (double)l.Credit)).ToList());
}

/// <summary>One line in a JE detail response.</summary>
public sealed record JournalEntryLineWire(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("debit")] double Debit,
    [property: JsonPropertyName("credit")] double Credit);

/// <summary>
/// List response envelope: <c>{ "data": [...], "total", "hasMore", "nextCursor", "take" }</c>.
/// Carries the paging metadata the ADR 0121 server-backed grid consumes (DataGrid
/// <c>{data,total}</c> seam + keyset cursor).
/// </summary>
public sealed record JournalEntryListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<JournalEntrySummaryWire> Data,
    [property: JsonPropertyName("total")] int? Total,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("take")] int Take);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record JournalEntryDetailResponse(
    [property: JsonPropertyName("data")] JournalEntryDetailWire Data);

/// <summary>POST body for <c>POST /api/local-node/journal-entries</c> (manual entry).</summary>
public sealed record CreateJournalEntryRequest(
    [property: JsonPropertyName("postingDate")] string PostingDate,
    [property: JsonPropertyName("memo")] string Memo,
    [property: JsonPropertyName("chartId")] string? ChartId,
    [property: JsonPropertyName("lines")] IReadOnlyList<CreateJournalEntryLine> Lines);

/// <summary>One debit/credit line in a manual JE create request.</summary>
public sealed record CreateJournalEntryLine(
    [property: JsonPropertyName("accountCode")] string AccountCode,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("direction")] string Direction);

/// <summary>POST body for <c>POST /api/local-node/journal-entries/{id}/reverse</c> (all optional).</summary>
public sealed record ReverseJournalEntryRequest(
    [property: JsonPropertyName("reversalDate")] string? ReversalDate,
    [property: JsonPropertyName("memo")] string? Memo);
