using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

/// <summary>
/// T-986: the host's durable kernel audit over one encrypted <c>local-node.db</c> with the search and roster
/// schemas applied: the trail store, the outbox that delivers to it, and the one reader. <see cref="Reopen"/>
/// is a restart: new objects over the same file.
/// </summary>
internal sealed class DurableAuditHarness : IAsyncDisposable
{
    private readonly SearchTestStore _origin;

    private DurableAuditHarness(SearchTestStore store, SearchTestStore origin)
    {
        Store = store;
        _origin = origin;
        var signer = new Ed25519Signer(KeyPair.Generate());
        TrailStore = new NodeAuditTrailStore(store.Factory);
        Trail = new AuthorityCapturingAuditTrail(TrailStore);
        Outbox = new NodeAuditOutbox(store.Factory, Trail, Trail, signer, TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
        Reader = new SnapshotAuditEventReader(TrailStore.SnapshotAsync, Trail, signer);
    }

    public SearchTestStore Store { get; }

    public IDbContextFactory<NodeLocalRosterDbContext> RosterFactory => Store.RosterFactory;

    public NodeAuditTrailStore TrailStore { get; }

    public AuthorityCapturingAuditTrail Trail { get; }

    public NodeAuditOutbox Outbox { get; }

    public IAuditEventReader Reader { get; }

    public static async Task<DurableAuditHarness> CreateAsync()
    {
        var store = await SearchTestStore.CreateAsync();
        await using (var roster = store.CreateRosterContext())
            await roster.Database.MigrateAsync();
        return new DurableAuditHarness(store, store);
    }

    /// <summary>A restart: nothing of this harness in memory survives into the returned one.</summary>
    public DurableAuditHarness Reopen() => new(SearchTestStore.Reopen(_origin), _origin);

    /// <summary>Delivers what the outbox owes, then reads the trail.</summary>
    public async Task<IReadOnlyList<AuditRecord>> DeliveredAsync(TenantId tenant, AuditEventType eventType)
    {
        await Outbox.DrainAsync();
        return (await Reader.ListAsync(tenant, new AuditEventReaderQuery(EventType: eventType))).Records;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var db = Store.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    public async ValueTask DisposeAsync()
    {
        Outbox.Dispose();
        await Store.DisposeAsync();
    }
}
