using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.ExecutionRuntime;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ExecutionRuntime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.ExecutionRuntime;

public sealed class NodeExecutionReceiptStoreTests
{
    private static readonly TenantId Tenant = new("receipt-tenant");
    private static readonly RunId Run = new(new RunKind("workflow-run"), Guid.Parse("11111111-1111-1111-1111-111111111111"));

    // Oracle: DES-0056 eng-1, ck-3, ck-4 and slice acceptance 1: literal identity,
    // decision, status and ordered step values survive disposal/recomposition on the same SQLCipher file.
    [Fact(DisplayName = "execution-runtime-eng-1 / execution-runtime-ck-3 / execution-runtime-ck-4: receipt and trace survive a new host on the same encrypted database")]
    [Trait("Holds", "execution-runtime-eng-1")]
    [Trait("Holds", "execution-runtime-ck-3")]
    [Trait("Holds", "execution-runtime-ck-4")]
    public async Task Receipt_and_trace_survive_restart()
    {
        using var fixture = new ReceiptDatabase();
        using (var first = fixture.Open())
        {
            var store = first.GetRequiredService<IExecutionReceiptStore>();
            Assert.IsType<NodeExecutionReceiptStore>(store);
            await store.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "decision-17"));
            await store.AppendTraceStepAsync(Tenant, Run, new(1, "start", "ok"));
            await store.AppendTraceStepAsync(Tenant, Run, new(2, "finish", "done"));
        }
        using var restarted = fixture.Open();
        var rebuilt = restarted.GetRequiredService<IExecutionReceiptStore>();
        var receipt = Assert.IsType<ExecutionReceipt>(await rebuilt.GetReceiptAsync(Tenant, Run));
        Assert.Equal("receipt-tenant", receipt.TenantId.Value);
        Assert.Equal("workflow-run", receipt.RunId.Kind.Value);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), receipt.RunId.Value);
        Assert.Equal(RunStatus.Running, receipt.Status);
        Assert.Equal("decision-17", receipt.AccessDecisionId);
        Assert.Equal(new[] { new ExecutionTraceStep(1, "start", "ok"), new ExecutionTraceStep(2, "finish", "done") }, await rebuilt.GetTraceAsync(Tenant, Run));
    }

    // Oracle: pinned platform ExecutionReceipt.cs and interface.yaml executionReceiptAndTrace;
    // each result is also pinned to a literal, including the reference's empty kindless reads.
    [Fact(DisplayName = "execution-runtime-eng-1: durable store matches every reference result and refusal")]
    [Trait("Holds", "execution-runtime-eng-1")]
    public async Task Differential_results_and_refusals_match_the_pinned_reference()
    {
        using var fixture = new ReceiptDatabase();
        using var provider = fixture.Open();
        var durable = provider.GetRequiredService<IExecutionReceiptStore>();
        var expected = await Exercise(new InMemoryExecutionReceiptStore());
        var actual = await Exercise(durable);
        Assert.Equal(expected, actual);
        Assert.Equal(new[] { "ok", "ok", "execution.access_decision_changed", "execution.transition_illegal",
            "ok", "ok", "execution.trace_step_out_of_order", "execution.trace_step_out_of_order",
            "execution.execution_receipt_invalid", "execution.trace_step_invalid", "ok", "ok", "ok", "ok",
            "ArgumentException", "ArgumentException", "ArgumentException", "ArgumentException" }, actual);
        Assert.Equal(new ExecutionReceipt(Tenant, Run, RunStatus.Running, "decision"), await durable.GetReceiptAsync(Tenant, Run));
        Assert.Equal(new[] { new ExecutionTraceStep(1, "start", "ok") }, await durable.GetTraceAsync(Tenant, Run));
    }

    // Oracle: literal six-by-six transition table in pinned RunStatus.cs; parity with
    // the independently shipped reference plus literal wire names guards every persisted status.
    [Fact(DisplayName = "execution-runtime-eng-1: every stored status and transition matches the closed platform table")]
    [Trait("Holds", "execution-runtime-eng-1")]
    public async Task Every_status_and_transition_matches_reference()
    {
        using var fixture = new ReceiptDatabase();
        using var provider = fixture.Open();
        var durable = provider.GetRequiredService<IExecutionReceiptStore>();
        var reference = new InMemoryExecutionReceiptStore();
        string[] wireNames = ["pending", "running", "awaiting-retry", "succeeded", "dead-lettered", "cancelled"];
        string[] allowed = ["110001", "011111", "011001", "000100", "000010", "000001"];
        for (var from = 0; from < 6; from++)
        for (var to = 0; to < 6; to++)
        {
            var run = new RunId(new RunKind("workflow-run"), Guid.NewGuid());
            await durable.PutReceiptAsync(new(Tenant, run, (RunStatus)from, null));
            await reference.PutReceiptAsync(new(Tenant, run, (RunStatus)from, null));
            var receipt = new ExecutionReceipt(Tenant, run, (RunStatus)to, null);
            var expected = await Record.ExceptionAsync(() => reference.PutReceiptAsync(receipt).AsTask());
            var actual = await Record.ExceptionAsync(() => durable.PutReceiptAsync(receipt).AsTask());
            if (allowed[from][to] == '1')
            {
                Assert.Null(expected);
                Assert.Null(actual);
            }
            else
            {
                Assert.Equal("execution.transition_illegal", Assert.IsType<ExecutionRuntimeRefusedException>(expected).Code);
                Assert.Equal("execution.transition_illegal", Assert.IsType<ExecutionRuntimeRefusedException>(actual).Code);
            }
            Assert.Equal(await reference.GetReceiptAsync(Tenant, run), await durable.GetReceiptAsync(Tenant, run));
            using var db = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContext();
            Assert.Equal(wireNames[allowed[from][to] == '1' ? to : from],
                (await db.Set<ExecutionReceiptRow>().SingleAsync(row => row.RunValue == run.Value)).Status);
        }
    }

    private static async Task<string[]> Exercise(IExecutionReceiptStore store)
    {
        var results = new List<string>();
        async Task Call(Func<ValueTask> call)
        {
            try { await call(); results.Add("ok"); }
            catch (ExecutionRuntimeRefusedException refused) { results.Add(refused.Code); }
            catch (ArgumentException) { results.Add("ArgumentException"); }
        }
        await Call(() => store.PutReceiptAsync(new(Tenant, Run, RunStatus.Pending, "decision")));
        await Call(() => store.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "decision")));
        await Call(() => store.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "other-decision")));
        await Call(() => store.PutReceiptAsync(new(Tenant, Run, RunStatus.Pending, "decision")));
        await Call(() => store.AppendTraceStepAsync(Tenant, Run, new(1, "start", "ok")));
        await Call(() => store.AppendTraceStepAsync(Tenant, Run, new(1, "start", "ok")));
        await Call(() => store.AppendTraceStepAsync(Tenant, Run, new(1, "changed", "ok")));
        await Call(() => store.AppendTraceStepAsync(Tenant, Run, new(3, "gap", "ok")));
        await Call(() => store.PutReceiptAsync(new(Tenant, default, RunStatus.Running, null)));
        await Call(() => store.AppendTraceStepAsync(Tenant, default, new(1, "start", "ok")));
        await Call(async () => Assert.Null(await store.GetReceiptAsync(Tenant, default)));
        await Call(async () => Assert.Empty(await store.GetTraceAsync(Tenant, default)));
        var empty = new RunId(new RunKind("workflow-run"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
        await Call(async () => Assert.Null(await store.GetReceiptAsync(Tenant, empty)));
        await Call(async () => Assert.Empty(await store.GetTraceAsync(Tenant, empty)));
        await Call(() => store.PutReceiptAsync(new(default, Run, RunStatus.Running, null)));
        await Call(() => store.AppendTraceStepAsync(default, Run, new(1, "start", "ok")));
        await Call(async () => { await store.GetReceiptAsync(default, Run); });
        await Call(async () => { await store.GetTraceAsync(default, Run); });
        return results.ToArray();
    }

    // Oracle: slice acceptance 2 and 4; literal counts and distinct payloads, not production keys.
    [Fact(DisplayName = "execution-runtime-ck-3: replay adds no rows and tenant and kind scope never merge")]
    [Trait("Holds", "execution-runtime-ck-3")]
    [Trait("Holds", "execution-runtime-ck-4")]
    public async Task Replay_and_tenant_and_kind_scoping()
    {
        using var fixture = new ReceiptDatabase();
        using var provider = fixture.Open();
        var store = provider.GetRequiredService<IExecutionReceiptStore>();
        var otherTenant = new TenantId("other-tenant");
        var otherKind = new RunId(new RunKind("report-run"), Run.Value);
        await store.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "one"));
        await store.AppendTraceStepAsync(Tenant, Run, new(1, "first", "ok"));
        Assert.Null(await store.GetReceiptAsync(otherTenant, Run));
        Assert.Empty(await store.GetTraceAsync(otherTenant, Run));
        Assert.Null(await store.GetReceiptAsync(Tenant, otherKind));
        Assert.Empty(await store.GetTraceAsync(Tenant, otherKind));
        await store.PutReceiptAsync(new(otherTenant, Run, RunStatus.Pending, "two"));
        await store.AppendTraceStepAsync(otherTenant, Run, new(1, "second", "waiting"));
        await store.PutReceiptAsync(new(Tenant, otherKind, RunStatus.Succeeded, null));
        await store.AppendTraceStepAsync(Tenant, otherKind, new(1, "third", "done"));
        await store.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "one"));
        await store.AppendTraceStepAsync(Tenant, Run, new(1, "first", "ok"));
        Assert.Equal("one", (await store.GetReceiptAsync(Tenant, Run))!.AccessDecisionId);
        Assert.Equal("two", (await store.GetReceiptAsync(otherTenant, Run))!.AccessDecisionId);
        Assert.Null((await store.GetReceiptAsync(Tenant, otherKind))!.AccessDecisionId);
        Assert.Equal("first", Assert.Single(await store.GetTraceAsync(Tenant, Run)).Phase);
        Assert.Equal("second", Assert.Single(await store.GetTraceAsync(otherTenant, Run)).Phase);
        Assert.Equal("third", Assert.Single(await store.GetTraceAsync(Tenant, otherKind)).Phase);
        using var db = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContext();
        Assert.Equal(3, await db.Set<ExecutionReceiptRow>().CountAsync());
        Assert.Equal(3, await db.Set<ExecutionTraceStepRow>().CountAsync());
        Assert.Equal(new[] { "pending", "running", "succeeded" }, await db.Set<ExecutionReceiptRow>().Select(row => row.Status).OrderBy(status => status).ToArrayAsync());
    }

    // Oracle: the platform's locked reference decision; one row and a literal loser code.
    [Theory(DisplayName = "execution-runtime-eng-1: concurrent appends serialize replay and refusal across hosts")]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Holds", "execution-runtime-eng-1")]
    public async Task Concurrent_appends_leave_one_step(bool identical)
    {
        using var fixture = new ReceiptDatabase();
        using var first = fixture.Open();
        using var second = fixture.Open();
        var a = first.GetRequiredService<IExecutionReceiptStore>();
        var b = second.GetRequiredService<IExecutionReceiptStore>();
        var results = await Race(
            () => a.AppendTraceStepAsync(Tenant, Run, new(1, "start", "ok")),
            () => b.AppendTraceStepAsync(Tenant, Run, new(1, identical ? "start" : "other", "ok")));
        Assert.Equal(identical ? new[] { "ok", "ok" } : new[] { "execution.trace_step_out_of_order", "ok" }, results);
        Assert.Single(await a.GetTraceAsync(Tenant, Run));
        using var db = first.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContext();
        Assert.Equal(1, await db.Set<ExecutionTraceStepRow>().CountAsync());
    }

    [Fact(DisplayName = "execution-runtime-ck-3: competing puts decide against the durable receipt in the transaction")]
    [Trait("Holds", "execution-runtime-ck-3")]
    public async Task Concurrent_puts_preserve_the_winning_decision()
    {
        using var fixture = new ReceiptDatabase();
        using var first = fixture.Open();
        using var second = fixture.Open();
        var a = first.GetRequiredService<IExecutionReceiptStore>();
        var b = second.GetRequiredService<IExecutionReceiptStore>();
        Assert.Equal(new[] { "execution.access_decision_changed", "ok" }, await Race(
            () => a.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "a")),
            () => b.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, "b"))));
        using var db = first.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContext();
        Assert.Equal(1, await db.Set<ExecutionReceiptRow>().CountAsync());
        var winner = (await a.GetReceiptAsync(Tenant, Run))!.AccessDecisionId;
        Assert.Contains(winner, new[] { "a", "b" });
        Assert.Equal(winner, (await b.GetReceiptAsync(Tenant, Run))!.AccessDecisionId);
    }

    // Oracle: port requires ordinal order regardless of the database's unordered scan plan.
    [Fact(DisplayName = "execution-runtime-ck-4: trace order survives a reversed unordered database scan")]
    [Trait("Holds", "execution-runtime-ck-4")]
    public async Task Trace_has_explicit_ordinal_order()
    {
        using var fixture = new ReceiptDatabase();
        using var provider = fixture.Open();
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        var store = new NodeExecutionReceiptStore(new ReverseScanFactory(factory));
        await store.AppendTraceStepAsync(Tenant, Run, new(1, "start", "ok"));
        await store.AppendTraceStepAsync(Tenant, Run, new(2, "middle", "ok"));
        await store.AppendTraceStepAsync(Tenant, Run, new(3, "end", "ok"));
        Assert.Equal(new[] { 1, 2, 3 }, (await store.GetTraceAsync(Tenant, Run)).Select(step => step.Ordinal));
    }

    private sealed class ReverseScanFactory(IDbContextFactory<LocalNodeDbContext> inner) : IDbContextFactory<LocalNodeDbContext>
    {
        public LocalNodeDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            db.Database.OpenConnection();
            db.Database.ExecuteSqlRaw("PRAGMA reverse_unordered_selects=ON;");
            return db;
        }
    }

    // Oracle: literal transition table: neither terminal status may leave itself.
    [Fact(DisplayName = "execution-runtime-ck-3: racing terminal puts validate against the stored predecessor")]
    [Trait("Holds", "execution-runtime-ck-3")]
    public async Task Concurrent_terminal_puts_refuse_the_loser()
    {
        using var fixture = new ReceiptDatabase();
        using var first = fixture.Open();
        using var second = fixture.Open();
        var left = first.GetRequiredService<IExecutionReceiptStore>();
        var right = second.GetRequiredService<IExecutionReceiptStore>();
        await left.PutReceiptAsync(new(Tenant, Run, RunStatus.Running, null));
        Assert.Equal(new[] { "execution.transition_illegal", "ok" }, await Race(
            () => left.PutReceiptAsync(new(Tenant, Run, RunStatus.Succeeded, null)),
            () => right.PutReceiptAsync(new(Tenant, Run, RunStatus.Cancelled, null))));
        var stored = (await right.GetReceiptAsync(Tenant, Run))!;
        Assert.Contains(stored.Status, new[] { RunStatus.Succeeded, RunStatus.Cancelled });
        Assert.Equal(stored, await left.GetReceiptAsync(Tenant, Run));
    }

    private static async Task<string[]> Race(Func<ValueTask> first, Func<ValueTask> second)
    {
        using var barrier = new Barrier(2);
        Task<string> Start(Func<ValueTask> action) => Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
            try { await action(); return "ok"; }
            catch (ExecutionRuntimeRefusedException refused) { return refused.Code; }
        });
        var results = await Task.WhenAll(Start(first), Start(second));
        return results.Order(StringComparer.Ordinal).ToArray();
    }
}

internal sealed class ReceiptDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "api527-receipts-" + Guid.NewGuid().ToString("N"));
    internal ReceiptDatabase()
    {
        Directory.CreateDirectory(_directory);
        using var provider = Open();
        using var db = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContext();
        db.Database.Migrate();
    }

    internal ServiceProvider Open()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContextWithStoreDek(new byte[32], Path.Combine(_directory, "local-node.db"), pooling: false);
        services.AddLocalNodePatternAModules();
        services.AddNodeExecutionReceiptStore();
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        // Best-effort temp cleanup when the database file is still locked.
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
