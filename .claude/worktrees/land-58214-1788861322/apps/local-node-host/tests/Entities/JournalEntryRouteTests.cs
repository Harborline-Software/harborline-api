using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local journal-entries surface
/// (<see cref="JournalEntryRoutes"/>) — the Cohort D financial-ledger node-flip.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handlers <see cref="HostedJournalEntryApiEndpoint"/> registers, on a real
/// in-process Kestrel listener (ephemeral loopback port), backed by a temp SQLite store (plain,
/// unencrypted — SC-1 at-rest is covered separately in SqlCipherFailClosedTests), driven by a real
/// <see cref="HttpClient"/>. The test composes the read-model over a real
/// <see cref="NodeEfJournalStore"/> and invokes the production registration helper
/// <see cref="JournalEntryRoutes.Map"/> (single source of truth, no test/prod drift).
/// </para>
/// <para>
/// <b>Read filters</b> (accountId incl. "split", chart, date, status, source, search), <b>manual
/// create</b> + <b>reverse</b>, and <b>tenant scoping</b> are exercised — mirroring the
/// maintenance/payments route-test style.
/// </para>
/// </remarks>
public sealed class JournalEntryRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfJournalStore _store = null!;
    private JournalPostingService _postingService = null!;
    private MutableAuthorizationContext _authorization = null!;

    /// <summary>The install-constant tenant the routes filter on (mirrors JournalEntryRoutes).</summary>
    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-je-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "je-test.db")};Pooling=False";

        // JournalEntry is contributed by FinancialLedgerEntityModule. Register it so
        // LocalNodeDbContext composes the same model the production host does.
        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));

        // Ticket 151 cluster: the JE write routes gate on ledger:post through the request-scoped
        // IAuthorizationContext seam. Allow-all by default; the gate tests narrow it.
        _authorization = new MutableAuthorizationContext();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(_authorization);
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Compose the read-model over the real node store (the production composition:
        // InMemoryJournalEntryQueryReadModel wraps IJournalStore.Snapshot).
        _store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        var readModel = new InMemoryJournalEntryQueryReadModel(_store);

        // Cohort D Step 2a — the write routes (create + reverse) route through the six-phase
        // JournalPostingService. Compose it over the SAME node-resident resolvers the production
        // host wires (NodeEfAccountResolver / NodeEfPeriodResolver over LocalNodeDbContext), so the
        // route tests exercise the real account-validity + period-gating gates (no test/prod drift).
        // Ticket 194: the posting service's gate is the SAME gate the routes resolve at, following the
        // SAME mutable holding set, so what the caller holds is decided in one place. It used to be an
        // allow-all gate, which meant the soft-close override was granted here by the test double no
        // matter what the caller held — the test-side shape of the constant this ticket deleted.
        _postingService = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    _store,
            gate:     Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
                          permission => _authorization.HasPermission(permission)));
        var postingAccessor = _postingService;

        // Seed the chart-of-accounts + an open fiscal period the create/reverse tests post into.
        // The READ tests use SeedEntryAsync (direct durable write — no posting), so they need no
        // accounts/periods; the WRITE tests post through the service and DO. CH-1 + accounts
        // 1000/4000 + an open 2026 period cover every posting test below.
        await SeedAccountAsync("1000", "Cash", GLAccountType.Asset, AccountSubtype.BankAccount);
        await SeedAccountAsync("4000", "Rental Revenue", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        // Map the SAME production routes (mirrors HostedJournalEntryApiEndpoint wiring).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        JournalEntryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            readModel,
            _store,
            postingAccessor,
            NodeTestActiveTeam.Accessor,
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string Route = "/api/local-node/journal-entries";

    // ── Fixture seeding (direct durable-row insert) ─────────────────────────────

    /// <summary>
    /// Inserts a balanced <see cref="JournalEntry"/> directly into the node store (via the store's
    /// SaveAtomicAsync — the same write path the POST route uses). Two single-account lines unless
    /// <paramref name="extraAccount"/> is supplied (then it is a split — three lines).
    /// </summary>
    private async Task SeedEntryAsync(
        string id,
        TenantId tenant,
        DateOnly date,
        string debitAccount,
        string creditAccount,
        decimal amount,
        JournalEntryStatus status = JournalEntryStatus.Posted,
        JournalEntrySource source = JournalEntrySource.Manual,
        string? chartId = "CH-1",
        string memo = "seed entry",
        string? extraAccount = null)
    {
        var lines = new List<JournalEntryLine>
        {
            new(new GLAccountId(debitAccount), amount, 0m),
        };
        if (extraAccount is null)
        {
            lines.Add(new JournalEntryLine(new GLAccountId(creditAccount), 0m, amount));
        }
        else
        {
            // Split the credit across two accounts so the entry touches 3 distinct accounts.
            var half = amount / 2m;
            lines.Add(new JournalEntryLine(new GLAccountId(creditAccount), 0m, half));
            lines.Add(new JournalEntryLine(new GLAccountId(extraAccount), 0m, amount - half));
        }

        var entry = new JournalEntry(
            id:           new JournalEntryId(id),
            tenantId:     tenant,
            entryDate:    date,
            memo:         memo,
            lines:        lines.AsReadOnly(),
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()))
        {
            ChartId     = chartId is null ? (ChartOfAccountsId?)null : new ChartOfAccountsId(chartId),
            Status      = status,
            PostedAtUtc = status == JournalEntryStatus.Posted ? new Instant(System.TimeProvider.System.GetUtcNow()) : null,
            SourceKind  = source,
        };
        await _store.SaveAtomicForTestAsync(tenant, entry);
    }

    /// <summary>
    /// Seeds a postable <see cref="GLAccount"/> into the node store (so the posting service's
    /// Phase-3 account-validity gate passes for create/reverse). The account <c>Id</c> equals its
    /// <paramref name="code"/> — the create route builds line accounts as <c>new GLAccountId(code)</c>
    /// and the resolver looks up by id, so id == code keeps the round-trip exact. Belongs to
    /// <paramref name="chartId"/> (default CH-1, the chart the posting tests use).
    /// </summary>
    private async Task SeedAccountAsync(
        string code,
        string name,
        GLAccountType type,
        AccountSubtype subtype,
        string chartId = "CH-1",
        bool isPostable = true)
    {
        var account = GLAccount.Create(
            id:          new GLAccountId(code),
            chartId:     new ChartOfAccountsId(chartId),
            code:        code,
            name:        name,
            type:        type,
            subtype:     subtype,
            currency:    "USD",
            isPostable:  isPostable,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(account);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds an OPEN <see cref="FiscalPeriod"/> covering [<paramref name="start"/>,
    /// <paramref name="end"/>] for <paramref name="chartId"/> (so the posting service's Phase-4
    /// period-gating passes for chart-scoped entries in range). The <see cref="FiscalPeriodStatus"/>
    /// can be overridden to exercise SoftClosed / Locked gating.
    /// </summary>
    private async Task SeedPeriodAsync(
        DateOnly start,
        DateOnly end,
        FiscalPeriodStatus status,
        string chartId = "CH-1")
    {
        var period = FiscalPeriod.CreateOpen(
            id:           FiscalPeriodId.NewId(),
            chartId:      new ChartOfAccountsId(chartId),
            fiscalYearId: new FiscalYearId("FY-2026"),
            kind:         FiscalPeriodKind.Monthly,
            label:        $"{start:yyyy-MM}",
            startDate:    start,
            endDate:      end,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        if (status != FiscalPeriodStatus.Open)
        {
            // Re-create with the requested status + the close fields the entity invariants require
            // (SoftClosedAtUtc for SoftClosed; both for Locked).
            period = period with
            {
                Status          = status,
                SoftClosedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()),
                LockedAtUtc     = status == FiscalPeriodStatus.Locked ? new Instant(System.TimeProvider.System.GetUtcNow()) : null,
            };
        }

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<FiscalPeriod>().Add(period);
        await ctx.SaveChangesAsync();
    }

    private Task SeedOpenPeriodAsync(DateOnly start, DateOnly end, string chartId = "CH-1") =>
        SeedPeriodAsync(start, end, FiscalPeriodStatus.Open, chartId);

    /// <summary>Asserts the refused post left NO journal entry on <paramref name="chartId"/> — a refusal
    /// that still wrote a row would be worse than the bypass it replaced.</summary>
    private async Task AssertNoEntryPostedAsync(string chartId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.Set<JournalEntry>()
            .Where(entry => entry.ChartId!.Value == chartId)
            .ToListAsync());
    }

    private static JsonElement Data(JsonElement doc) => doc.GetProperty("data");

    // ── Read: list ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "JE read: list is empty on a fresh store")]
    public async Task List_Empty_OnFreshStore()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, Data(doc).GetArrayLength());
        Assert.Equal(0, doc.GetProperty("total").GetInt32());
        Assert.False(doc.GetProperty("hasMore").GetBoolean());
    }

    [Fact(DisplayName = "JE read: serves a seeded entry with summary fields + accountIds")]
    public async Task List_ServesSeededEntry()
    {
        await SeedEntryAsync("JE-1", LocalTenantId, new DateOnly(2026, 1, 10), "1000", "4000", 250m);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());

        var e = arr[0];
        Assert.Equal("JE-1", e.GetProperty("id").GetString());
        Assert.Equal("Posted", e.GetProperty("status").GetString());
        Assert.Equal("Manual", e.GetProperty("sourceKind").GetString());
        Assert.Equal(2, e.GetProperty("lineCount").GetInt32());
        Assert.Equal(250d, e.GetProperty("totalDebits").GetDouble());
        Assert.Equal(250d, e.GetProperty("totalCredits").GetDouble());

        var accountIds = e.GetProperty("accountIds").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(2, accountIds.Count);
        Assert.Contains("1000", accountIds);
        Assert.Contains("4000", accountIds);
    }

    [Fact(DisplayName = "JE read: a split entry surfaces all distinct account ids")]
    public async Task List_SplitEntry_SurfacesAllAccountIds()
    {
        await SeedEntryAsync("JE-SPLIT", LocalTenantId, new DateOnly(2026, 1, 10),
            "1000", "4000", 300m, extraAccount: "4100");

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var e = Data(doc)[0];
        var accountIds = e.GetProperty("accountIds").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(3, e.GetProperty("lineCount").GetInt32());
        Assert.Equal(3, accountIds.Count);
        Assert.Contains("1000", accountIds);
        Assert.Contains("4000", accountIds);
        Assert.Contains("4100", accountIds);
    }

    [Fact(DisplayName = "JE read: list is newest-first by entry date")]
    public async Task List_NewestFirst()
    {
        await SeedEntryAsync("JE-OLD", LocalTenantId, new DateOnly(2026, 1, 1), "1000", "4000", 100m);
        await SeedEntryAsync("JE-NEW", LocalTenantId, new DateOnly(2026, 3, 1), "1000", "4000", 200m);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var arr = Data(doc);
        Assert.Equal("JE-NEW", arr[0].GetProperty("id").GetString());
        Assert.Equal("JE-OLD", arr[1].GetProperty("id").GetString());
    }

    // ── Read: filters ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "JE read: ?accountId= filters to entries touching that account")]
    public async Task List_FiltersByAccountId()
    {
        await SeedEntryAsync("JE-A", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "4000", 100m);
        await SeedEntryAsync("JE-B", LocalTenantId, new DateOnly(2026, 1, 6), "1200", "5000", 200m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?accountId=5000");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-B", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?accountId= matches a split-entry account too")]
    public async Task List_AccountId_MatchesSplitLeg()
    {
        await SeedEntryAsync("JE-SPLIT", LocalTenantId, new DateOnly(2026, 1, 5),
            "1000", "4000", 300m, extraAccount: "4100");
        await SeedEntryAsync("JE-PLAIN", LocalTenantId, new DateOnly(2026, 1, 6), "1000", "9999", 50m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?accountId=4100");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-SPLIT", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?chartId= filters to that chart (entity scope)")]
    public async Task List_FiltersByChart()
    {
        await SeedEntryAsync("JE-A", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "4000", 100m, chartId: "CH-A");
        await SeedEntryAsync("JE-B", LocalTenantId, new DateOnly(2026, 1, 6), "1000", "4000", 200m, chartId: "CH-B");

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?chartId=CH-A");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-A", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?from=/?to= filters by entry date range")]
    public async Task List_FiltersByDateRange()
    {
        await SeedEntryAsync("JE-JAN", LocalTenantId, new DateOnly(2026, 1, 15), "1000", "4000", 100m);
        await SeedEntryAsync("JE-MAR", LocalTenantId, new DateOnly(2026, 3, 15), "1000", "4000", 200m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?from=2026-02-01&to=2026-12-31");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-MAR", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?status= filters by lifecycle status (comma-separated)")]
    public async Task List_FiltersByStatus()
    {
        await SeedEntryAsync("JE-POSTED", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "4000", 100m,
            status: JournalEntryStatus.Posted);
        await SeedEntryAsync("JE-DRAFT", LocalTenantId, new DateOnly(2026, 1, 6), "1000", "4000", 200m,
            status: JournalEntryStatus.Draft);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?status=Draft");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-DRAFT", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?source= filters by source kind")]
    public async Task List_FiltersBySource()
    {
        await SeedEntryAsync("JE-MANUAL", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "4000", 100m,
            source: JournalEntrySource.Manual);
        await SeedEntryAsync("JE-INV", LocalTenantId, new DateOnly(2026, 1, 6), "1000", "4000", 200m,
            source: JournalEntrySource.Invoice);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?source=Invoice");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-INV", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?search= matches the memo substring")]
    public async Task List_FiltersBySearch()
    {
        await SeedEntryAsync("JE-RENT", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "4000", 100m,
            memo: "January rent receipt");
        await SeedEntryAsync("JE-UTIL", LocalTenantId, new DateOnly(2026, 1, 6), "1000", "4000", 200m,
            memo: "Utilities expense");

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?search=rent");
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-RENT", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: ?take= paginates and reports hasMore + nextCursor")]
    public async Task List_Pages_WithTakeAndCursor()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedEntryAsync($"JE-{i:D2}", LocalTenantId, new DateOnly(2026, 1, 1 + i), "1000", "4000", 100m);
        }

        var first = await _client.GetFromJsonAsync<JsonElement>($"{Route}?take=2");
        Assert.Equal(2, Data(first).GetArrayLength());
        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await _client.GetFromJsonAsync<JsonElement>($"{Route}?take=2&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(2, Data(second).GetArrayLength());
        // No overlap between page 1 and page 2.
        var firstIds = Data(first).EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        var secondIds = Data(second).EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact(DisplayName = "JE read: ?accountId= paging metadata reflects the filtered set across pages")]
    public async Task List_AccountIdFilter_PagesAcrossBoundary()
    {
        // 3 entries touch 5000, interleaved with 2 that don't. With take=2 the matches span a
        // page boundary — total/hasMore/nextCursor must be computed over the FILTERED set (the
        // old route-side post-filter reported the unfiltered superset's metadata).
        await SeedEntryAsync("JE-M0", LocalTenantId, new DateOnly(2026, 1, 1), "1000", "5000", 100m);
        await SeedEntryAsync("JE-X0", LocalTenantId, new DateOnly(2026, 1, 2), "1000", "4000", 100m);
        await SeedEntryAsync("JE-M1", LocalTenantId, new DateOnly(2026, 1, 3), "1000", "5000", 100m);
        await SeedEntryAsync("JE-X1", LocalTenantId, new DateOnly(2026, 1, 4), "1000", "4000", 100m);
        await SeedEntryAsync("JE-M2", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "5000", 100m);

        var first = await _client.GetFromJsonAsync<JsonElement>($"{Route}?accountId=5000&take=2");
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var firstIds = Data(first).EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "JE-M2", "JE-M1" }, firstIds);

        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await _client.GetFromJsonAsync<JsonElement>(
            $"{Route}?accountId=5000&take=2&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(3, second.GetProperty("total").GetInt32());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.True(second.GetProperty("nextCursor").ValueKind is JsonValueKind.Null);
        var secondIds = Data(second).EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "JE-M0" }, secondIds);
    }

    // ── Read: detail ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "JE read: by-id returns detail incl. lines")]
    public async Task GetById_ReturnsDetailWithLines()
    {
        await SeedEntryAsync("JE-X", LocalTenantId, new DateOnly(2026, 1, 10), "1000", "4000", 750m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}/JE-X");
        var d = doc.GetProperty("data");
        Assert.Equal("JE-X", d.GetProperty("id").GetString());
        Assert.Equal(2, d.GetProperty("lines").GetArrayLength());
        Assert.Equal(750d, d.GetProperty("totalDebits").GetDouble());
        var lineAccounts = d.GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("accountId").GetString()).ToList();
        Assert.Contains("1000", lineAccounts);
        Assert.Contains("4000", lineAccounts);
    }

    [Fact(DisplayName = "JE read: by-id 404 for an unknown id")]
    public async Task GetById_NotFound()
    {
        var resp = await _client.GetAsync($"{Route}/JE-DOES-NOT-EXIST");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Write: manual create ──────────────────────────────────────────────────

    [Fact(DisplayName = "JE create: a balanced two-line entry is created Posted/Manual")]
    public async Task Create_BalancedEntry_Succeeds()
    {
        var body = new
        {
            postingDate = "2026-01-15",
            memo = "Manual adjusting entry",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1000", amount = 500m, direction = "Debit" },
                new { accountCode = "4000", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var d = doc.GetProperty("data");
        Assert.Equal("Posted", d.GetProperty("status").GetString());
        Assert.Equal("Manual", d.GetProperty("sourceKind").GetString());
        Assert.Equal(500d, d.GetProperty("totalDebits").GetDouble());
        var id = d.GetProperty("id").GetString();

        // The entry is durably persisted + readable through the list.
        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(1, Data(list).GetArrayLength());
        Assert.Equal(id, Data(list)[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "ticket 151 cluster: JE create without ledger:post is refused (403), nothing persists")]
    public async Task Create_Without_LedgerPost_Is_Refused()
    {
        _authorization.Allow(TeamRolePermissions.RecordsWrite); // everything BUT ledger:post.

        var body = new
        {
            postingDate = "2026-01-15",
            memo = "Ungated write attempt",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1000", amount = 500m, direction = "Debit" },
                new { accountCode = "4000", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TeamRolePermissions.LedgerPost, doc.GetProperty("permission").GetString());

        // Nothing persisted — the refusal fired before any posting work.
        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, Data(list).GetArrayLength());
    }

    [Fact(DisplayName = "ticket 151 cluster: JE reverse without ledger:post is refused (403), original untouched")]
    public async Task Reverse_Without_LedgerPost_Is_Refused()
    {
        await SeedEntryAsync("JE-GATE-1", LocalTenantId, new DateOnly(2026, 1, 10), "1000", "4000", 250m);
        _authorization.Allow(TeamRolePermissions.RecordsWrite);

        var resp = await _client.PostAsJsonAsync($"{Route}/JE-GATE-1/reverse", new { });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // The original is untouched — still Posted, no reversing entry minted.
        _authorization.Allow(TeamRolePermissions.LedgerPost);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{Route}/JE-GATE-1");
        Assert.Equal("Posted", detail.GetProperty("data").GetProperty("status").GetString());
        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(1, Data(list).GetArrayLength());
    }

    [Fact(DisplayName = "JE create: an imbalanced entry is rejected 400 imbalanced")]
    public async Task Create_Imbalanced_Rejected()
    {
        var body = new
        {
            postingDate = "2026-01-15",
            memo = "Bad entry",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1000", amount = 500m, direction = "Debit" },
                new { accountCode = "4000", amount = 400m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("imbalanced", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "JE create: fewer than two lines is rejected 400 minimum_two_lines")]
    public async Task Create_TooFewLines_Rejected()
    {
        var body = new
        {
            postingDate = "2026-01-15",
            memo = "One-liner",
            chartId = "CH-1",
            lines = new[] { new { accountCode = "1000", amount = 500m, direction = "Debit" } },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("minimum_two_lines", doc.GetProperty("error").GetString());
    }

    // ── Write: posting gates (Cohort D Step 2a — six-phase JournalPostingService) ──
    //
    // These prove the create route now runs the full posting algorithm — account-validity
    // (Phase 3) and period-gating (Phase 4) — over the node-resident resolvers, not just the
    // route's own balance check. They are the live, testable caller the posting service gained.

    [Fact(DisplayName = "JE create posting: a line referencing an unknown account is rejected 400 unknown_account")]
    public async Task Create_UnknownAccount_Rejected()
    {
        // "9999" was never seeded into gl_accounts, so Phase-3 account-validity fails.
        var body = new
        {
            postingDate = "2026-01-15",
            memo = "Posts to a nonexistent account",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1000", amount = 500m, direction = "Debit" },
                new { accountCode = "9999", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_account", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "JE create posting: a line posting to a non-postable (header) account is rejected 400 account_not_postable")]
    public async Task Create_NonPostableAccount_Rejected()
    {
        // A header/summary account that must not receive postings directly (IsPostable=false).
        await SeedAccountAsync("1999", "Asset Header", GLAccountType.Asset, AccountSubtype.CurrentAsset, isPostable: false);

        var body = new
        {
            postingDate = "2026-01-15",
            memo = "Posts to a header account",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1999", amount = 500m, direction = "Debit" },
                new { accountCode = "4000", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("account_not_postable", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "JE create posting: a chart-scoped entry whose date has no fiscal period is rejected 400 no_period_for_date")]
    public async Task Create_NoPeriodForDate_Rejected()
    {
        // 2027 is OUTSIDE the seeded 2026 open period, so Phase-4 period-gating finds no period.
        var body = new
        {
            postingDate = "2027-03-15",
            memo = "Posts outside any fiscal period",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1000", amount = 500m, direction = "Debit" },
                new { accountCode = "4000", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_period_for_date", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "JE create posting: a Locked period rejects the post 400 period_locked (even the FinancialAdmin operator)")]
    public async Task Create_LockedPeriod_Rejected()
    {
        // A LOCKED period on a different chart so it does not collide with the open CH-1 period.
        // Distinct account codes too (GLAccount is keyed on Id == code, so codes must be unique).
        await SeedAccountAsync("1000-LK", "Cash (LK)", GLAccountType.Asset, AccountSubtype.BankAccount, chartId: "CH-LK");
        await SeedAccountAsync("4000-LK", "Revenue (LK)", GLAccountType.Revenue, AccountSubtype.OperatingIncome, chartId: "CH-LK");
        await SeedPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), FiscalPeriodStatus.Locked, chartId: "CH-LK");

        var body = new
        {
            postingDate = "2026-04-15",
            memo = "Posts into a locked period",
            chartId = "CH-LK",
            lines = new[]
            {
                new { accountCode = "1000-LK", amount = 500m, direction = "Debit" },
                new { accountCode = "4000-LK", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("period_locked", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName =
        "JE create posting: a caller HOLDING financial:period-override-soft-close CAN post into a SoftClosed period")]
    public async Task Create_SoftClosedPeriod_AdminBypass_Succeeds()
    {
        // Ticket 194: the override is an ACT the caller holds, asked of the gate at the posting
        // service's point of use and named against the fiscal period it addresses — no longer a
        // constant true from a static user context. Locked would still block, override or not.
        // Different chart + distinct account codes so it does not collide with the open CH-1 period
        // or the install-seeded 1000/4000 accounts.
        _authorization.Allow(
            TeamRolePermissions.LedgerPost, Permission.FinancialPeriodOverrideSoftClose);
        await SeedAccountAsync("1000-SC", "Cash (SC)", GLAccountType.Asset, AccountSubtype.BankAccount, chartId: "CH-SC");
        await SeedAccountAsync("4000-SC", "Revenue (SC)", GLAccountType.Revenue, AccountSubtype.OperatingIncome, chartId: "CH-SC");
        await SeedPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), FiscalPeriodStatus.SoftClosed, chartId: "CH-SC");

        var body = new
        {
            postingDate = "2026-05-15",
            memo = "Admin posts into a soft-closed period",
            chartId = "CH-SC",
            lines = new[]
            {
                new { accountCode = "1000-SC", amount = 500m, direction = "Debit" },
                new { accountCode = "4000-SC", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Posted", doc.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact(DisplayName =
        "JE create posting: a caller NOT holding financial:period-override-soft-close is refused " +
        "400 period_soft_closed (ticket 194)")]
    public async Task Create_SoftClosedPeriod_WithoutTheOverrideAct_Refused()
    {
        // The pair to Create_SoftClosedPeriod_AdminBypass_Succeeds, one holding different: the caller
        // may post to the ledger but does NOT hold the soft-close override. Before ticket 194 the node
        // registered a user context that answered that override with a constant, so this state could
        // not be reached at all — the bypass survived having no administrator.
        _authorization.Allow(TeamRolePermissions.LedgerPost);
        await SeedAccountAsync("1000-NX", "Cash (NX)", GLAccountType.Asset, AccountSubtype.BankAccount, chartId: "CH-NX");
        await SeedAccountAsync("4000-NX", "Revenue (NX)", GLAccountType.Revenue, AccountSubtype.OperatingIncome, chartId: "CH-NX");
        await SeedPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), FiscalPeriodStatus.SoftClosed, chartId: "CH-NX");

        var body = new
        {
            postingDate = "2026-05-15",
            memo = "A caller without the override posts into a soft-closed period",
            chartId = "CH-NX",
            lines = new[]
            {
                new { accountCode = "1000-NX", amount = 500m, direction = "Debit" },
                new { accountCode = "4000-NX", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("period_soft_closed", doc.GetProperty("error").GetString());
        await AssertNoEntryPostedAsync("CH-NX");
    }

    [Fact(DisplayName = "JE create posting: an UNSCOPED (no-chart) balanced entry posts without period-gating")]
    public async Task Create_UnscopedEntry_SkipsPeriodGating_Succeeds()
    {
        // No chartId → Phase-4 period-gating is skipped entirely (per the algorithm). Account
        // validity still applies, and 1000/4000 exist, so the post succeeds even though there is
        // no fiscal period for an unscoped entry.
        var body = new
        {
            postingDate = "2030-09-09", // far outside any seeded period — proves gating is skipped
            memo = "Unscoped manual entry",
            chartId = (string?)null,
            lines = new[]
            {
                new { accountCode = "1000", amount = 500m, direction = "Debit" },
                new { accountCode = "4000", amount = 500m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Posted", doc.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact(DisplayName = "JE create posting: the posted entry is durably persisted as Posted/Manual via the posting service")]
    public async Task Create_PostedEntry_IsDurableAndPosted()
    {
        var body = new
        {
            postingDate = "2026-07-01",
            memo = "Routed through JournalPostingService",
            chartId = "CH-1",
            lines = new[]
            {
                new { accountCode = "1000", amount = 125m, direction = "Debit" },
                new { accountCode = "4000", amount = 125m, direction = "Credit" },
            },
        };

        var resp = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("data").GetProperty("id").GetString();

        // postedAt is stamped by the service's TimeProvider (one clock of record).
        Assert.False(string.IsNullOrEmpty(created.GetProperty("data").GetProperty("postedAt").GetString()));

        // The durable row carries Posted/Manual and is readable by id.
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{Route}/{id}");
        Assert.Equal("Posted", detail.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("Manual", detail.GetProperty("data").GetProperty("sourceKind").GetString());
    }

    // ── Write: reverse ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "JE reverse: creates a swapped reversing entry + marks the original Reversed")]
    public async Task Reverse_PostedEntry_Succeeds()
    {
        await SeedEntryAsync("JE-ORIG", LocalTenantId, new DateOnly(2026, 1, 10), "1000", "4000", 250m,
            status: JournalEntryStatus.Posted);

        var resp = await _client.PostAsJsonAsync($"{Route}/JE-ORIG/reverse", new { reversalDate = "2026-02-01" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var rev = doc.GetProperty("data");
        Assert.Equal("Reversal", rev.GetProperty("sourceKind").GetString());
        Assert.Equal("JE-ORIG", rev.GetProperty("reversalOf").GetString());
        // The reversing entry swaps debits/credits but stays balanced.
        Assert.Equal(250d, rev.GetProperty("totalDebits").GetDouble());
        Assert.Equal(250d, rev.GetProperty("totalCredits").GetDouble());

        // The original is now Reversed and points to the reversing entry.
        var orig = await _client.GetFromJsonAsync<JsonElement>($"{Route}/JE-ORIG");
        Assert.Equal("Reversed", orig.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(rev.GetProperty("id").GetString(),
            orig.GetProperty("data").GetProperty("reversedBy").GetString());
    }

    [Fact(DisplayName = "JE reverse: a second reverse on an already-reversed entry is rejected 400")]
    public async Task Reverse_AlreadyReversed_Rejected()
    {
        await SeedEntryAsync("JE-ORIG", LocalTenantId, new DateOnly(2026, 1, 10), "1000", "4000", 250m,
            status: JournalEntryStatus.Posted);

        var first = await _client.PostAsJsonAsync($"{Route}/JE-ORIG/reverse", new { });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync($"{Route}/JE-ORIG/reverse", new { });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var doc = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_reversed", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "JE reverse: a draft (non-posted) entry cannot be reversed (400 not_posted)")]
    public async Task Reverse_Draft_Rejected()
    {
        await SeedEntryAsync("JE-DRAFT", LocalTenantId, new DateOnly(2026, 1, 10), "1000", "4000", 250m,
            status: JournalEntryStatus.Draft);

        var resp = await _client.PostAsJsonAsync($"{Route}/JE-DRAFT/reverse", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_posted", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "JE reverse: 404 for an unknown id")]
    public async Task Reverse_NotFound()
    {
        var resp = await _client.PostAsJsonAsync($"{Route}/JE-NOPE/reverse", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Tenant isolation (financial-cluster discipline; ADR 0092) ────────────────

    [Fact(DisplayName = "JE read: a foreign-tenant entry never leaks into the list")]
    public async Task List_DoesNotLeakForeignTenant()
    {
        await SeedEntryAsync("JE-LOCAL", LocalTenantId, new DateOnly(2026, 1, 5), "1000", "4000", 100m);
        await SeedEntryAsync("JE-OTHER", new TenantId("some-other-tenant"), new DateOnly(2026, 1, 6),
            "1000", "4000", 999m);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var arr = Data(doc);
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("JE-LOCAL", arr[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "JE read: by-id 404 for a foreign-tenant entry (no cross-tenant read)")]
    public async Task GetById_ForeignTenant_NotFound()
    {
        await SeedEntryAsync("JE-OTHER", new TenantId("some-other-tenant"), new DateOnly(2026, 1, 6),
            "1000", "4000", 999m);

        var resp = await _client.GetAsync($"{Route}/JE-OTHER");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "JE reverse: a foreign-tenant entry cannot be reversed (opaque 404)")]
    public async Task Reverse_ForeignTenant_NotFound()
    {
        await SeedEntryAsync("JE-OTHER", new TenantId("some-other-tenant"), new DateOnly(2026, 1, 6),
            "1000", "4000", 999m, status: JournalEntryStatus.Posted);

        var resp = await _client.PostAsJsonAsync($"{Route}/JE-OTHER/reverse", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── ADR 0122 §D2 — posting idempotency on the RECOVERABLE node store ─────────
    //
    // These exercise the dedupe over the REAL NodeEfJournalStore (local-node.db /
    // SQLite), proving the dedupe state IS the recoverable record (SC-4-safe placement,
    // not a seed-keyed KV). Two halves: the cooperative phase-1.5 lookup, and the
    // durable unique-index backstop.

    /// <summary>
    /// Posts a balanced source-referenced JE through the node <see cref="JournalPostingService"/>,
    /// then RE-DRIVES the same source event (same SourceReference, fresh entry id). The
    /// phase-1.5 lookup over the recoverable store must return the existing JE — exactly ONE
    /// row persisted on the node store across both drives.
    /// </summary>
    [Fact(DisplayName = "JE idempotency (node store): re-driving the same SourceReference posts exactly ONE JE")]
    public async Task Post_ReDrivenSourceReference_NodeStore_PersistsExactlyOne()
    {
        const string sourceRef = "invoice:INV-2026-07-01-A-0042";

        var first = await _postingService.PostAsync(
            NewSourceReferencedDraft("JE-IDEM-1", new DateOnly(2026, 7, 1), 300m, sourceRef));
        Assert.True(first.IsSuccess);

        // Re-drive: a brand-new draft (distinct id) for the SAME source event.
        var redrive = await _postingService.PostAsync(
            NewSourceReferencedDraft("JE-IDEM-2", new DateOnly(2026, 7, 1), 300m, sourceRef));
        Assert.True(redrive.IsSuccess);

        // The re-drive returned the ORIGINAL posted entry, and the recoverable store holds
        // exactly one JE for that source reference.
        Assert.Equal("JE-IDEM-1", redrive.Entry!.Id.Value);
        var hits = _store.Snapshot(LocalTenantId)
            .Where(e => e.SourceReference == sourceRef)
            .ToList();
        Assert.Single(hits);
        Assert.Equal("JE-IDEM-1", hits[0].Id.Value);
    }

    /// <summary>
    /// A manual JE (null SourceReference) is NEVER deduped: two distinct manual JEs both persist
    /// on the node store. (SQLite treats NULLs as distinct in the unique index, so the durable
    /// backstop does not collide manual entries either.)
    /// </summary>
    [Fact(DisplayName = "JE idempotency (node store): null SourceReference is not deduped — manual JEs both persist")]
    public async Task Post_NullSourceReference_NodeStore_NotDeduped()
    {
        var a = await _postingService.PostAsync(
            NewSourceReferencedDraft("JE-MAN-1", new DateOnly(2026, 7, 2), 75m, sourceReference: null));
        var b = await _postingService.PostAsync(
            NewSourceReferencedDraft("JE-MAN-2", new DateOnly(2026, 7, 2), 75m, sourceReference: null));

        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);
        var manual = _store.Snapshot(LocalTenantId)
            .Where(e => e.SourceReference is null)
            .ToList();
        Assert.Equal(2, manual.Count);
    }

    /// <summary>
    /// The DURABLE backstop: the unique index <c>ux_journal_entries_tenant_source_ref</c> on the
    /// recoverable store rejects a second row with the same (tenant, SourceReference) even when the
    /// cooperative phase-1.5 lookup is bypassed (a direct double <c>SaveAtomicAsync</c> simulates a
    /// race that slips past it). Proves the dedupe is enforced by the recoverable store itself, not
    /// only by the service.
    /// </summary>
    [Fact(DisplayName = "JE idempotency (node store): the unique index rejects a duplicate SourceReference on direct double-save")]
    public async Task SaveAtomic_DuplicateSourceReference_NodeStore_ViolatesUniqueIndex()
    {
        const string sourceRef = "payment-clear:PMT-2026-07-03-A-0007";

        var firstEntry = NewSourceReferencedDraft("JE-DUP-1", new DateOnly(2026, 7, 3), 120m, sourceRef)
            with { Status = JournalEntryStatus.Posted, PostedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()) };
        await _store.SaveAtomicForTestAsync(LocalTenantId, firstEntry);

        var dupEntry = NewSourceReferencedDraft("JE-DUP-2", new DateOnly(2026, 7, 3), 120m, sourceRef)
            with { Status = JournalEntryStatus.Posted, PostedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()) };

        // A direct second save with the SAME (tenant, source-reference) must violate the unique
        // index — DbUpdateException wraps the SQLite UNIQUE-constraint failure.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => _store.SaveAtomicForTestAsync(LocalTenantId, dupEntry));

        // Only the first row survived.
        var hits = _store.Snapshot(LocalTenantId)
            .Where(e => e.SourceReference == sourceRef)
            .ToList();
        Assert.Single(hits);
        Assert.Equal("JE-DUP-1", hits[0].Id.Value);
    }

    /// <summary>
    /// Builds a balanced two-line Draft entry posting 1000→4000 for <paramref name="amount"/> with
    /// the given <paramref name="sourceReference"/> (null = manual). Used by the node idempotency
    /// tests to drive the posting service over the real CH-1 chart/period the fixture seeds.
    /// </summary>
    private JournalEntry NewSourceReferencedDraft(
        string id,
        DateOnly date,
        decimal amount,
        string? sourceReference)
        => new JournalEntry(
            id:           new JournalEntryId(id),
            tenantId:     LocalTenantId,
            entryDate:    date,
            memo:         "idempotency test",
            lines: new[]
            {
                new JournalEntryLine(new GLAccountId("1000"), amount, 0m),
                new JournalEntryLine(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            sourceReference: sourceReference)
        {
            ChartId    = new ChartOfAccountsId("CH-1"),
            SourceKind = sourceReference is null ? JournalEntrySource.Manual : JournalEntrySource.Invoice,
        };
}
