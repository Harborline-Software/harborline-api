using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T1 local-first sweep — node-local <b>chart-of-accounts MANAGEMENT</b> surface
/// (list / detail / create / archive), the node-resident counterpart of the Bridge
/// <c>ChartOfAccountsEndpoints</c> (Track E). Relocates the COA read+write surface
/// off signal-bridge so the Accounting module — and, critically, the Journal Entries
/// Account column (which resolves account names via <c>useCOACache</c>→
/// <c>useChartOfAccounts</c>) — renders fully offline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes (the camelCase wire mirrors the Bridge <c>/api/v1/chart-of-accounts</c>
/// contract <c>ledger.ts</c> consumes — same shapes, node transport):</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/chart-of-accounts</c> — list accounts in the node's
///     install chart. <c>?includeInactive=true</c> includes archived accounts. Returns
///     <c>{ chartId, accounts: [...] }</c> (an empty chart / no-chart yet returns an
///     empty list with <c>chartId: null</c> — never an error, so the page renders pre-seed).</item>
///   <item><c>GET  /api/local-node/chart-of-accounts/{id}</c> — full account detail;
///     opaque 404 if absent or not in the install chart.</item>
///   <item><c>POST /api/local-node/chart-of-accounts</c> — create a GL account in the
///     install chart. 201 with the created detail. 400 on validation failure.</item>
///   <item><c>POST /api/local-node/chart-of-accounts/{id}/archive</c> — soft-delete
///     (sets <c>IsActive=false</c>; idempotent). 200 with the archived detail; 404 if absent.</item>
/// </list>
/// </para>
/// <para>
/// <b>Single-device chart resolution.</b> The embedded node serves one operator and one
/// chart per install (created via the seed route / onboarding wizard). All four routes
/// resolve "the chart" as the single most-recently-created <see cref="ChartOfAccounts"/>
/// row. Account lookup is by id only — <see cref="GLAccount"/> is keyed on <c>Id</c> with
/// no <c>TenantId</c> column (install-global on the single-device node; same posture as
/// <see cref="Harborline.Api.LocalNodeHost.Data.Financial.NodeEfAccountResolver"/>). Reads reuse
/// that resolver; create/archive write directly through the EF context factory (mirroring
/// <see cref="EntityRoutes"/>).
/// </para>
/// <para>
/// <b>Delete semantics (CIC ruling 2026-06-03 — no hard-DELETE on masters).</b> GL accounts
/// are ARCHIVED (soft-delete), never hard-deleted — exactly the Bridge posture
/// (<c>ChartOfAccountsEndpoints.HandleArchiveAccountAsync</c>). Archive produces a new
/// inactive record copy and persists it.
/// </para>
/// <para>
/// <b>Recoverability (SC-4-C2).</b> Create + archive write ONLY the Store-DEK-enveloped,
/// recoverable <c>local-node.db</c> (the <c>gl_accounts</c> table contributed by
/// <c>FinancialLedgerEntityModule</c>) via the EF context factory — no kernel CRDT /
/// per-team event-log write, no <c>IDomainEventPublisher</c> call. So the COA write path
/// adds no orphan vector: <c>Sc4RecoverabilityGuard</c> + the SC4-T9(b) IL/DI gates stay
/// green (the routes reference no <c>PostingEngine</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>).
/// The financial-cluster audit-envelope durable-layer pattern applies (no inline signed
/// event — row presence in the keyed store satisfies X-AUDIT; same deferred posture as
/// <see cref="ChartOfAccountsRoutes"/> and <see cref="JournalEntryRoutes"/>).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): they bind to the loopback-only Kestrel listener
/// (same posture as every other <c>/api/local-node/*</c> route). The Bridge equivalents
/// carry CSRF + AccountantPolicy because they are network-reachable; the node surface is not.
/// </para>
/// <para>
/// The factory is injected from the OUTER host container and passed to <see cref="Map"/> —
/// NOT resolved via <c>[FromServices]</c>, which would fail on the inner shared-app
/// container (bug-2849).
/// </para>
/// </remarks>
public static class ChartOfAccountsManagementRoutes
{
    /// <summary>Canonical route base for the node-local CoA management surface.</summary>
    public const string RouteBase = "/api/local-node/chart-of-accounts";

