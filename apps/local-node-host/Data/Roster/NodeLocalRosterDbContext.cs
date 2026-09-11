using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative <c>roster_records</c> table —
/// the durable read model for the roster-sync doctype (production-wiring gap #1). Maps the flat
/// <see cref="NodeRosterRecord"/> row the CRDT roster list converges. Mirrors <c>NodeLocalCommsDbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT <c>LocalNodeDbContext</c>
/// and <see cref="NodeRosterRecord"/> is NOT a shared <c>IHarborlineEntityModule</c>: the roster is node-local
/// and has no Bridge EF persistence, so it stays out of the council C2 both-provider parity check. It opens
/// the SAME SQLCipher-encrypted database file as the financial store, keyed through the same connection
/// interceptor — there is no plaintext path (SC-1), and it IS the recoverable relational store (SC4-C2: the
/// roster CRDT's only durable sink, never the seed-keyed per-team KV store).
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against one
/// SQLite file; they must not share the default <c>__EFMigrationsHistory</c> table. This context records its
/// history in <c>__RosterMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalRosterDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its applied-migration records out of
    /// the financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__RosterMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalRosterDbContext"/>.</summary>
    public NodeLocalRosterDbContext(DbContextOptions<NodeLocalRosterDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local roster records (append-only signed admissions / revocations).</summary>
    public DbSet<NodeRosterRecord> RosterRecords => Set<NodeRosterRecord>();

    /// <summary>
    /// The append-only administrator-authority log (ADR 0066 migration step 1) — simultaneously the canonical
    /// state the installer's gate is evaluated against and the permanent audit of every establishment,
    /// removal and expiry. It lives in THIS context, beside the signed roster it derives from, because the
    /// gate read and the establishing write must commit in one transaction and a second context would be a
    /// second connection.
    /// </summary>
    public DbSet<Identity.AdministratorAuthorityRecord> AdministratorAuthority =>
        Set<Identity.AdministratorAuthorityRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NodeRosterRecord>(e =>
        {
            e.ToTable("roster_records");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").HasMaxLength(256);
            e.Property(r => r.Kind).HasColumnName("kind");
            e.Property(r => r.TeamId).HasColumnName("team_id").HasMaxLength(64);
            e.Property(r => r.PartyId).HasColumnName("party_id").HasMaxLength(256);
            e.Property(r => r.PublicKeyB64Url).HasColumnName("public_key").HasMaxLength(256);
            // INFO-2 — the carried team-scoped transport pubkey (≥3-node mesh follow-on). Nullable + default empty:
            // additive, legacy rows + revocation records carry none. The EF migration adds this column to existing
            // local-node.db files (bug-2851 lesson: model field without a migration → "no such column" at runtime).
            e.Property(r => r.TransportPublicKeyB64Url)
                .HasColumnName("transport_public_key").HasMaxLength(256).HasDefaultValue(string.Empty);
            // C5 — the carried team-scoped DM-encryption pubkey (DM content confidentiality). Same additive,
            // nullable-default-empty posture as the transport key; the EF migration adds the column to existing
            // local-node.db files (bug-2851 lesson: model field without a migration → "no such column" at runtime).
            e.Property(r => r.DmPublicKeyB64Url)
                .HasColumnName("dm_public_key").HasMaxLength(256).HasDefaultValue(string.Empty);
            // 2c-iii-b — the carried team-scoped X-Wing pubkey (the suite-#3 write-side enabler). Same additive,
            // nullable-default-empty posture as the DM key; the EF migration adds the column to existing
            // local-node.db files (bug-2851 lesson: model field without a migration → "no such column" at runtime).
            // Larger max length: the 1216-byte X-Wing key base64url-encodes to ~1624 chars (vs 256 for a 32-byte
            // PrincipalId key) — 2048 leaves headroom without an unbounded TEXT column.
            e.Property(r => r.XWingPublicKeyB64Url)
                .HasColumnName("xwing_public_key").HasMaxLength(2048).HasDefaultValue(string.Empty);
            // #3167 R1.2 — the SIGNED admission provenance (pairing token id + mint-session evidence). Same additive,
            // nullable-default-empty posture as the key columns; the EF migration adds these columns to existing
            // local-node.db files (bug-2851 lesson: model field without a migration → "no such column" at runtime).
            // UNLIKE the key columns these are part of the SIGNED envelope, so they must survive persistence intact or
            // a pairing admission fails signature-verify on cold-start rebuild.
            e.Property(r => r.AdmittedViaTokenId)
                .HasColumnName("admitted_via_token_id").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.MintingSessionEvidence)
                .HasColumnName("minting_session_evidence").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.PermissionsJson).HasColumnName("permissions");
            e.Property(r => r.AdmittedByPublicKey).HasColumnName("admitted_by_key").HasMaxLength(256);
            e.Property(r => r.AdmittedByPartyId).HasColumnName("admitted_by_party").HasMaxLength(256);
            e.Property(r => r.NonceGuid).HasColumnName("nonce").HasMaxLength(64);
            e.Property(r => r.SignatureB64Url).HasColumnName("signature").HasMaxLength(256);
            e.Property(r => r.IsGenesis).HasColumnName("is_genesis");
            // Store the issuance instant as Unix epoch-MILLISECONDS (INTEGER): SQLite cannot ORDER BY a
            // DateTimeOffset, and the rebuild orders revocations by issuance time. Epoch-ms is
            // integer-orderable, lossless to ms (matching the signing precision), and round-trips.
            e.Property(r => r.IssuedAtUtc)
                .HasColumnName("issued_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            e.Property(r => r.ReceivedAtUtc).HasColumnName("received_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            e.Property(r => r.WireFormatVersion).HasColumnName("wire_format_version");
            e.Property(r => r.ReceivedByPartyId).HasColumnName("received_by_party")
                .HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.ReceivedByPublicKey).HasColumnName("received_by_key")
                .HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.ReceiveAttestationSignatureB64Url).HasColumnName("receive_attestation_signature")
                .HasMaxLength(256).HasDefaultValue(string.Empty);
            // The roster scope + ordering columns.
            e.HasIndex(r => r.TeamId);
            e.HasIndex(r => new { r.TeamId, r.IssuedAtUtc });
            e.HasIndex(r => new { r.TeamId, r.ReceivedAtUtc });
        });

        modelBuilder.Entity<Identity.AdministratorAuthorityRecord>(e =>
        {
            e.ToTable("administrator_authority");
            e.HasKey(r => r.Sequence);
            // Never database-generated: the sequence is assigned from the chain tip READ INSIDE the writing
            // transaction, so it is part of the same serializable decision as the state gate.
            e.Property(r => r.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            e.Property(r => r.TeamId).HasColumnName("team_id").HasMaxLength(64);
            e.Property(r => r.PartyId).HasColumnName("party_id").HasMaxLength(256);
            e.Property(r => r.Event).HasColumnName("event");
            e.Property(r => r.Provenance).HasColumnName("provenance");
            e.Property(r => r.MemberPublicKey)
                .HasColumnName("member_public_key").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.AdmissionSignature)
                .HasColumnName("admission_signature").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.AdmittedByPublicKey)
                .HasColumnName("admitted_by_key").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.AdmittedByPartyId)
                .HasColumnName("admitted_by_party").HasMaxLength(256).HasDefaultValue(string.Empty);
            e.Property(r => r.IsGenesisAdmission).HasColumnName("is_genesis_admission");
            e.Property(r => r.Reason).HasColumnName("reason").HasMaxLength(128).HasDefaultValue(string.Empty);
            e.Property(r => r.PreviousHash).HasColumnName("prev_hash").HasMaxLength(64);
            e.Property(r => r.Hash).HasColumnName("hash").HasMaxLength(64);
            // Epoch-milliseconds INTEGER, matching roster_records: SQLite cannot ORDER BY or compare a
            // DateTimeOffset, and the expiry predicate is a comparison.
            e.Property(r => r.OccurredAtUtc)
                .HasColumnName("occurred_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            e.Property(r => r.ExpiresAtUtc)
                .HasColumnName("expires_at")
                .HasConversion(new ValueConverter<DateTimeOffset?, long?>(
                    v => v == null ? null : v.Value.ToUnixTimeMilliseconds(),
                    v => v == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value)));
            e.HasIndex(r => new { r.TeamId, r.PartyId });
        });

        modelBuilder.Entity<CompromisedDeviceResponseRow>(e =>
        {
            e.ToTable("compromised_device_responses");
            e.HasKey(r => r.CorrelationId);
            e.Property(r => r.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.Property(r => r.PayloadJson).HasColumnName("payload_json");
            e.Property(r => r.SignerPublicKey).HasColumnName("signer_public_key").HasMaxLength(256);
            e.Property(r => r.SignedAt)
                .HasColumnName("signed_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            e.Property(r => r.SigningNonce).HasColumnName("signing_nonce").HasMaxLength(64);
            e.Property(r => r.Signature).HasColumnName("signature");
        });
    }
}
