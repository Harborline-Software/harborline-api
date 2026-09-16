using System.Data;
using System.Data.Common;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackProjectionResourceLifetimeTests
{
    private static readonly TenantId Tenant = new("projection-resource-lifetime");

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