    /// <summary>
    /// Maps the chart-of-accounts management routes onto <paramref name="app"/>, closing
    /// over the <see cref="LocalNodeDbContext"/> <paramref name="factory"/> from the outer
    /// host container.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<LocalNodeDbContext> factory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        MapList(app, factory);
        MapDetail(app, factory);
        MapCreate(app, factory, timeProvider);
        MapArchive(app, factory, timeProvider);
    }

    // ── GET /api/local-node/chart-of-accounts ─────────────────────────────────────
    private static void MapList(IEndpointRouteBuilder app, IDbContextFactory<LocalNodeDbContext> factory)
    {
        app.MapGet(RouteBase, async (bool? includeInactive, CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
            if (chartId is null)
            {
                // No chart seeded yet — return an empty list so the Accounting page renders
                // (the COA cache resolves to an empty map; the JE Account column degrades to
                // raw ids rather than throwing). Never an error pre-seed.
                return Results.Ok(new CoaListResponse(null, []));
            }

            var query = ctx.Set<GLAccount>()
                .AsNoTracking()
                .Where(a => a.ChartId == chartId);
            if (includeInactive != true)
            {
                query = query.Where(a => a.IsActive);
            }

            var accounts = await query.ToListAsync(ct).ConfigureAwait(false);
            var items = accounts
                .OrderBy(a => a.Code, StringComparer.Ordinal)
                .Select(GlAccountSummaryWire.From)
                .ToList();

            return Results.Ok(new CoaListResponse(chartId.Value.Value, items));
        });
    }

    // ── GET /api/local-node/chart-of-accounts/{id} ────────────────────────────────
    private static void MapDetail(IEndpointRouteBuilder app, IDbContextFactory<LocalNodeDbContext> factory)
    {
        app.MapGet($"{RouteBase}/{{id}}", async (string id, CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
            if (chartId is null)
            {
                return Results.NotFound();
            }

            var account = await ctx.Set<GLAccount>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == new GLAccountId(id), ct)
                .ConfigureAwait(false);

            // Opaque 404 when absent OR not in the install chart.
            if (account is null || account.ChartId != chartId)
            {
                return Results.NotFound();
            }

            return Results.Ok(GlAccountDetailWire.From(account));
        });
    }

    // ── POST /api/local-node/chart-of-accounts ────────────────────────────────────
    private static void MapCreate(IEndpointRouteBuilder app, IDbContextFactory<LocalNodeDbContext> factory, TimeProvider timeProvider)
    {
        app.MapPost(RouteBase, async (CreateAccountRequest body, CancellationToken ct) =>
        {
            var at = new Instant(timeProvider.GetUtcNow());
            if (body is null || string.IsNullOrWhiteSpace(body.Code))
            {
                return Results.BadRequest(new { error = "code_required" });
            }
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(new { error = "name_required" });
            }
            if (!Enum.TryParse<GLAccountType>(body.Type, ignoreCase: true, out var accountType))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_account_type",
                    detail = $"type must be one of: {string.Join(", ", Enum.GetNames<GLAccountType>())}.",
                });
            }
            if (string.IsNullOrWhiteSpace(body.Subtype)
                || !Enum.TryParse<AccountSubtype>(body.Subtype, ignoreCase: true, out var subtype))
            {
                return Results.BadRequest(new { error = "invalid_subtype" });
            }
            if (string.IsNullOrWhiteSpace(body.Currency) || body.Currency.Length != 3)
            {
                return Results.BadRequest(new { error = "invalid_currency" });
            }

            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
            if (chartId is null)
            {
                // No chart to add to — the operator must seed a chart first.
                return Results.BadRequest(new { error = "no_chart" });
            }

            // Validate the parent account, if supplied, belongs to the same chart.
            GLAccountId? parentAccountId = null;
            if (!string.IsNullOrWhiteSpace(body.ParentAccountId))
            {
                var parent = await ctx.Set<GLAccount>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == new GLAccountId(body.ParentAccountId), ct)
                    .ConfigureAwait(false);
                if (parent is null || parent.ChartId != chartId)
                {
                    return Results.BadRequest(new { error = "parent_not_found" });
                }
                parentAccountId = parent.Id;
            }

            var account = GLAccount.Create(
                id:              GLAccountId.NewId(),
                chartId:         chartId.Value,
                code:            body.Code.Trim(),
                name:            body.Name.Trim(),
                type:            accountType,
                subtype:         subtype,
                currency:        body.Currency.ToUpperInvariant(),
                createdAtUtc:    at,
                parentAccountId: parentAccountId,
                isPostable:      body.IsPostable ?? true,
                description:     body.Description?.Trim());

            // Defence-in-depth: the domain invariants (matching NormalBalance, etc.).
            var validation = account.Validate();
            if (!validation.IsValid)
            {
                return Results.BadRequest(new
                {
                    error = "validation_failed",
                    detail = string.Join("; ", validation.Errors),
                });
            }

            // A duplicate account code within the chart is a real user error (the
            // ux_gl_accounts_chart_code UNIQUE index enforces it). Pre-check so the
            // caller gets a clean 400 rather than an unhandled SQLite UNIQUE 500.
            var codeExists = await ctx.Set<GLAccount>()
                .AsNoTracking()
                .AnyAsync(a => a.ChartId == chartId && a.Code == account.Code, ct)
                .ConfigureAwait(false);
            if (codeExists)
            {
                return Results.BadRequest(new
                {
                    error = "duplicate_code",
                    detail = $"An account with code '{account.Code}' already exists in this chart.",
                });
            }

            ctx.Set<GLAccount>().Add(account);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Created(
                $"{RouteBase}/{account.Id.Value}",
                GlAccountDetailWire.From(account));
        });
    }

    // ── POST /api/local-node/chart-of-accounts/{id}/archive ───────────────────────
    private static void MapArchive(IEndpointRouteBuilder app, IDbContextFactory<LocalNodeDbContext> factory, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/archive", async (string id, CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
            if (chartId is null)
            {
                return Results.NotFound();
            }

            // Tracked load — we mutate + save.
            var account = await ctx.Set<GLAccount>()
                .FirstOrDefaultAsync(a => a.Id == new GLAccountId(id), ct)
                .ConfigureAwait(false);

            if (account is null || account.ChartId != chartId)
            {
                return Results.NotFound();
            }

            if (!account.IsActive)
            {
                // Already archived — idempotent.
                return Results.Ok(GlAccountDetailWire.From(account));
            }

            // GLAccount is a record — produce a new inactive copy and replace the tracked row.
            var archived = account with { IsActive = false, UpdatedAtUtc = new Instant(timeProvider.GetUtcNow()) };
            ctx.Entry(account).CurrentValues.SetValues(archived);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Ok(GlAccountDetailWire.From(archived));
        });
    }

    // ── Single-device chart resolution ────────────────────────────────────────────

    /// <summary>
    /// Resolves the install chart on the single-device node: the single most-recently-created
    /// <see cref="ChartOfAccounts"/> row. Returns <see langword="null"/> when no chart has been
    /// seeded yet (a legitimate pre-onboarding state).
    /// </summary>
    private static async Task<ChartOfAccountsId?> ResolveChartIdAsync(
        LocalNodeDbContext ctx,
        CancellationToken ct)
    {
        // CreatedAtUtc is an Instant stored via a DateTimeOffset value-converter, and SQLite
        // cannot ORDER BY a DateTimeOffset expression server-side (NotSupportedException). The
        // single-device node has at most a handful of charts, so materialise then order in
        // memory — cheap + correct (same client-side-ordering posture the JE read-model uses for
        // its non-keysettable projections). bug logged: node SQLite DateTimeOffset ORDER BY.
        var charts = await ctx.Set<ChartOfAccounts>()
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return charts
            .OrderByDescending(c => c.CreatedAtUtc.Value)
            .FirstOrDefault()
            ?.Id;
    }
}

