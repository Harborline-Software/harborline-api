using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

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

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-1047: <c>POST /api/local-node/bank-accounts</c> honours a durable <c>Idempotency-Key</c> scoped to the tenant and
/// the acting principal (ADR-0100; DES-0006). The fixture composes the production idempotency middleware with the routes and SQLite store.
/// </summary>
public sealed class BankAccountIdempotencyRouteTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TeamId OtherTeamId = new(Guid.Parse("7e570000-0000-0000-0000-0000000000bb"));
    private const string ReusedCode = "authorization.idempotency_key_reused";

    private readonly MutableAuthorizationContext _authorization = new();
    private string? _rawKeyOverride;
    private bool _writerAllowed = true;
    private int _writerDecisions;
    private readonly CommitHooks _hooks = new();
    private readonly MutableClock _clock = new(T0);
    private readonly SwitchableActiveTeam _team = new(NodeTestActiveTeam.TestTeamId);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private bool _retainDirectory;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private string _databasePath = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-bank-idem-" + Guid.NewGuid().ToString("N"));
        await StartHostAsync();
    }

    private async Task StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "bank-idem.db")};Pooling=False";
        builder.Services.AddSingleton<IHarborlineEntityModule, BankingEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, BankAccountCreateKeyEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString).AddInterceptors(_hooks));

        var authorization = _authorization;
        authorization.Allow(TeamRolePermissions.RecordsWrite);
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(authorization);
        builder.Services.AddSingleton<IActiveTeamAccessor>(_team);
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => authorization.HasPermission(permission)));

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
            await ctx.Database.OpenConnectionAsync();
            try { _databasePath = Path.GetFullPath(ctx.Database.GetDbConnection().DataSource); }
            finally { await ctx.Database.CloseConnectionAsync(); }
        }

        // The desktop plane; a request carrying X-Test-Party is a selected-session member instead.
        _app.Use(async (http, next) =>
        {
            // Preserve raw whitespace for contract tests; HTTP transports can strip header OWS.
            if (_rawKeyOverride is not null)
                http.Request.Headers[IdempotencyContract.HeaderName] = _rawKeyOverride;
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (http.Request.Headers["X-Test-Party"] is { Count: 1 } party)
                http.Features.Set(Member(party[0]!, NodeTenant.Resolve(_team)));
            await next(http);
        });

        AuthorizationDenialTranslation.Use(_app);
        NodeMutationIdempotency.UseOnce(_app, _clock);

        var repository = new NodeEfBankAccountRepository(_factory);
        BankingServices banking = (repository, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        BankAccountRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            banking,
            _team,
            new NodeBankAccountWriter(repository, Authorization.TestAuthorization.Gate(
                _ => _writerAllowed, _ => Interlocked.Increment(ref _writerDecisions)), createKeys: repository),
            _clock);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        await StopHostAsync();
        if (_retainDirectory) return;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private async Task StopHostAsync()
    {
        _client?.Dispose();
        _client = null!;
        var app = _app;
        _app = null!;
        if (app is null) return;
        try { await app.StopAsync(); }
        finally { await app.DisposeAsync(); }
    }

    [Fact(DisplayName = "T-1047: a keyed create retried after a restart replays the first 201 byte-identical; one account, one key")]
    public async Task KeyedRetry_ReplaysTheFirstResponse()
    {
        var first = await PostAsync("bank-key-1", "Ops Checking");
        await RestartHostAsync();
        var retry = await PostAsync("bank-key-1", "Ops Checking");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        // The bank account is record-is-audit (durable-write-classification.tsv): one row is one audit entry.
        var account = Assert.Single(await AccountsAsync());
        Assert.Equal("Ops Checking", account.DisplayName);
        Assert.Equal(account.Id.Value, Assert.Single(await KeysAsync()).AccountId);
        Assert.Equal($"/api/local-node/bank-accounts/{account.Id.Value}", first.Headers.Location!.OriginalString);
    }

    [Fact(DisplayName = "T-1047: the same key with a different body is refused 409 with the stable code, and nothing is written")]
    public async Task KeyReusedWithDifferentBody_IsRefused()
    {
        var first = await PostAsync("bank-key-2", "Ops Checking");
        var reused = await PostAsync("bank-key-2", "Payroll");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(ReusedCode, (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("Ops Checking", Assert.Single(await AccountsAsync()).DisplayName);
        Assert.Single(await KeysAsync());
    }

    [Fact(DisplayName = "T-1047: the same key from another principal is a separate request and creates its own account")]
    public async Task SameKeyFromAnotherPrincipal_IsASeparateRequest()
    {
        var operatorCreate = await PostAsync("bank-key-3", "Ops Checking");
        var memberCreate = await PostAsync("bank-key-3", "Ops Checking", party: "party:alice");

        Assert.Equal(HttpStatusCode.Created, operatorCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, memberCreate.StatusCode);
        Assert.NotEqual(operatorCreate.Headers.Location, memberCreate.Headers.Location);
        Assert.Equal(2, (await AccountsAsync()).Count);
        Assert.Contains(await KeysAsync(), key => key.Principal == "party:alice");
    }

    [Fact(DisplayName = "T-1047: the same key in another tenant is a separate request and creates that tenant's account")]
    public async Task SameKeyInAnotherTenant_IsASeparateRequest()
    {
        var home = await PostAsync("bank-key-4", "Ops Checking");
        _team.Switch(OtherTeamId);
        var other = await PostAsync("bank-key-4", "Ops Checking");

        Assert.Equal(HttpStatusCode.Created, home.StatusCode);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.NotEqual(home.Headers.Location, other.Headers.Location);
        var accounts = await AccountsAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal(2, accounts.Select(a => a.TenantId).Distinct().Count());
    }

    [Fact(DisplayName = "T-1047: two concurrent creates of one key, both past the key lookup, commit exactly one account and both get it")]
    public async Task ConcurrentSameKey_CommitsOneAccount()
    {
        Task<HttpResponseMessage>? loser = null;
        // Hold the winner at its insert, start the loser, and release the winner only after the loser's key
        // lookup has run against the store and missed. The loser then fails its commit on the key and replays.
        _hooks.BeforeKeyInsert = async () =>
        {
            var loserLookedUp = _hooks.ArmNextKeyLookup();
            loser = PostAsync("bank-key-5", "Ops Checking");
            await loserLookedUp;
        };

        var winner = await PostAsync("bank-key-5", "Ops Checking");
        var lost = await loser!;

        Assert.Equal(HttpStatusCode.Created, winner.StatusCode);
        Assert.Equal(HttpStatusCode.Created, lost.StatusCode);
        Assert.Equal(await winner.Content.ReadAsStringAsync(), await lost.Content.ReadAsStringAsync());
        Assert.Single(await AccountsAsync());
        Assert.Single(await KeysAsync());
    }

    [Fact(DisplayName = "T-1047: an exception at the account's insert rolls back the key and account, and a retry creates it once")]
    public async Task CrashAtCommit_LeavesNeither_AndARetrySucceedsOnce()
    {
        _hooks.FailAccountInsert = true;
        var failed = await PostAsync("bank-key-6", "Ops Checking");

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Empty(await AccountsAsync());
        Assert.Empty(await KeysAsync());

        _hooks.FailAccountInsert = false;
        var retry = await PostAsync("bank-key-6", "Ops Checking");
        var again = await PostAsync("bank-key-6", "Ops Checking");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(retry.Headers.Location, again.Headers.Location);
        Assert.Single(await AccountsAsync());
        Assert.Single(await KeysAsync());
    }

    [Fact(DisplayName = "T-1047: killing the HTTP writer between physical inserts leaves neither row; retry and replay create one account")]
    public async Task ProcessLossBetweenInserts_RetryCreatesExactlyOnce()
    {
        await StopHostAsync();
        await RunChildAsync(ChildOutcome.KilledBetweenInserts);
        await StartHostAsync();
        Assert.Empty(await AccountsAsync());
        Assert.Empty(await KeysAsync());

        using var retry = await PostAsync("bank-process-loss", "Ops Checking");
        using var replay = await PostAsync("bank-process-loss", "Ops Checking");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await retry.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(retry.Headers.Location, replay.Headers.Location);
        await AssertSingleCreatedPairAsync(retry);
    }

    [Fact(DisplayName = "T-1047: the same child completes both physical inserts and HTTP 201; a fresh host replays its durable response")]
    public async Task BetweenInsertControl_CompletesBothRows()
    {
        await StopHostAsync();
        await RunChildAsync(ChildOutcome.Completed);
        await StartHostAsync();
        Assert.Single(await AccountsAsync());
        var committed = Assert.Single(await KeysAsync());
        using var replay = await PostAsync("bank-process-loss", "Ops Checking");
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.Deserialize<JsonElement>(committed.Response),
            JsonSerializer.Deserialize<JsonElement>(await replay.Content.ReadAsStringAsync())));
        await AssertSingleCreatedPairAsync(replay);
    }

    private async Task AssertSingleCreatedPairAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = Assert.Single(await AccountsAsync());
        var key = Assert.Single(await KeysAsync());
        Assert.Equal("Ops Checking", account.DisplayName);
        Assert.Equal("7e570000-0000-0000-0000-0000000000aa", account.TenantId.Value);
        Assert.Equal("7e570000-0000-0000-0000-0000000000aa", key.TenantId);
        Assert.Equal("desktop-operator-under-test", key.Principal);
        Assert.Equal("bank-process-loss", key.Key);
        Assert.Equal(account.Id.Value, key.AccountId);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.Deserialize<JsonElement>(key.Response),
            JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())));
        Assert.Equal($"/api/local-node/bank-accounts/{account.Id.Value}", response.Headers.Location!.OriginalString);
    }

    private const string ChildSettingsVariable = "HARBORLINE_T1047_BANK_CHILD";
    private const int ChildFailureExit = 71;
    private const int ChildDeadlineExit = 72;
    private enum ChildOutcome { KilledBetweenInserts, Completed }
    private enum InsertTable { Account, Key }
    private sealed record ChildSettings(string Directory, string Nonce, ChildOutcome Outcome);
    private sealed record Ready(string Nonce, string Database, int Pid, string Phase,
        InsertTable First, InsertTable Next, long Accounts, long Keys, bool HasTransaction);

    [ModuleInitializer]
    internal static void StartBankChildIfSelected()
    {
        var selected = Environment.GetEnvironmentVariable(ChildSettingsVariable);
        if (selected is null) return;
        // Return from module initialization before the worker awaits HTTP callbacks in this assembly.
        new Thread(() =>
        {
            using var watchdog = new Timer(_ => Environment.Exit(ChildDeadlineExit), null,
                TimeSpan.FromSeconds(300), Timeout.InfiniteTimeSpan);
            try
            {
                var settings = JsonSerializer.Deserialize<ChildSettings>(selected)
                    ?? throw new InvalidOperationException("Missing bank child settings.");
                if (!Path.IsPathFullyQualified(settings.Directory) ||
                    !string.Equals(settings.Directory, Path.GetFullPath(settings.Directory), StringComparison.Ordinal) ||
                    !File.Exists(Path.Combine(settings.Directory, "bank-idem.db")) ||
                    !Guid.TryParseExact(settings.Nonce, "N", out _) || !Enum.IsDefined(settings.Outcome))
                    throw new InvalidOperationException("Invalid bank child settings.");
                RunRequestInChildAsync(settings).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.Exit(ChildFailureExit);
            }
        }) { IsBackground = false }.Start();
    }

    private static async Task RunRequestInChildAsync(ChildSettings settings)
    {
        var fixture = new BankAccountIdempotencyRouteTests { _dir = settings.Directory };
        fixture._hooks.BetweenInserts = new BetweenInsertBarrier(settings);
        await fixture.StartHostAsync();
        fixture._client.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await fixture.PostAsync("bank-process-loss", "Ops Checking");
        Assert.Equal(ChildOutcome.Completed, settings.Outcome);
        await fixture.AssertSingleCreatedPairAsync(response);
        await fixture.StopHostAsync();
    }

    private async Task RunChildAsync(ChildOutcome outcome)
    {
        var settings = new ChildSettings(Path.GetFullPath(_dir), Guid.NewGuid().ToString("N"), outcome);
        var readyPath = Path.Combine(settings.Directory, "bank-child-ready.json");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(BankAccountIdempotencyRouteTests).Assembly.Location);
        start.Environment.Remove("HARBORLINE_T1048H_NARROWING_CHILD_DIRECTORY");
        start.Environment.Remove("HARBORLINE_T1048_MEMBERSHIP_CHILD_DIRECTORY");
        start.Environment.Remove("HARBORLINE_T1048G_ERASURE_CHILD_DB");
        start.Environment.Remove("HARBORLINE_LOGOUT_PROCESS_TEST_DIRECTORY");
        start.Environment.Remove("HARBORLINE_LOGOUT_PROCESS_TEST_PHASE");
        start.Environment[ChildSettingsVariable] = JsonSerializer.Serialize(settings);
        var process = new Process { StartInfo = start };
        var stopReaders = new CancellationTokenSource();
        Task<string>? output = null;
        Task<string>? error = null;
        Task? completion = null;
        Task? kill = null;
        var started = false;
        var closed = false;
        var pid = 0;
        Exception? failure = null;
        var startup = Stopwatch.StartNew();
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException("Could not start the bank child.");
            _retainDirectory = true;
            pid = process.Id;
            output = process.StandardOutput.ReadToEndAsync(stopReaders.Token);
            error = process.StandardError.ReadToEndAsync(stopReaders.Token);
            ObserveFault(output);
            ObserveFault(error);
            var exit = process.WaitForExitAsync(stopReaders.Token);
            ObserveFault(exit);
            completion = Task.WhenAll(exit, output, error);
            ObserveFault(completion);
            while (!File.Exists(readyPath))
            {
                if (output.IsFaulted) await output;
                if (error.IsFaulted) await error;
                if (process.HasExited && !File.Exists(readyPath))
                    throw new InvalidOperationException("Bank child exited before reaching the physical INSERT gap.");
                await Task.Delay(TimeSpan.FromMilliseconds(25)).WaitAsync(Remaining(startup, 120));
            }

            var readReady = File.ReadAllTextAsync(readyPath);
            ObserveFault(readReady);
            var ready = JsonSerializer.Deserialize<Ready>(await readReady.WaitAsync(Remaining(startup, 120)))
                ?? throw new InvalidOperationException("Missing bank child readiness.");
            Console.WriteLine($"T1047 readiness {JsonSerializer.Serialize(ready)}; parent database={_databasePath}");
            Assert.Equal(settings.Nonce, ready.Nonce);
            Assert.Equal(_databasePath, ready.Database);
            Assert.Equal(pid, ready.Pid);
            Assert.Equal("between-inserts", ready.Phase);
            Assert.True(
                ready is { First: InsertTable.Account, Next: InsertTable.Key, Accounts: 1, Keys: 0 } or
                    { First: InsertTable.Key, Next: InsertTable.Account, Accounts: 0, Keys: 1 },
                "Readiness must prove one physical row and an opposite pending INSERT.");

            var finishing = Stopwatch.StartNew();
            if (outcome == ChildOutcome.KilledBetweenInserts)
            {
                Assert.False(process.HasExited, "The child must remain alive at the physical INSERT gap until the parent kills it.");
                kill = Task.Run(() => process.Kill(entireProcessTree: true));
                ObserveFault(kill);
                await kill.WaitAsync(Remaining(finishing, 30));
            }
            await completion.WaitAsync(Remaining(finishing, 30));
            closed = true;
            Console.WriteLine($"T1047 child PID={pid}; outcome={outcome}; exit={process.ExitCode}; both pipes drained\n{output.Result}\n{error.Result}");
            if (outcome == ChildOutcome.Completed) Assert.Equal(0, process.ExitCode);
            else
            {
                Assert.NotEqual(0, process.ExitCode);
                Assert.NotEqual(ChildFailureExit, process.ExitCode);
                Assert.NotEqual(ChildDeadlineExit, process.ExitCode);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            exception.Data["T1047.child"] = Diagnostics();
            throw;
        }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            string? cleanupFailure = null;
            if (started && !closed)
            {
                try
                {
                    // Reuse a pending kill so neither another kill nor disposal races its Process handle.
                    kill ??= Task.Run(() => { if (!process.HasExited) process.Kill(entireProcessTree: true); });
                    ObserveFault(kill);
                    await kill.WaitAsync(Remaining(cleanup, 30));
                    if (completion is null) throw new InvalidOperationException("Child output drains were not established.");
                    await completion.WaitAsync(Remaining(cleanup, 30));
                    closed = true;
                }
                catch (Exception exception) { cleanupFailure = exception + "\n" + Diagnostics(); }
            }
            _retainDirectory = started && !closed;
            var disposal = (kill ?? Task.CompletedTask).ContinueWith(_ =>
            {
                try { stopReaders.Cancel(); }
                finally
                {
                    try { process.Dispose(); }
                    finally { stopReaders.Dispose(); }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            ObserveFault(disposal);
            try { await disposal.WaitAsync(Remaining(cleanup, 30)); }
            catch (Exception exception) { cleanupFailure += "\nDisposal: " + exception; }
            if (failure is not null)
            {
                failure.Data["T1047.child"] = Diagnostics();
                if (cleanupFailure is not null) failure.Data["T1047.cleanup"] = cleanupFailure;
                throw new InvalidOperationException(
                    $"{failure.Message}\n{Diagnostics()}" + (cleanupFailure is null ? "" : $"\ncleanup:\n{cleanupFailure}"),
                    failure);
            }
            if (cleanupFailure is not null) throw new InvalidOperationException(cleanupFailure);
        }

        string Diagnostics() => $"PID={pid}; directory={settings.Directory}; confirmed closure={closed}\n" +
            $"stdout:\n{(output?.IsCompletedSuccessfully == true ? output.Result : "<incomplete>")}\n" +
            $"stderr:\n{(error?.IsCompletedSuccessfully == true ? error.Result : "<incomplete>")}";

        static TimeSpan Remaining(Stopwatch clock, int seconds)
        {
            var remaining = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException($"Bank child exceeded its {seconds}-second stage budget.");
            return remaining;
        }

        static void ObserveFault(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [Fact(DisplayName = "T-1047: a key is remembered for 24 hours; one second before it replays, at 24 hours it creates a new account")]
    public async Task KeyExpiresAfter24Hours()
    {
        var first = await PostAsync("bank-key-7", "Ops Checking");
        await RestartHostAsync();
        _clock.Now = T0 + TimeSpan.FromHours(23);
        var nearExpiry = await PostAsync("bank-key-7", "Ops Checking");
        Assert.Equal(first.Headers.Location, nearExpiry.Headers.Location);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), Assert.Single(await KeysAsync()).ExpiresAt);
        _clock.Now = T0 + TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        var withinWindow = await PostAsync("bank-key-7", "Ops Checking");
        _clock.Now = T0 + TimeSpan.FromHours(24);
        var afterWindow = await PostAsync("bank-key-7", "Ops Checking");

        Assert.Equal(first.Headers.Location, withinWindow.Headers.Location);
        Assert.Equal(HttpStatusCode.Created, afterWindow.StatusCode);
        Assert.NotEqual(first.Headers.Location, afterWindow.Headers.Location);
        Assert.Equal(2, (await AccountsAsync()).Count);
        var key = Assert.Single(await KeysAsync());
        Assert.Equal(afterWindow.Headers.Location!.OriginalString, $"/api/local-node/bank-accounts/{key.AccountId}");
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero), key.ExpiresAt);
    }

    [Fact(DisplayName = "T-1047: without a key each create makes its own account and no key is stored")]
    public async Task NoKey_CreatesEachTime()
    {
        var first = await PostAsync(key: null, "Ops Checking");
        var second = await PostAsync(key: null, "Ops Checking");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, (await AccountsAsync()).Count);
        Assert.Empty(await KeysAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task TrimmedKey_UsesOneBoundedDurableIdentityWarmAndAfterRestart(int length)
    {
        var canonical = new string('k', length);
        _rawKeyOverride = " \t\u00a0" + canonical + "\u00a0\t ";
        var first = await PostAsync("placeholder", "Ops Checking");
        _rawKeyOverride = null;
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(canonical, Assert.Single(await KeysAsync()).Key);

        var warm = await PostAsync(canonical, "Ops Checking");
        Assert.Equal(HttpStatusCode.Created, warm.StatusCode);
        Assert.Equal(first.Headers.Location, warm.Headers.Location);
        await RestartHostAsync();
        var cold = await PostAsync(canonical, "Ops Checking");
        Assert.Equal(HttpStatusCode.Created, cold.StatusCode);
        Assert.Equal(first.Headers.Location, cold.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await cold.Content.ReadAsStringAsync());
        Assert.Single(await AccountsAsync());
        Assert.Equal(canonical, Assert.Single(await KeysAsync()).Key);
    }

    [Fact]
    public async Task TrimmedKey_AboveTwoHundredCharactersIsRejectedBeforeDurableCreate()
    {
        _rawKeyOverride = " " + new string('k', 201) + " ";
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("placeholder", "Ops Checking")).StatusCode);
        Assert.Empty(await AccountsAsync());
        Assert.Empty(await KeysAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("one,two")]
    public async Task InvalidKey_IsRejectedBeforeDurableCreate(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BankAccountRoutes.RouteBase)
        {
            Content = JsonContent.Create(new { displayName = "Ops Checking" }),
        };
        request.Headers.TryAddWithoutValidation(IdempotencyContract.HeaderName, key);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
        Assert.Empty(await AccountsAsync());
        Assert.Empty(await KeysAsync());
    }

    private async Task RestartHostAsync()
    {
        await StopHostAsync();
        await StartHostAsync();
    }

    [Fact]
    public async Task WarmReplay_RechecksRoutePermission()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostAsync("revoked", "Ops Checking")).StatusCode);
        _authorization.DenyAll();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("revoked", "Ops Checking")).StatusCode);
        Assert.Single(await AccountsAsync());
        Assert.Single(await KeysAsync());
    }

    [Fact]
    public async Task WarmReplay_RechecksWriterPermission()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostAsync("writer-revoked", "Ops Checking")).StatusCode);
        _writerAllowed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("writer-revoked", "Ops Checking")).StatusCode);
        Assert.Equal(2, _writerDecisions);
        Assert.Single(await AccountsAsync());
        Assert.Single(await KeysAsync());
    }

    [Fact]
    public async Task EquivalentBodies_ReplayWarmAndAfterRestart()
    {
        var first = await PostAsync("normalized", "Ops Checking");
        const string equivalent = "{\"currencyCode\":\"USD\",\"institutionName\":\" First Harbor \" ,\"displayName\":\" Ops Checking \"}";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BankAccountRoutes.RouteBase)
            {
                Content = new StringContent(equivalent, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(IdempotencyContract.HeaderName, "normalized");
            var replay = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(first.Headers.Location, replay.Headers.Location);
            Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            await RestartHostAsync();
        }
        Assert.Single(await AccountsAsync());
        Assert.Single(await KeysAsync());
    }

    private Task<HttpResponseMessage> PostAsync(string? key, string displayName, string? party = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BankAccountRoutes.RouteBase)
        {
            Content = JsonContent.Create(new { displayName, institutionName = "First Harbor", currencyCode = "usd" }),
        };
        if (key is not null)
            request.Headers.Add(IdempotencyContract.HeaderName, key);
        if (party is not null)
            request.Headers.Add("X-Test-Party", party);
        return _client.SendAsync(request);
    }

    // A projection: a node-created account's ledger ref has no chart id, and the full row does not read back
    // (ChartOfAccountsId refuses a null), which is outside this ticket.
    private async Task<List<(BankAccountId Id, TenantId TenantId, string DisplayName)>> AccountsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var rows = await ctx.Set<BankAccount>().AsNoTracking()
            .Select(a => new { a.Id, a.TenantId, a.DisplayName })
            .ToListAsync();
        return rows.Select(a => (a.Id, a.TenantId, a.DisplayName)).ToList();
    }

    private async Task<List<BankAccountCreateKeyRow>> KeysAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<BankAccountCreateKeyRow>().AsNoTracking().ToListAsync();
    }

    private static SelectedSessionRequestPrincipal Member(string canonicalParty, TenantId tenant) => new(
        accountId: "account-" + canonicalParty,
        tenantId: tenant,
        principalUserId: new PrincipalUserId("principal-" + canonicalParty),
        canonicalParty: new CanonicalPartyReference(canonicalParty),
        membershipId: "membership-" + canonicalParty,
        membershipOwnerVersion: 1,
        pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-" + canonicalParty, 1)],
        authorizationEpoch: 1,
        sessionCorrelationId: "session-" + canonicalParty,
        coordinationCorrelationId: "coordination-" + canonicalParty);

    private sealed class BetweenInsertBarrier(ChildSettings settings)
    {
        private InsertTable? _first;

        public async Task BeforeAsync(DbCommand command)
        {
            const string accountInsert = "INSERT INTO \"bank_accounts\"";
            const string keyInsert = "INSERT INTO \"bank_account_create_keys\"";
            var accountAt = command.CommandText.IndexOf(accountInsert, StringComparison.Ordinal);
            var keyAt = command.CommandText.IndexOf(keyInsert, StringComparison.Ordinal);
            if (accountAt < 0 && keyAt < 0) return;
            if ((accountAt >= 0 && keyAt >= 0) ||
                (accountAt >= 0 && command.CommandText.IndexOf(accountInsert, accountAt + accountInsert.Length, StringComparison.Ordinal) >= 0) ||
                (keyAt >= 0 && command.CommandText.IndexOf(keyInsert, keyAt + keyInsert.Length, StringComparison.Ordinal) >= 0))
                throw new InvalidOperationException("Batched participant INSERTs cannot prove a between-inserts gap.");
            var next = accountAt >= 0 ? InsertTable.Account : InsertTable.Key;
            if (_first == next) throw new InvalidOperationException("Repeated participant INSERT cannot establish the opposite-table gap.");

            var connection = command.Connection ?? throw new InvalidOperationException("INSERT has no connection.");
            await using var count = connection.CreateCommand();
            count.Transaction = command.Transaction;
            count.CommandText = "SELECT COUNT(*) FROM \"bank_accounts\"";
            var accounts = (long)(await count.ExecuteScalarAsync())!;
            count.CommandText = "SELECT COUNT(*) FROM \"bank_account_create_keys\"";
            var keys = (long)(await count.ExecuteScalarAsync())!;
            if (_first is null)
            {
                Assert.Equal(0L, accounts);
                Assert.Equal(0L, keys);
                _first = next;
                return;
            }

            Assert.Equal(_first == InsertTable.Account ? 1L : 0L, accounts);
            Assert.Equal(_first == InsertTable.Key ? 1L : 0L, keys);
            var ready = new Ready(settings.Nonce, Path.GetFullPath(connection.DataSource), Environment.ProcessId,
                "between-inserts", _first.Value, next, accounts, keys, command.Transaction is not null);
            var path = Path.Combine(settings.Directory, "bank-child-ready.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(ready));
            File.Move(path + ".tmp", path);
            if (settings.Outcome == ChildOutcome.KilledBetweenInserts)
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    private sealed class CommitHooks : DbCommandInterceptor
    {
        private Func<Task>? _beforeKeyInsert;
        private TaskCompletionSource? _nextKeyLookup;

        public Func<Task>? BeforeKeyInsert { set => _beforeKeyInsert = value; }

        public volatile bool FailAccountInsert;
        public BetweenInsertBarrier? BetweenInserts { get; set; }

        public Task ArmNextKeyLookup()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextKeyLookup = signal;
            return signal.Task;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await BeforeAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            await BeforeAsync(command);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("FROM \"bank_account_create_keys\"", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _nextKeyLookup, null)?.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }

        private async Task BeforeAsync(DbCommand command)
        {
            if (BetweenInserts is { } barrier)
            {
                try { await barrier.BeforeAsync(command); }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception);
                    throw;
                }
            }
            if (FailAccountInsert && command.CommandText.Contains("INSERT INTO \"bank_accounts\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected exception before the bank account insert.");
            if (command.CommandText.Contains("INSERT INTO \"bank_account_create_keys\"", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _beforeKeyInsert, null) is { } gate)
            {
                await gate();
            }
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SwitchableActiveTeam(TeamId initial) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; private set; } = Context(initial);

        public void Switch(TeamId team) => Active = Context(team);

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct)
        {
            Switch(teamId);
            return Task.CompletedTask;
        }

#pragma warning disable CS0067 // The accessor contract carries the event; nothing here subscribes.
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067

        private static TeamContext Context(TeamId team) =>
            new(team, "Test Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
    }
}
