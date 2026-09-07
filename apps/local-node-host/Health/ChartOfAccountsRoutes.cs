using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Seeds;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 gap C4 — node-local chart-of-accounts seed route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>POST /api/local-node/chart-of-accounts/seed-from-template</c> —
///     create a <see cref="ChartOfAccounts"/> for the given legal entity and
///     seed it with accounts from the selected bundled template. Returns 201
///     with <c>{ chartId, entityId, accountsSeeded }</c>. 400 when the entity
///     is not found in the local store or <c>templateId</c> is unrecognised.
///     Idempotency: a second call with the same entity creates a second chart
///     (append semantics). Frontend guards by checking entity count before
///     the wizard advances.</item>
/// </list>
/// </para>
/// <para>
/// <b>ADR 0115 §D7 compliance.</b> Seed content is BUNDLED in the host binary
/// (<see cref="LocalNodeChartTemplates"/>, which mirrors
/// <see cref="DefaultChartTemplates"/> + the frontend's <c>coa-templates.ts</c>
/// catalog). NO Bridge / network fetch is required. The operator never has to
/// be online to seed a chart.
/// </para>
/// <para>
/// <b>Template split decision (gap C4).</b> The frontend POSTs a
/// <c>templateId</c> string; the host resolves the bundled template, builds
/// the domain objects, and persists them — so neither the account list NOR any
/// network call leaves the device. This is the cleanest split: the host owns
/// the canonical seed catalog (same source as <see cref="DefaultChartTemplates"/>
/// in <c>blocks-financial-ledger</c>), and the frontend selects from the
/// catalog by ID.
/// </para>
/// <para>
/// <b>Financial-cluster audit-envelope durable-layer pattern.</b> The local
/// node IS the durable mutation layer for the offline first-run path. Writes to
/// <c>charts_of_accounts</c> and <c>gl_accounts</c> in the SQLCipher store
/// satisfy the X-AUDIT obligation by their presence in the keyed durable store
/// — no inline signed-event emission is required at this stage (same deferred
/// posture as <see cref="Harborline.Api.Blocks.FinancialLedger.Models.LegalEntity"/>
/// and <see cref="Harborline.Api.Blocks.FinancialLedger.Models.JournalEntry"/>).
/// </para>
/// </remarks>
public static class ChartOfAccountsRoutes
{
    /// <summary>Canonical route base for the node-local CoA seed surface.</summary>
    public const string SeedRoute = "/api/local-node/chart-of-accounts/seed-from-template";