// ── Wire shapes (camelCase; mirror the Bridge /api/v1/chart-of-accounts contract) ───

/// <summary>List envelope: <c>{ "chartId": "...", "accounts": [...] }</c> (chartId null pre-seed).</summary>
public sealed record CoaListResponse(
    [property: JsonPropertyName("chartId")] string? ChartId,
    [property: JsonPropertyName("accounts")] IReadOnlyList<GlAccountSummaryWire> Accounts);

/// <summary>Row item in a node COA list (mirrors the Bridge <c>ChartOfAccountsSummaryDto</c>).</summary>
public sealed record GlAccountSummaryWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("isPostable")] bool IsPostable,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("currency")] string? Currency)
{
    /// <summary>Projects a domain <see cref="GLAccount"/> onto the summary wire shape.</summary>
    public static GlAccountSummaryWire From(GLAccount a) => new(
        Id:         a.Id.Value,
        Code:       a.Code,
        Name:       a.Name,
        Type:       a.Type.ToString(),
        Subtype:    a.Subtype?.ToString(),
        IsActive:   a.IsActive,
        IsPostable: a.IsPostable,
        ParentId:   a.ParentAccountId?.Value,
        Currency:   a.Currency);
}

/// <summary>Full detail (mirrors the Bridge <c>GlAccountDetailDto</c>).</summary>
public sealed record GlAccountDetailWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("normalBalance")] string? NormalBalance,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("isPostable")] bool IsPostable,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("chartId")] string? ChartId,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt)
{
    /// <summary>Projects a domain <see cref="GLAccount"/> onto the detail wire shape.</summary>
    public static GlAccountDetailWire From(GLAccount a) => new(
        Id:            a.Id.Value,
        Code:          a.Code,
        Name:          a.Name,
        Type:          a.Type.ToString(),
        Subtype:       a.Subtype?.ToString(),
        NormalBalance: a.NormalBalance?.ToString(),
        IsActive:      a.IsActive,
        IsPostable:    a.IsPostable,
        ParentId:      a.ParentAccountId?.Value,
        Currency:      a.Currency,
        Description:   a.Description,
        ChartId:       a.ChartId?.Value,
        CreatedAt:     a.CreatedAtUtc?.Value.ToString("O"),
        UpdatedAt:     a.UpdatedAtUtc?.Value.ToString("O"));
}

/// <summary>POST body for <c>POST /api/local-node/chart-of-accounts</c> (create).</summary>
public sealed record CreateAccountRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("parentAccountId")] string? ParentAccountId = null,
    [property: JsonPropertyName("isPostable")] bool? IsPostable = null);
