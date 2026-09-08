using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative <c>messages</c> table — the
/// durable read model for the comms append-log doctype (the first messaging doctype on the live node-host
/// path). Maps the flat <see cref="NodeMessage"/> row the CRDT comms list converges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT <c>LocalNodeDbContext</c>
/// and <see cref="NodeMessage"/> is NOT a shared <c>IHarborlineEntityModule</c>: comms is node-local and has no
/// Bridge EF persistence, so the entity stays out of the council C2 both-provider parity check (which
/// inspects only <c>LocalNodeDbContext</c>'s injected module set). It opens the SAME SQLCipher-encrypted
/// database file as the financial store, keyed through the same connection interceptor — there is no
/// plaintext path (SC-1), and it IS the recoverable relational store (SC4-C2: the comms CRDT's only durable
/// sink, never the seed-keyed per-team KV store). Mirrors <c>NodeLocalPropertyDbContext</c>.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against one
/// SQLite file; they must not share the default <c>__EFMigrationsHistory</c> table or they would clobber
/// each other's applied-migration records. This context records its history in
/// <c>__CommsMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalCommsDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its applied-migration records out of
    /// the financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__CommsMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalCommsDbContext"/>.</summary>
    public NodeLocalCommsDbContext(DbContextOptions<NodeLocalCommsDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local comms messages (append-only).</summary>
    public DbSet<NodeMessage> Messages => Set<NodeMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NodeMessage>(e =>
        {
            e.ToTable("messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id").HasMaxLength(128);
            e.Property(m => m.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            // C1 conversation scope. Default the column to the team channel so the additive migration back-fills
            // pre-C1 rows to "team" and a row inserted without an explicit conversation still lands on the team log.
            e.Property(m => m.ConversationId)
                .HasColumnName("conversation_id")
                .HasMaxLength(256)
                .HasDefaultValue(CommsConversation.TeamConversationId);
            e.Property(m => m.AuthorPartyId).HasColumnName("author_party_id").HasMaxLength(256);
            e.Property(m => m.AuthorIssuerId).HasColumnName("author_issuer_id").HasMaxLength(256);
            e.Property(m => m.Body).HasColumnName("body");
            e.Property(m => m.NonceGuid).HasColumnName("nonce").HasMaxLength(64);
            e.Property(m => m.SignatureB64Url).HasColumnName("signature").HasMaxLength(256);
            // Store the authoring instant as Unix epoch-MILLISECONDS (INTEGER), NOT a DateTimeOffset column:
            // SQLite cannot ORDER BY a DateTimeOffset, and the comms read path orders by authored time
            // server-side. Epoch-ms is integer-orderable, lossless to ms (matching the signing precision —
            // CommsMessageFactory truncates to ms before signing), and round-trips via the converter.
            e.Property(m => m.AuthoredAtUtc)
                .HasColumnName("authored_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            // The comms read path is per-tenant, per-conversation, authored-order — index the scoping +
            // ordering columns. The (tenant, conversation, authored) composite serves the C1 conversation-scoped
            // GET; the (tenant) + (tenant, authored) indexes are retained for the team-wide read + back-compat.
            e.HasIndex(m => m.TenantId);
            e.HasIndex(m => new { m.TenantId, m.AuthoredAtUtc });
            e.HasIndex(m => new { m.TenantId, m.ConversationId, m.AuthoredAtUtc });
        });
    }
}
