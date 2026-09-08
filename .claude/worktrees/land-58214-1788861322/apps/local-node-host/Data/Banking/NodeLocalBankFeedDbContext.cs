using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative
/// <c>bank_feed_connections</c> table. Tracks whether a bank account has an active
/// mock feed connection so <c>BankAccountDetailWire.FeedConnected</c> reflects real
/// state across process restarts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT
/// <see cref="LocalNodeDbContext"/> and its single entity is NOT a shared
/// <c>IHarborlineEntityModule</c>: feed-connection state is node-local-authoritative
/// and has no Bridge EF persistence, so it must stay out of the council C2
/// both-provider parity check (which inspects only <see cref="LocalNodeDbContext"/>'s
/// injected module set). It opens the SAME SQLCipher-encrypted database file as the
/// financial store, keyed through the same <see cref="SqlCipherConnectionInterceptor"/>
/// on the connection — there is no plaintext path (SC-1). Mirrors
/// <c>NodeLocalMaintenanceDbContext</c> exactly.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Two EF contexts that each call
/// <c>MigrateAsync</c> against one SQLite file must not share the default
/// <c>__EFMigrationsHistory</c> table or they would clobber each other's
/// applied-migration records. This context records its history in
/// <c>__BankFeedMigrationsHistory</c> so neither the financial store's table
/// nor any other node-local context's table is touched.
/// </para>
/// </remarks>
public sealed class NodeLocalBankFeedDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its
    /// applied-migration records out of the financial store's default
    /// <c>__EFMigrationsHistory</c> table and the other node-local contexts'
    /// tables (the two contexts share one SQLCipher file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__BankFeedMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalBankFeedDbContext"/>.</summary>
    public NodeLocalBankFeedDbContext(DbContextOptions<NodeLocalBankFeedDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local bank-feed connection records (one row per connected account).</summary>
    public DbSet<BankFeedConnectionRecord> BankFeedConnections => Set<BankFeedConnectionRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BankFeedConnectionRecord>(e =>
        {
            e.ToTable("bank_feed_connections");
            e.HasKey(r => r.AccountId);
            e.Property(r => r.AccountId).HasColumnName("account_id").HasMaxLength(128).IsRequired();
            e.Property(r => r.ConnectedAt).HasColumnName("connected_at").IsRequired();
        });
    }
}
