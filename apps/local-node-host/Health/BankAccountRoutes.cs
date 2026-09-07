using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Banking.Feed;
using Harborline.Api.Blocks.Banking.Import;
using Harborline.Api.Blocks.Banking.Matching;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Financial;
using BankingServices = (
    Harborline.Api.Blocks.Banking.Services.IBankAccountRepository AccountRepo,
    Harborline.Api.Blocks.Banking.Services.IStatementLineRepository LineRepo,
    Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository LinkRepo,
    Harborline.Api.Blocks.Banking.Services.IReconciliationRepository ReconciliationRepo,
    Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository PeriodRepo,
    Harborline.Api.Blocks.Banking.Import.ImportPipelineService ImportPipeline,
    Harborline.Api.Blocks.Banking.Matching.AcceptMatchService AcceptMatchService,
    Harborline.Api.Blocks.Banking.Matching.UnMatchService UnMatchService,
    Harborline.Api.Blocks.Banking.Matching.ReconciliationLockLease ReconciliationLease,
    Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider FeedProvider,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Harborline.Api.LocalNodeHost.Data.Banking.NodeLocalBankFeedDbContext> FeedConnectionFactory);

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local banking surface — the T3 local-first sweep (ADR 0113 ABSOLUTE local-first). Drives
/// <c>BankAccountsPage</c> / <c>BankAccountDetailPage</c> fully offline: a single-device install
/// (signal-bridge STOPPED) lists/creates/archives bank accounts, imports statements
/// (CSV/OFX/QIF/CAMT.053), reviews + accepts/un-matches proposals, locks/unlocks reconciliation,
/// and runs the mock bank feed — all against the recoverable keyed (SQLCipher) <c>local-node.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns banking data.</b> The four banking tables (mapped by <c>BankingEntityModule</c>
/// into <see cref="Data.LocalNodeDbContext"/>) already exist in the node <c>InitialSchema</c> migration
/// — this flip adds NO new schema. Reads + writes go through the Node EF banking repos +
/// <see cref="ImportPipelineService"/> / <see cref="AcceptMatchService"/> / <see cref="UnMatchService"/>,
/// all over <c>local-node.db</c> (the C1-durable source of truth), so banking no longer needs
/// signal-bridge.
/// </para>
/// <para>
/// <b>Wire shape == the FRONTEND consumer contract</b> (the <c>banking.ts</c> interfaces the pages
/// actually read: <c>id</c> / <c>accountNumberMask</c> / <c>currentBalance</c> / <c>archivedAt</c> /
/// <c>currencyCode</c> / <c>feedConnected</c> / <c>lastImportAt</c> / <c>transactionDate</c> /
/// <c>reconciliationState</c> / <c>importedCount</c> / <c>skippedCount</c> / <c>linesAdded</c> /
/// <c>proposals[]</c>). The legacy Bridge DTOs used different field names (AccountId/IsArchived/…), so
/// the node serves the consumer-true shape and the frontend client repoints IN PLACE with no field
/// remapping. <c>currentBalance</c> is computed node-side (opening balance + Σ statement-line amounts);
/// <c>accountNumberMask</c> is stored in <see cref="BankAccount.InstitutionName"/>'s sibling — the
/// domain has no mask field, so a derived "last-4 of display" placeholder is served (v1; a dedicated
/// mask column is a follow-on if the UX demands it).
/// </para>
/// <para>
/// <b>ReconciliationState mapping.</b> The frontend pill only knows
/// <c>Unmatched|Proposed|Matched|Reconciled</c>; the domain adds <c>PartiallyMatched|Excluded</c>.
/// Mapping: <c>PartiallyMatched → Proposed</c> (in-progress), <c>Excluded → Unmatched</c>,
/// <c>Matched → Matched</c> so the page never renders an undefined pill.
/// </para>
/// <para>
/// <b>Tenant + actor scoping.</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), not a
/// fixed <c>"local"</c> sentinel. Reconciliation holders resolve through
/// <see cref="NodeCallerParty"/>: the selected-session member on the web plane and the install
/// operator only on the desktop/bootstrap fallback path.
/// Opaque 404 for a missing/cross-tenant account — the per-org isolation boundary.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only Kestrel listener (the Bridge CSRF +
/// AuthenticatedTenantPolicy posture does not apply on the node).
/// </para>
/// <para>
/// <b>Wiring.</b> Every banking dep is injected from the OUTER host container via
/// <see cref="BankingServices"/> and passed to <see cref="Map"/> as a closed-over dependency —
/// NOT resolved via <c>[FromServices]</c> (bug-2849).
/// </para>
/// </remarks>
public static class BankAccountRoutes
{
    /// <summary>Canonical route base for the node-local banking surface.</summary>
    public const string RouteBase = "/api/local-node/bank-accounts";

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB (mirrors the Bridge cap)
    private const int MaxFileNameLen = 255;

