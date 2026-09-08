using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harborline.Api.LocalNodeHost.Data.Admission;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative <c>admission_tokens</c> table — the
/// durable backing for minted single-use admission INVITES (enrollment Phase B, admitter side). Maps the flat
/// <see cref="NodeAdmissionTokenRecord"/> row. Mirrors <c>NodeLocalRosterDbContext</c> / <c>NodeLocalCommsDbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT <c>LocalNodeDbContext</c> and
/// <see cref="NodeAdmissionTokenRecord"/> is NOT a shared <c>IHarborlineEntityModule</c>: invites are admitter-local
/// and never sync, so they stay out of the council C2 both-provider parity check. It opens the SAME
/// SQLCipher-encrypted database file as the financial store, keyed through the same connection interceptor — there
/// is no plaintext path (SC-1).
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against one SQLite
/// file; they must not share the default <c>__EFMigrationsHistory</c> table. This context records its history in
/// <c>__AdmissionMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalAdmissionDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its applied-migration records out of the
    /// financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__AdmissionMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalAdmissionDbContext"/>.</summary>
    public NodeLocalAdmissionDbContext(DbContextOptions<NodeLocalAdmissionDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local minted admission-invite tokens (single-use + TTL ledger).</summary>
    public DbSet<NodeAdmissionTokenRecord> AdmissionTokens => Set<NodeAdmissionTokenRecord>();

    /// <summary>#3167 R6/D — the node-local web-admitted-member device-pairing bindings (keyed pin lookup).</summary>
    public DbSet<NodePairingInviteBindingRecord> PairingInviteBindings => Set<NodePairingInviteBindingRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NodePairingInviteBindingRecord>(e =>
        {
            e.ToTable("pairing_invite_bindings");
            e.HasKey(r => r.TokenId);
            e.Property(r => r.TokenId).HasColumnName("token_id").HasMaxLength(128);
            e.Property(r => r.BoundPartyId).HasColumnName("bound_party_id").HasMaxLength(256);
            e.Property(r => r.SessionCorrelationId)
                .HasColumnName("session_correlation_id").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.MembershipJson).HasColumnName("membership_json");
        });

        modelBuilder.Entity<NodeAdmissionTokenRecord>(e =>
        {
            e.ToTable("admission_tokens");
            e.HasKey(r => r.TokenId);
            e.Property(r => r.TokenId).HasColumnName("token_id").HasMaxLength(128);
            e.Property(r => r.AnchorTeamId).HasColumnName("anchor_team_id").HasMaxLength(64);
            e.Property(r => r.AnchorGenesisPartyId).HasColumnName("anchor_genesis_party").HasMaxLength(256);
            e.Property(r => r.AnchorGenesisPublicKey).HasColumnName("anchor_genesis_key").HasMaxLength(256);
            // Store the issuance instant as Unix epoch-MILLISECONDS (INTEGER): integer-orderable, lossless to ms
            // (matching the minting precision), and round-trips — the same convention the roster store uses.
            e.Property(r => r.IssuedAtUtc)
                .HasColumnName("issued_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            e.Property(r => r.TtlMilliseconds).HasColumnName("ttl_ms");
            e.Property(r => r.Redeemed).HasColumnName("redeemed");
        });
    }
}