    /// <summary>
    /// Maps the chart-of-accounts seed route onto <paramref name="app"/>, closing
    /// over the <see cref="LocalNodeDbContext"/> <paramref name="factory"/> from
    /// the outer host container.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<LocalNodeDbContext> factory, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        // POST /api/local-node/chart-of-accounts/seed-from-template
        app.MapPost(SeedRoute, async (
            SeedChartRequest body,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var admittedAt = timeProvider.GetUtcNow();
            if (body is null || string.IsNullOrWhiteSpace(body.EntityId))
            {
                return Results.BadRequest(new { error = "entityId is required." });
            }

            // Resolve the bundled template. The "skip" template is valid — it
            // seeds an empty chart (0 accounts), which is a legitimate offline
            // first-run posture.
            var template = LocalNodeChartTemplates.Resolve(body.TemplateId ?? "rental-real-estate");
            if (template is null)
            {
                return Results.BadRequest(new
                {
                    error = $"templateId '{body.TemplateId}' is not recognised. " +
                             $"Valid ids: {string.Join(", ", LocalNodeChartTemplates.KnownIds)}.",
                });
            }

            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Verify the entity exists in the local store.
            var entityId = new LegalEntityId(body.EntityId);
            var entityExists = await ctx.Set<LegalEntity>()
                .AsNoTracking()
                .AnyAsync(e => e.TenantId == LocalTenantId && e.Id == entityId, ct)
                .ConfigureAwait(false);

            if (!entityExists)
            {
                return Results.BadRequest(new
                {
                    error = $"Entity '{body.EntityId}' not found in the local store.",
                });
            }

            // Build the ChartOfAccounts domain record.
            var chartId = ChartOfAccountsId.NewId();
            var chartName = string.IsNullOrWhiteSpace(body.ChartName)
                ? $"{template.Name} — {DateOnly.FromDateTime(admittedAt.UtcDateTime):yyyy}"
                : body.ChartName;

            var now = new Instant(admittedAt);
            var chart = new ChartOfAccounts(
                Id: chartId,
                LegalEntityId: entityId,
                Name: chartName,
                BaseCurrency: body.BaseCurrency ?? "USD",
                FiscalYearStartMonth: 1,
                FiscalYearStartDay: 1,
                RetainedEarningsAccountId: null,   // Wired after accounts are seeded if present.
                IsActive: true,
                CreatedAtUtc: now,
                UpdatedAtUtc: now);

            ctx.Set<ChartOfAccounts>().Add(chart);

            // Expand the template into GLAccount records. Parent codes must appear
            // before children in DefaultChartTemplates (documented contract), so we
            // build a code→GLAccountId map in insertion order.
            var codeToId = new Dictionary<string, GLAccountId>(StringComparer.Ordinal);
            var seededAccounts = new List<GLAccount>(template.Accounts.Count);

            foreach (var row in template.Accounts)
            {
                GLAccountId? parentId = null;
                if (row.ParentCode is not null && codeToId.TryGetValue(row.ParentCode, out var pid))
                {
                    parentId = pid;
                }

                var account = GLAccount.Create(
                    id: GLAccountId.NewId(),
                    chartId: chartId,
                    code: row.Code,
                    name: row.Name,
                    type: row.Type,
                    subtype: row.Subtype,
                    currency: body.BaseCurrency ?? "USD",
                    createdAtUtc: now,
                    parentAccountId: parentId,
                    isPostable: row.IsPostable);

                codeToId[row.Code] = account.Id;
                seededAccounts.Add(account);
            }

            ctx.Set<GLAccount>().AddRange(seededAccounts);

            // Wire the retained-earnings account if the template includes code 3900.
            if (codeToId.TryGetValue("3900", out var retainedId))
            {
                // Re-build chart with the FK wired. EF tracks the first Add, so we
                // need to detach + re-add, or just update the tracked entry directly.
                var entry = ctx.Entry(chart);
                entry.Property(nameof(ChartOfAccounts.RetainedEarningsAccountId)).CurrentValue = retainedId;
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Created(
                SeedRoute,
                new SeedChartResponse(
                    ChartId: chartId.Value,
                    EntityId: body.EntityId,
                    TemplateName: template.Name,
                    AccountsSeeded: seededAccounts.Count));
        });
    }
}

// ── Bundled template catalog ──────────────────────────────────────────────────

/// <summary>
/// Node-host-side bundled chart-of-accounts templates (ADR 0115 §D7).
/// Mirrors <see cref="DefaultChartTemplates"/> from
/// <c>blocks-financial-ledger</c> and exposes the same ids as the frontend's
/// <c>coa-templates.ts</c>. All seed content is local to the binary — no
/// Bridge / network fetch required.
/// </summary>
internal static class LocalNodeChartTemplates
{
    /// <summary>All known template ids exposed for validation error messages.</summary>
    internal static readonly IReadOnlyList<string> KnownIds =
        ["rental-real-estate", "pm-pack", "skip"];

    /// <summary>
    /// Resolves a bundled <see cref="ChartTemplate"/> by <paramref name="id"/>.
    /// Returns <see langword="null"/> when the id is unrecognised.
    /// </summary>
    internal static ChartTemplate? Resolve(string id) => id switch
    {
        "rental-real-estate" => DefaultChartTemplates.RentalRealEstate,
        "pm-pack"            => DefaultChartTemplates.ScorpManagementCo,
        "skip"               => SkipTemplate,
        _                    => null,
    };

    /// <summary>
    /// The "skip" template — an empty chart with no starter accounts. The
    /// operator adds accounts manually in the Accounting module.
    /// </summary>
    private static readonly ChartTemplate SkipTemplate = new(
        Name: "Empty chart (start from scratch)",
        Description: "No starter accounts — add accounts manually in the Accounting module.",
        Accounts: []);
}

// ── Wire shapes ───────────────────────────────────────────────────────────────

/// <summary>Request body for <c>POST /api/local-node/chart-of-accounts/seed-from-template</c>.</summary>
public sealed record SeedChartRequest(
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("templateId")] string? TemplateId = "rental-real-estate",
    [property: JsonPropertyName("chartName")] string? ChartName = null,
    [property: JsonPropertyName("baseCurrency")] string? BaseCurrency = "USD");

/// <summary>201 Created response after seeding a chart.</summary>
public sealed record SeedChartResponse(
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("templateName")] string TemplateName,
    [property: JsonPropertyName("accountsSeeded")] int AccountsSeeded);