    private static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".ofx", ".qfx", ".qif", ".xml" };

    /// <summary>Maps the banking routes onto <paramref name="app"/>, closing over the banking accessor.</summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        BankingServices banking,
        IActiveTeamAccessor activeTeam,
        NodeBankAccountWriter writer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapListAccounts(app, banking, activeTeam);
        MapGetAccount(app, banking, activeTeam);
        MapCreateAccount(app, banking, activeTeam, writer, timeProvider);
        MapArchiveAccount(app, banking, activeTeam, writer, timeProvider);
        MapSetOpeningBalance(app, banking, activeTeam, writer, timeProvider);
        MapImportStatement(app, banking, activeTeam);
        MapListStatementLines(app, banking, activeTeam);
        MapListMatchProposals(app, banking, activeTeam);
        MapAcceptMatch(app, banking, activeTeam);
        MapUnMatch(app, banking, activeTeam);
        MapGetReconciliationState(app, banking, activeTeam);
        MapLockReconciliation(app, banking, activeTeam);
        MapUnlockReconciliation(app, banking, activeTeam);
        MapFeedConnect(app, banking, activeTeam, timeProvider);
        MapFeedPull(app, banking, activeTeam, timeProvider);
        MapFeedDisconnect(app, banking, activeTeam);
    }

    // ── GET /api/local-node/bank-accounts ──────────────────────────────────────
    private static void MapListAccounts(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (bool? includeArchived, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var accounts = await b.AccountRepo.ListAsync(LocalTenantId, includeArchived ?? false, ct).ConfigureAwait(false);
            var items = new List<BankAccountSummaryWire>(accounts.Count);
            foreach (var a in accounts)
            {
                var balance = await ComputeCurrentBalanceAsync(b, a, LocalTenantId, ct).ConfigureAwait(false);
                items.Add(ToSummary(a, balance));
            }
            return Results.Ok(new BankAccountListWire(items.ToArray(), items.Count));
        });
    }

    // ── GET /api/local-node/bank-accounts/{accountId} ──────────────────────────
    private static void MapGetAccount(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{accountId}}", async (string accountId, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            var lines = await b.LineRepo.ListByAccountAsync(LocalTenantId, account.Id, ct).ConfigureAwait(false);
            var balance = account.OpeningBalance + lines.Sum(l => l.Amount);
            string? lastImportAt = lines.Count == 0
                ? null
                : lines.Max(l => l.CreatedAtUtc).Value.ToString("O");

            var feedConnected = await IsFeedConnectedAsync(b, accountId, ct).ConfigureAwait(false);
            return Results.Ok(ToDetail(account, balance, lastImportAt, feedConnected));
        });
    }

    // ── POST /api/local-node/bank-accounts ─────────────────────────────────────
    private static void MapCreateAccount(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam, NodeBankAccountWriter writer, TimeProvider timeProvider)
    {
        app.MapPost(RouteBase, async (CreateBankAccountBody body, HttpContext http, CancellationToken ct) =>
        {
            // Ticket 151 stage-one gate: a PERMISSION decision, not just the transport fence — the same
            // request-scoped mechanism the gated sibling routes use (ContactRoutes / InvoiceRoutes).
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsWrite, RouteRecord.TheInstall, ct) is { } denied)
                return denied;

            if (body is null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "display_name_required" });

            var id = BankAccountId.NewId();
            var at = timeProvider.GetUtcNow();
            var now = (Instant)at;
            var account = new BankAccount(
                Id:                  id,
                TenantId:            LocalTenantId,
                EntityId:            EntityId.Parse($"banking:tenants/{LocalTenantId.Value}/accounts/{Guid.NewGuid():N}"),
                Kind:                ParseKind(body.Kind),
                DisplayName:         body.DisplayName.Trim(),
                InstitutionName:     string.IsNullOrWhiteSpace(body.InstitutionName) ? null : body.InstitutionName.Trim(),
                Currency:            new CurrencyCode(string.IsNullOrWhiteSpace(body.CurrencyCode) ? "USD" : body.CurrencyCode.Trim().ToUpperInvariant()),
                LinkedLedgerAccount: new LedgerAccountRef(
                    GLAccountId: new GLAccountId(body.LinkedLedgerAccountId ?? string.Empty),
                    ChartId:     default),
                OpeningBalance:      0m,
                CutoverAsOf:         now,
                ArchivedAt:          null,
                CreatedAtUtc:        now,
                UpdatedAtUtc:        now);

            var authority = Authority(http, LocalTenantId, id, at);
            account = await writer.CreateAsync(account, authority, ct).ConfigureAwait(false);
            // Newly-created account: no feed connected yet (no row in bank_feed_connections).
            return Results.Created($"{RouteBase}/{account.Id.Value}", ToDetail(account, account.OpeningBalance, null, feedConnected: false));
        });
    }

    // ── POST /api/local-node/bank-accounts/{accountId}/archive ─────────────────
    private static void MapArchiveAccount(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam, NodeBankAccountWriter writer, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/archive", async (string accountId, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsWrite, RouteRecord.Of(accountId), ct) is { } denied)
                return denied;
            var id = new BankAccountId(accountId);
            var at = timeProvider.GetUtcNow();
            var archived = await writer.ArchiveAsync(id, Authority(http, LocalTenantId, id, at), ct).ConfigureAwait(false);
            if (archived is null) return Results.NotFound();
            var feedConnectedOnArchive = await IsFeedConnectedAsync(b, accountId, ct).ConfigureAwait(false);
            return Results.Ok(ToDetail(archived, archived.OpeningBalance, null, feedConnectedOnArchive));
        });
    }

    // ── POST /api/local-node/bank-accounts/{accountId}/opening-balance ─────────
    private static void MapSetOpeningBalance(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam, NodeBankAccountWriter writer, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/opening-balance", async (string accountId, SetOpeningBalanceBody body, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, LocalTenantId, TeamRolePermissions.RecordsWrite, RouteRecord.Of(accountId), ct) is { } denied)
                return denied;
            if (body is null) return Results.BadRequest(new { error = "body_required" });
            if (!TryParseInstant(body.OpeningBalanceDate, out var cutover))
                return Results.BadRequest(new { error = "invalid_date", detail = "openingBalanceDate must be ISO-8601." });

            var id = new BankAccountId(accountId);
            var at = timeProvider.GetUtcNow();
            var updated = await writer.SetOpeningBalanceAsync(
                id, body.OpeningBalance, cutover, Authority(http, LocalTenantId, id, at), ct).ConfigureAwait(false);
            if (updated is null) return Results.NotFound();
            var lines = await b.LineRepo.ListByAccountAsync(LocalTenantId, updated.Id, ct).ConfigureAwait(false);
            var feedConnectedOnBalanceSet = await IsFeedConnectedAsync(b, accountId, ct).ConfigureAwait(false);
            return Results.Ok(ToDetail(updated, updated.OpeningBalance + lines.Sum(l => l.Amount), null, feedConnectedOnBalanceSet));
        });
    }

    private static AuthorizationWriteContext Authority(
        HttpContext http,
        TenantId tenant,
        BankAccountId account,
        DateTimeOffset at)
    {
        return new AuthorizationWriteContext(
            new ActorId(NodeCallerParty.Resolve(http).Value),
            tenant,
            at);
    }

    // ── POST /api/local-node/bank-accounts/{accountId}/import-statement ────────
    private static void MapImportStatement(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/import-statement", async (string accountId, HttpRequest request, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart_required" });

            IFormFile? file;
            try
            {
                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                file = form.Files.GetFile("file");
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "malformed_multipart" });
            }

            if (file is null) return Results.BadRequest(new { error = "file_required" });
            if (file.Length > MaxUploadBytes) return Results.BadRequest(new { error = "file_too_large" });

            var safeName = Path.GetFileName(file.FileName ?? string.Empty); // strips ../ path components
            if (string.IsNullOrWhiteSpace(safeName) || safeName.Length > MaxFileNameLen)
                return Results.BadRequest(new { error = "invalid_file_name" });
            if (!AllowedExtensions.Contains(Path.GetExtension(safeName)))
                return Results.BadRequest(new { error = "unsupported_format" });

            var batchId = Guid.NewGuid().ToString("N");
            ImportBatchResult result;
            try
            {
                await using var stream = file.OpenReadStream();
                result = await b.ImportPipeline.ImportFileAsync(LocalTenantId, account.Id, stream, safeName, batchId, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No parser handles", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "unsupported_format", detail = "No parser recognized the file." });
            }
            catch (StatementParseException ex)
            {
                return Results.BadRequest(new { error = "parse_failed", detail = ex.Message });
            }

            // Frontend ImportBatchResult shape: { importedCount, skippedCount, errors }
            return Results.Ok(new ImportBatchResultWire(result.Inserted, result.Skipped, Array.Empty<string>()));
        });
    }

    // ── GET /api/local-node/bank-accounts/{accountId}/statement-lines ──────────
    private static void MapListStatementLines(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{accountId}}/statement-lines", async (string accountId, string? state, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            var lines = await b.LineRepo.ListByAccountAsync(LocalTenantId, account.Id, ct).ConfigureAwait(false);
            // Node ORDER BY on an Instant column is unsupported (SQLite/DateTimeOffset) — order in memory.
            var ordered = lines.OrderByDescending(l => l.PostedAt.Value).ToList();
            var items = ordered.Select(ToStatementLine).ToArray();
            return Results.Ok(new StatementLineListWire(items, items.Length));
        });
    }

    // ── GET /api/local-node/bank-accounts/{accountId}/match-proposals ──────────
    private static void MapListMatchProposals(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{accountId}}/match-proposals", async (string accountId, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            var lines = await b.LineRepo.ListByAccountAsync(LocalTenantId, account.Id, ct).ConfigureAwait(false);
            var proposals = new List<MatchProposalWire>();
            foreach (var line in lines.Where(l => l.State == ReconciliationState.Proposed))
            {
                var links = await b.LinkRepo.ListByStatementLineAsync(LocalTenantId, line.Id, ct).ConfigureAwait(false);
                foreach (var link in links.Where(l => l.State == MatchLinkState.Proposed))
                    proposals.Add(ToMatchProposal(line, link));
            }
            return Results.Ok(new MatchProposalListWire(proposals.ToArray(), proposals.Count));
        });
    }

    // ── POST /api/local-node/bank-accounts/{accountId}/accept-match ────────────
    private static void MapAcceptMatch(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/accept-match", async (string accountId, MatchLinkBody body, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (body is null || string.IsNullOrWhiteSpace(body.MatchLinkId))
                return Results.BadRequest(new { error = "match_link_id_required" });

            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            var matchLinkId = new MatchLinkId(body.MatchLinkId);
            var link = await b.LinkRepo.GetByIdAsync(LocalTenantId, matchLinkId, ct).ConfigureAwait(false);
            if (link is null) return Results.BadRequest(new { error = "match_link_not_found" });

            var statementLine = await b.LineRepo.GetByIdAsync(LocalTenantId, link.StatementLine, ct).ConfigureAwait(false);
            if (statementLine is null) return Results.BadRequest(new { error = "statement_line_not_found" });

            // Server-derive the covering fiscal period from the line's PostedAt (never trust the
            // client) so the AcceptMatchService Locked + bank-rec-lock gates run against the real
            // period. If no period covers the date, the accept proceeds with no fiscal gate (single-
            // device tenants may not have opened a period yet — the bank-rec lock still applies).
            var postedDate = DateOnly.FromDateTime(statementLine.PostedAt.Value.UtcDateTime);
            FiscalPeriod? period = await b.PeriodRepo
                .FindByChartAndDateAsync(account.LinkedLedgerAccount.ChartId, postedDate, ct).ConfigureAwait(false);

            try
            {
                await b.AcceptMatchService.AcceptAsync(LocalTenantId, matchLinkId, period?.Id, ct).ConfigureAwait(false);
            }
            catch (MatchAcceptException ex)
            {
                return Results.BadRequest(new { error = "accept_rejected", reason = ex.Reason.ToString(), detail = ex.Message });
            }

            return Results.Ok(new MatchResultWire(body.MatchLinkId, "Accepted"));
        });
    }

    // ── POST /api/local-node/bank-accounts/{accountId}/un-match ────────────────
    private static void MapUnMatch(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/un-match", async (
            string accountId, MatchLinkBody body, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (body is null || string.IsNullOrWhiteSpace(body.MatchLinkId))
                return Results.BadRequest(new { error = "match_link_id_required" });

            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            try
            {
                await b.UnMatchService.UnMatchAsync(
                    LocalTenantId,
                    new MatchLinkId(body.MatchLinkId),
                    ct).ConfigureAwait(false);
            }
            catch (UnMatchException ex)
            {
                return Results.BadRequest(new { error = "unmatch_rejected", detail = ex.Message });
            }

            return Results.Ok(new MatchResultWire(body.MatchLinkId, "Unmatched"));
        });
    }

    // ── GET /api/local-node/bank-accounts/{accountId}/reconciliation-state ─────
    private static void MapGetReconciliationState(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{accountId}}/reconciliation-state", async (string accountId, string? periodId, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            Reconciliation? rec = null;
            if (!string.IsNullOrWhiteSpace(periodId))
                rec = await b.ReconciliationRepo.GetByAccountPeriodAsync(LocalTenantId, account.Id, new FiscalPeriodId(periodId), ct).ConfigureAwait(false);

            // 3421 — report the EFFECTIVE lock, not the persisted column. Every mutation guard
            // (this route's own transition, accept-match and un-match) now asks the lease, so a row
            // whose lease has expired, or a migrated row carrying no timestamp, is not held and the
            // server will let the next caller take it. Serializing the raw column here would tell the
            // client "Locked" about a reconciliation the server would happily hand over — leaving the
            // human visibly wedged on exactly the stale lock this card exists to release, which is the
            // wedge as the user experiences it. The wire shape is unchanged.
            return Results.Ok(new ReconciliationStateWire(
                AccountId:               accountId,
                PeriodId:                periodId,
                OpeningBalance:          (double)(rec?.OpeningBalance ?? account.OpeningBalance),
                StatementClosingBalance: (double)(rec?.StatementClosingBalance ?? 0m),
                ClearedMovement:         (double)(rec?.ClearedMovement ?? 0m),
                IsBalanced:              rec?.IsBalanced ?? false,
                LockState:               ProjectEffectiveLockState(rec, b.ReconciliationLease)));
        });
    }

    /// <summary>
    /// Projects the lock a caller should ACT on, which is the lease and not the stored column.
    /// </summary>
    /// <remarks>
    /// Extracted so it can be proven rather than asserted. An expired lease and a locked row with no
    /// timestamp both report <c>Open</c>, matching what the transition route, accept-match and un-match
    /// will actually allow.
    /// </remarks>
    internal static string ProjectEffectiveLockState(Reconciliation? rec, ReconciliationLockLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (rec is null) return "Unlocked";
        return lease.IsHeld(rec)
            ? BankReconciliationLockState.Locked.ToString()
            : BankReconciliationLockState.Open.ToString();
    }

    // ── POST .../reconciliation-state/lock ─────────────────────────────────────
    private static void MapLockReconciliation(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/reconciliation-state/lock", (
            HttpContext http, string accountId, PeriodBody body, CancellationToken ct) =>
            SetReconciliationLockAsync(
                b,
                accountId,
                body,
                BankReconciliationLockState.Locked,
                NodeCallerParty.Resolve(http),
                NodeTenant.Resolve(activeTeam),
                ct));
    }

    // ── POST .../reconciliation-state/unlock ───────────────────────────────────
    private static void MapUnlockReconciliation(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/reconciliation-state/unlock", (
            HttpContext http, string accountId, PeriodBody body, CancellationToken ct) =>
            SetReconciliationLockAsync(
                b,
                accountId,
                body,
                BankReconciliationLockState.Open,
                NodeCallerParty.Resolve(http),
                NodeTenant.Resolve(activeTeam),
                ct));
    }

    private static async Task<IResult> SetReconciliationLockAsync(
        BankingServices b,
        string accountId,
        PeriodBody? body,
        BankReconciliationLockState target,
        PartyId callerParty,
        TenantId LocalTenantId,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.PeriodId))
            return Results.BadRequest(new { error = "period_id_required" });

        var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
        if (account is null) return Results.NotFound();

        var periodId = new FiscalPeriodId(body.PeriodId);
        var now = b.ReconciliationLease.Now;
        var rec = await b.ReconciliationRepo.GetByAccountPeriodAsync(LocalTenantId, account.Id, periodId, ct).ConfigureAwait(false);

        if (rec is null)
        {
            // Reverse-not-delete + create-on-first-lock: an un-started reconciliation begins Open;
            // locking it materializes the aggregate. Balances seed from the account opening balance +
            // cleared statement movement so the lock reflects the current node state.
            var lines = await b.LineRepo.ListByAccountAsync(LocalTenantId, account.Id, ct).ConfigureAwait(false);
            var cleared = lines.Where(l => l.State == ReconciliationState.Matched).Sum(l => l.Amount);
            rec = new Reconciliation(
                Id:                      ReconciliationId.NewId(),
                TenantId:                LocalTenantId,
                AccountId:               account.Id,
                PeriodId:                periodId,
                OpeningBalance:          account.OpeningBalance,
                StatementClosingBalance: account.OpeningBalance + cleared,
                ClearedMovement:         cleared,
                LockState:               target,
                LockedAt:                target == BankReconciliationLockState.Locked ? now : null,
                LockedByPrincipalId:     target == BankReconciliationLockState.Locked ? callerParty.Value : null,
                CreatedAtUtc:            now,
                UpdatedAtUtc:            now);
            try
            {
                await b.ReconciliationRepo.AddAsync(rec, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
            {
                return Results.Conflict(new
                {
                    error = "reconciliation_lock_transition_lost_race",
                });
            }
        }
        else
        {
            if (b.ReconciliationLease.IsHeld(rec)
                && !string.Equals(rec.LockedByPrincipalId, callerParty.Value, StringComparison.Ordinal))
            {
                return Results.Conflict(new
                {
                    error = "reconciliation_lock_held_by_another_member",
                });
            }

            var updated = rec with
            {
                LockState           = target,
                LockedAt            = target == BankReconciliationLockState.Locked ? now : null,
                LockedByPrincipalId = target == BankReconciliationLockState.Locked ? callerParty.Value : null,
                UpdatedAtUtc        = now,
                Version             = rec.Version + 1,
            };
            if (!await b.ReconciliationRepo.UpdateAsync(updated, ct).ConfigureAwait(false))
            {
                return Results.Conflict(new
                {
                    error = "reconciliation_lock_transition_lost_race",
                });
            }
        }

        return Results.Ok();
    }

    // ── POST .../feed/connect ──────────────────────────────────────────────────
    private static void MapFeedConnect(
        IEndpointRouteBuilder app,
        BankingServices b,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/feed/connect", async (string accountId, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            var start = await b.FeedProvider.BeginConnectAsync(ct).ConfigureAwait(false);
            var (url, claimUrl) = start switch
            {
                ConnectionStart.RedirectFlow r   => (r.Url, (string?)null),
                ConnectionStart.TokenClaimFlow t => ((string?)null, t.ClaimUrl),
                _                                => ((string?)null, (string?)null),
            };

            // Persist the feed-connection so FeedConnected=true survives process restarts.
            // For the mock provider the "connection" is always successful (BeginConnect
            // returns a URL but the mock never actually redirects); we record the row here
            // so the connect→pull→disconnect round-trip works correctly in the UI.
            await PersistFeedConnectedAsync(b, accountId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);

            return Results.Ok(new FeedConnectionStartWire(url, claimUrl));
        });
    }

    // ── POST .../feed/pull ─────────────────────────────────────────────────────
    private static void MapFeedPull(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/feed/pull", async (string accountId, string? cursor, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            // Gate pull behind connected state — the UI only renders the Pull button when
            // FeedConnected=true, but the route enforces it server-side as well.
            if (!await IsFeedConnectedAsync(b, accountId, ct).ConfigureAwait(false))
                return Results.BadRequest(new { error = "feed_not_connected" });

            var connection = await b.FeedProvider.CompleteConnectAsync("mock-claim", ct).ConfigureAwait(false);
            var feedAccounts = await b.FeedProvider.ListAccountsAsync(connection, ct).ConfigureAwait(false);
            var feedAccount = feedAccounts.FirstOrDefault();
            if (feedAccount is null) return Results.Ok(new FeedPullResultWire(0));

            var page = await b.FeedProvider.PullTransactionsAsync(connection, feedAccount, cursor, ct).ConfigureAwait(false);
            var admittedAt = new Instant(timeProvider.GetUtcNow());

            // Persist the pulled transactions as statement lines (dedup on provider-txn id).
            var added = 0;
            var ordinal = 0;
            foreach (var txn in page.Transactions)
            {
                var existing = txn.ProviderTxnId is null
                    ? null
                    : await b.LineRepo.FindByProviderTxnIdAsync(LocalTenantId, account.Id, txn.ProviderTxnId, ct).ConfigureAwait(false);
                if (existing is not null) { ordinal++; continue; }

                var line = new StatementLine(
                    Id:             StatementLineId.NewId(),
                    TenantId:       LocalTenantId,
                    AccountId:      account.Id,
                    ProviderTxnId:  txn.ProviderTxnId,
                    PostedAt:       txn.PostedAt,
                    Amount:         txn.Amount,
                    Currency:       txn.Currency,
                    Description:    txn.Description,
                    Pending:        txn.Pending,
                    State:          ReconciliationState.Unmatched,
                    Source:         new ImportSourceRef(ImportSourceKind.LiveFeed, connection.ConnectionId, ordinal++),
                    RawProviderBlob: txn.Raw,
                    CreatedAtUtc:   admittedAt);
                await b.LineRepo.AddAsync(line, ct).ConfigureAwait(false);
                added++;
            }

            return Results.Ok(new FeedPullResultWire(added));
        });
    }

    // ── POST .../feed/disconnect ───────────────────────────────────────────────
    private static void MapFeedDisconnect(IEndpointRouteBuilder app, BankingServices b, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{accountId}}/feed/disconnect", async (string accountId, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var account = await b.AccountRepo.GetByIdAsync(LocalTenantId, new BankAccountId(accountId), ct).ConfigureAwait(false);
            if (account is null) return Results.NotFound();

            var connection = await b.FeedProvider.CompleteConnectAsync("mock-claim", ct).ConfigureAwait(false);
            await b.FeedProvider.DisconnectAsync(connection, ct).ConfigureAwait(false);

            // Remove the feed-connection row so FeedConnected flips back to false.
            await RemoveFeedConnectedAsync(b, accountId, ct).ConfigureAwait(false);

            return Results.Ok();
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task<decimal> ComputeCurrentBalanceAsync(BankingServices b, BankAccount a, TenantId LocalTenantId, CancellationToken ct)
    {
        var lines = await b.LineRepo.ListByAccountAsync(LocalTenantId, a.Id, ct).ConfigureAwait(false);
        return a.OpeningBalance + lines.Sum(l => l.Amount);
    }

    /// <summary>
    /// Returns <c>true</c> when a <see cref="BankFeedConnectionRecord"/> row exists for
    /// <paramref name="accountId"/> in <c>bank_feed_connections</c>; <c>false</c> otherwise.
    /// </summary>
    private static async Task<bool> IsFeedConnectedAsync(BankingServices b, string accountId, CancellationToken ct)
    {
        await using var ctx = await b.FeedConnectionFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.BankFeedConnections
            .AnyAsync(r => r.AccountId == accountId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts a <see cref="BankFeedConnectionRecord"/> row for <paramref name="accountId"/>
    /// (idempotent: a second connect call on an already-connected account is a no-op on the row).
    /// </summary>
    private static async Task PersistFeedConnectedAsync(
        BankingServices b,
        string accountId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using var ctx = await b.FeedConnectionFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await ctx.BankFeedConnections
            .FirstOrDefaultAsync(r => r.AccountId == accountId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            ctx.BankFeedConnections.Add(new BankFeedConnectionRecord
            {
                AccountId   = accountId,
                ConnectedAt = at,
            });
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the <see cref="BankFeedConnectionRecord"/> row for <paramref name="accountId"/>
    /// if one exists (idempotent: disconnect on an already-disconnected account is a no-op).
    /// </summary>
    private static async Task RemoveFeedConnectedAsync(BankingServices b, string accountId, CancellationToken ct)
    {
        await using var ctx = await b.FeedConnectionFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await ctx.BankFeedConnections
            .FirstOrDefaultAsync(r => r.AccountId == accountId, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ctx.BankFeedConnections.Remove(existing);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static BankAccountKind ParseKind(string? raw) =>
        Enum.TryParse<BankAccountKind>(raw, ignoreCase: true, out var k) ? k : BankAccountKind.Bank;

    private static bool TryParseInstant(string? iso, out Instant result)
    {
        if (!string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, out var dto))
        {
            result = (Instant)dto;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(iso) && DateOnly.TryParse(iso, out var d))
        {
            result = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>Maps domain ReconciliationState → the 4-value frontend pill vocabulary.</summary>
    private static string ToFrontendState(ReconciliationState s) => s switch
    {
        ReconciliationState.Matched          => "Matched",
        ReconciliationState.Proposed         => "Proposed",
        ReconciliationState.PartiallyMatched => "Proposed",
        ReconciliationState.Excluded         => "Unmatched",
        _                                    => "Unmatched",
    };

    private static BankAccountSummaryWire ToSummary(BankAccount a, decimal currentBalance) => new(
        Id:                  a.Id.Value,
        TenantId:            a.TenantId.Value,
        DisplayName:         a.DisplayName,
        InstitutionName:     a.InstitutionName ?? string.Empty,
        AccountNumberMask:   Mask(a.DisplayName),
        CurrencyCode:        a.Currency.Iso4217,
        LinkedLedgerAccountId: a.LinkedLedgerAccount.GLAccountId.Value,
        CurrentBalance:      (double)currentBalance,
        OpeningBalance:      (double)a.OpeningBalance,
        OpeningBalanceDate:  a.CutoverAsOf.Value.ToString("O"),
        ArchivedAt:          a.ArchivedAt?.Value.ToString("O"));

    private static BankAccountDetailWire ToDetail(BankAccount a, decimal currentBalance, string? lastImportAt, bool feedConnected) => new(
        Id:                  a.Id.Value,
        TenantId:            a.TenantId.Value,
        DisplayName:         a.DisplayName,
        InstitutionName:     a.InstitutionName ?? string.Empty,
        AccountNumberMask:   Mask(a.DisplayName),
        CurrencyCode:        a.Currency.Iso4217,
        LinkedLedgerAccountId: a.LinkedLedgerAccount.GLAccountId.Value,
        CurrentBalance:      (double)currentBalance,
        OpeningBalance:      (double)a.OpeningBalance,
        OpeningBalanceDate:  a.CutoverAsOf.Value.ToString("O"),
        ArchivedAt:          a.ArchivedAt?.Value.ToString("O"),
        LastCutoverDate:     a.CutoverAsOf.Value.ToString("O"),
        LastImportAt:        lastImportAt,
        FeedConnected:       feedConnected);

    private static StatementLineWire ToStatementLine(StatementLine l) => new(
        Id:                  l.Id.Value,
        TenantId:            l.TenantId.Value,
        AccountId:           l.AccountId.Value,
        TransactionDate:     l.PostedAt.Value.ToString("O"),
        Description:         l.Description,
        Amount:              (double)l.Amount,
        Balance:             null,
        Reference:           l.ProviderTxnId,
        ReconciliationState: ToFrontendState(l.State));

    private static MatchProposalWire ToMatchProposal(StatementLine line, MatchLink link) => new(
        StatementLineId:      line.Id.Value,
        MatchLinkId:          link.Id.Value,
        LedgerTransactionRef: link.LedgerTransaction.JournalEntryId.Value,
        Amount:               (double)link.Amount,
        State:                "Proposed",
        StatementDescription: line.Description,
        StatementDate:        line.PostedAt.Value.ToString("O"));

    /// <summary>Derives a last-4 mask placeholder (the domain has no dedicated mask column in v1).</summary>
    private static string Mask(string displayName)
    {
        var digits = new string(displayName.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : "0000";
    }

    // ── Request bodies ───────────────────────────────────────────────────────────
    public sealed record CreateBankAccountBody(
        string? DisplayName, string? InstitutionName, string? AccountNumberMask,
        string? CurrencyCode, string? LinkedLedgerAccountId, string? Kind);
    public sealed record SetOpeningBalanceBody(decimal OpeningBalance, string? OpeningBalanceDate);
    public sealed record MatchLinkBody(string? MatchLinkId);
    public sealed record PeriodBody(string? PeriodId);

    // ── Response DTOs (field-identical to the banking.ts consumer contract) ─────────
    public sealed record BankAccountSummaryWire(
        string Id, string TenantId, string DisplayName, string InstitutionName, string AccountNumberMask,
        string CurrencyCode, string LinkedLedgerAccountId, double CurrentBalance, double OpeningBalance,
        string? OpeningBalanceDate, string? ArchivedAt);

    public sealed record BankAccountListWire(BankAccountSummaryWire[] Items, int Total);

    public sealed record BankAccountDetailWire(
        string Id, string TenantId, string DisplayName, string InstitutionName, string AccountNumberMask,
        string CurrencyCode, string LinkedLedgerAccountId, double CurrentBalance, double OpeningBalance,
        string? OpeningBalanceDate, string? ArchivedAt, string? LastCutoverDate, string? LastImportAt, bool FeedConnected);

    public sealed record StatementLineWire(
        string Id, string TenantId, string AccountId, string TransactionDate, string Description,
        double Amount, double? Balance, string? Reference, string ReconciliationState);

    public sealed record StatementLineListWire(StatementLineWire[] Items, int Total);

    public sealed record MatchProposalWire(
        string StatementLineId, string MatchLinkId, string LedgerTransactionRef, double Amount,
        string State, string StatementDescription, string StatementDate);

    public sealed record MatchProposalListWire(MatchProposalWire[] Proposals, int Total);

    public sealed record ImportBatchResultWire(int ImportedCount, int SkippedCount, string[] Errors);

    public sealed record MatchResultWire(string MatchLinkId, string State);

    public sealed record ReconciliationStateWire(
        string AccountId, string? PeriodId, double OpeningBalance, double StatementClosingBalance,
        double ClearedMovement, bool IsBalanced, string LockState);

    public sealed record FeedConnectionStartWire(string? Url, string? ClaimUrl);

    public sealed record FeedPullResultWire(int LinesAdded);
}
