using System.Data;
using System.Data.Common;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

[Collection(PackProjectionBarrierCollection.Name)]
public sealed class PackProjectionResourceLifetimeTests
{
    private static readonly TenantId Tenant = new("projection-resource-lifetime");

    [Fact]
    public async Task Paused_schema_enumerator_allows_queued_activation_and_consumer_read_and_retains_snapshot()
    {
        var store = new InMemorySchemaRegistry(TimeProvider.System);
        var first = await store.RegisterAsync("""{"type":"string"}""");
        var second = await store.RegisterAsync("""{"type":"number"}""");
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker;
        using (ExecutionContext.SuppressFlow())
            blocker = Task.Factory.StartNew(() =>
            {
                using var lease = PackProjectionActivationBarrier.Read(deadline.Token);
                holding.SetResult();
                release.Wait(deadline.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? activation = null;
        Task<Schema?>? consumer = null;
        await using var iterator = store.ListAsync().GetAsyncEnumerator();
        try
        {
            await holding.Task.WaitAsync(deadline.Token);
            Assert.True(await iterator.MoveNextAsync());
            var selected = iterator.Current.Id;
            var seen = new List<SchemaId> { selected };
            var writerStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ExecutionContext.SuppressFlow())
                activation = Task.Factory.StartNew(async () =>
                {
                    writerStarted.SetResult(Thread.CurrentThread);
                    using var transaction = new PackProjectionTransaction(deadline.Token);
                    transaction.Enlist(store);
                    await store.RegisterAsync("""{"type":"boolean"}""", ct: deadline.Token);
                    transaction.Commit();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            var writerThread = await writerStarted.Task.WaitAsync(deadline.Token);
            Assert.True(SpinWait.SpinUntil(() =>
                (writerThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));

            var consumerStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ExecutionContext.SuppressFlow())
                consumer = Task.Factory.StartNew(async () =>
                {
                    consumerStarted.SetResult(Thread.CurrentThread);
                    return await store.GetAsync(selected, deadline.Token);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            var consumerThread = await consumerStarted.Task.WaitAsync(deadline.Token);
            Assert.True(SpinWait.SpinUntil(() => consumer.IsCompleted ||
                (consumerThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            Assert.False(consumer.IsCompleted);
            release.Set();

            await activation.WaitAsync(deadline.Token);
            Assert.Equal(selected, (await consumer.WaitAsync(deadline.Token))!.Id);
            while (await iterator.MoveNextAsync()) seen.Add(iterator.Current.Id);
            Assert.Equal(new[] { first.Id, second.Id }.OrderBy(id => id.Value), seen.OrderBy(id => id.Value));
            var current = new List<Schema>();
            await foreach (var schema in store.ListAsync()) current.Add(schema);
            Assert.Equal(3, current.Count);
        }
        finally
        {
            release.Set();
            deadline.Cancel();
            var actors = new List<Task> { blocker };
            if (activation is not null) actors.Add(activation);
            if (consumer is not null) actors.Add(consumer);
            await Task.WhenAll(actors).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData("open")]
    [InlineData("begin")]
    [InlineData("enlist")]
    public void Failed_sqlite_unit_initialization_disposes_its_owned_context(string failureAt)
    {
        var options = new DbContextOptionsBuilder<ObservedContext>().UseSqlite("Data Source=:memory:");
        if (failureAt == "open") options.AddInterceptors(new RefuseOpen());
        if (failureAt == "enlist") options.AddInterceptors(new RefuseEnlist());
        using var owner = new ObservedContext(options.Options);
        var connection = (SqliteConnection)owner.Database.GetDbConnection();
        using var existing = failureAt == "begin" ? OpenExistingTransaction(owner, connection) : null;

        var failure = Assert.ThrowsAny<Exception>(() =>
        {
            using var unit = new PackProjectionSqliteUnit(owner);
        });
        if (failureAt == "begin")
            Assert.Contains("nested transactions", Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        else
            Assert.Equal(failureAt, Assert.IsType<IOException>(failure).Message);
        Assert.True(owner.Disposed);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_form_enumerator_does_not_block_activation_and_retains_its_snapshot(bool publishedOnly)
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        await AddPublishedFormAsync(store, "a");
        await AddPublishedFormAsync(store, "b");
        await using var iterator = (publishedOnly ? store.ListPublishedAsync() : store.ListByTenantAsync(Tenant))
            .GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("a", iterator.Current.Id.Value);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task activation;
        using (ExecutionContext.SuppressFlow())
            activation = Task.Run(async () =>
            {
                using var transaction = new PackProjectionTransaction(deadline.Token);
                transaction.Enlist(store);
                await store.WithdrawAsync(new(Tenant, "b", "1.0.0"), deadline.Token);
                await AddPublishedFormAsync(store, "c");
                transaction.Commit();
            });
        await activation;

        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("b", iterator.Current.Id.Value);
        Assert.Equal(FormDefinitionStatus.Published, iterator.Current.Status);
        Assert.False(await iterator.MoveNextAsync());
        Assert.NotNull(await store.GetCurrentPublishedAsync(new(Tenant, "c")));
        Assert.Null(await store.GetCurrentPublishedAsync(new(Tenant, "b")));
    }

    [Fact]
    public async Task Paused_standing_enumerator_does_not_block_activation_and_retains_its_snapshot()
    {
        var store = new InMemoryStandingRuleDefinitionStore();
        await store.RegisterAsync(Standing("a"));
        await store.RegisterAsync(Standing("b"));
        await using var iterator = store.ListAsync().GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("a", iterator.Current.RuleId);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task activation;
        using (ExecutionContext.SuppressFlow())
            activation = Task.Run(async () =>
            {
                using var transaction = new PackProjectionTransaction(deadline.Token);
                transaction.Enlist(store);
                Assert.True(await store.RemoveAsync("b", "1.0.0", deadline.Token));
                await store.RegisterAsync(Standing("c"), deadline.Token);
                transaction.Commit();
            });
        await activation;

        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("b", iterator.Current.RuleId);
        Assert.False(await iterator.MoveNextAsync());
        Assert.NotNull(await store.GetAsync("c", "1.0.0"));
        Assert.Null(await store.GetAsync("b", "1.0.0"));
    }

    private static SqliteTransaction OpenExistingTransaction(DbContext context, SqliteConnection connection)
    {
        context.Database.OpenConnection();
        return connection.BeginTransaction();
    }

    private static async Task AddPublishedFormAsync(IFormDefinitionStore store, string id)
    {
        await store.RegisterAsync(new FormDefinition(new FormDefinitionId(id), new SemanticVersion(1, 0, 0),
            FormDefinitionStatus.Draft, Tenant, IdentityRef.System, new SchemaId("schema"),
            new HarborlineOverlay(new Dictionary<string, FieldOverlay>(), [], []), null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        await store.PublishAsync(new(Tenant, id, "1.0.0"));
    }

    private static StandingRuleDefinition Standing(string id) => new(id, "1.0.0", new StandingReference("handler"),
        "matter", ["handler_id"], new RuleDefinition(
            new DefinitionEnvelope<string, string, TenantId, string?>(id, "1.0.0", Tenant, CascadeLayer.Pack, null, []),
            RuleTier.JsonLogic, RuleScope.Schema, string.Empty,
            """{"==":[{"var":"handler_id"},"person-7"]}""", RuleActionKind.Validate));

    private sealed class ObservedContext(DbContextOptions<ObservedContext> options) : DbContext(options)
    {
        internal bool Disposed { get; private set; }
        public override void Dispose()
        {
            Disposed = true;
            base.Dispose();
        }
    }

    private sealed class RefuseOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result) => throw new IOException("open");
    }

    private sealed class RefuseEnlist : DbTransactionInterceptor
    {
        public override DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData,
            DbTransaction result) => throw new IOException("enlist");
    }
}
