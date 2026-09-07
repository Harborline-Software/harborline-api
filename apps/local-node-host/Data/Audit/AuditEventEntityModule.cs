using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> mapping the node audit system-of-record tables into
/// <see cref="LocalNodeDbContext"/> — <b>Pattern A</b> (ADR 0126 §D1 / OQ4, forced by the OQ2 =
/// atomic ruling).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pattern A (shared context), not Pattern B (separate context).</b> Because the OQ2 ruling makes
/// the audit-row append commit in the SAME <c>local-node.db</c> transaction as the journal-entry
/// write, the audit table MUST share <see cref="LocalNodeDbContext"/>'s connection + transaction. A
/// separate <c>NodeLocalAuditDbContext</c> (Pattern B) owns its own connection and could not join the
/// JE unit-of-work. So this is an <see cref="IHarborlineEntityModule"/> like
/// <c>FinancialLedgerEntityModule</c> / <c>BankingEntityModule</c>, NOT a node-exclusive DbContext
/// like <c>NodeLocalMaintenanceDbContext</c>.
/// </para>
/// <para>
/// <b>Host-local module.</b> Unlike the shared <c>blocks-*</c> entity modules (which the Bridge also
/// composes), this module lives in the local-node-host app and is registered ONLY on the node. There
/// is no Bridge audit-projection schema (the Bridge audit path stays <c>EventLogBackedAuditTrail</c>
/// over <c>IEventLog</c>, unchanged — ADR 0126 §Decision), so the C2 both-provider parity obligation
/// does NOT attach: this is a node-resident system-of-record, not a Bridge-replicated entity.
/// </para>
/// <para>
/// <b>Column types ride the SQLite sweep.</b> The tables declare Postgres-style hints
/// (<c>jsonb</c> for the canonical-JSON payload, <c>bytea</c> for signature / public-key bytes) so
/// <see cref="LocalNodeDbContext"/>'s post-configuration sweep rewrites them to <c>TEXT</c> / <c>BLOB</c>
/// for SQLite — the same convention every shared module uses, no per-provider fork.
/// </para>
/// </remarks>
public sealed class AuditEventEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.audit";

    /// <summary>
    /// <c>DateTimeOffset</c> → ISO-8601-UTC string converter. SQLite cannot <c>ORDER BY</c> a
    /// <c>DateTimeOffset</c> column (NotSupportedException), and the audit surface MUST order by
    /// <c>OccurredAt</c> server-side (the reverse-chronological cursor + the hash-chain order). An
    /// ISO-8601 UTC string (<c>"O"</c> form, normalised to UTC) sorts lexicographically identically to
    /// chronological order, so <c>ORDER BY</c> + range filters work in SQL. Round-trips losslessly.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter =
        new(v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureAuditEvents(modelBuilder);
        ConfigureSignatureEpochs(modelBuilder);
    }

    private static void ConfigureAuditEvents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NodeAuditEventRow>(e =>
        {
            e.ToTable("node_audit_events");
            e.HasKey(r => r.AuditId);

            e.Property(r => r.AuditId).HasMaxLength(128).IsRequired();
            e.Property(r => r.TenantId).HasMaxLength(256).IsRequired();
            e.Property(r => r.EventType).HasMaxLength(256).IsRequired();
            // ISO-8601-UTC string so SQLite can ORDER BY + range-filter the cursor key.
            e.Property(r => r.OccurredAt)
                .HasConversion(Iso8601UtcConverter)
                .HasMaxLength(33)
                .IsRequired();
            e.Property(r => r.Actor).HasMaxLength(256);
            e.Property(r => r.CorrelationId).HasMaxLength(256);

            // Canonical-JSON payload body. jsonb on Postgres → TEXT on SQLite (LocalNodeDbContext
            // sweep). The reader treats it as opaque text and re-parses for payload_summary.
            e.Property(r => r.Payload)
                .HasColumnName("payload_json")
                .HasColumnType("jsonb")
                .IsRequired();

            // Hash-chain fields (offline integrity verification — HashChain, ADR 0126 §D4).
            e.Property(r => r.PrevHash).HasMaxLength(64);
            e.Property(r => r.Hash).HasMaxLength(64).IsRequired();

            // Seed-derived Ed25519 signature bytes (nullable — the foundation AuditRecord allows a
            // null signature). bytea on Postgres → BLOB on SQLite.
            e.Property(r => r.Signature)
                .HasColumnName("signature")
                .HasColumnType("bytea");

            // Tenant isolation index — the node's defence-in-depth boundary (ADR 0092).
            e.HasIndex(r => r.TenantId).HasDatabaseName("ix_node_audit_events_tenant_id");
            // The reverse-chronological cursor key (OccurredAt DESC, AuditId DESC) — list reads.
            e.HasIndex(r => new { r.TenantId, r.OccurredAt })
                .HasDatabaseName("ix_node_audit_events_tenant_occurred");
            // Event-type filter.
            e.HasIndex(r => new { r.TenantId, r.EventType })
                .HasDatabaseName("ix_node_audit_events_tenant_event_type");
            // Correlation-id drill-down filter.
            e.HasIndex(r => new { r.TenantId, r.CorrelationId })
                .HasDatabaseName("ix_node_audit_events_tenant_correlation");
        });
    }

    private static void ConfigureSignatureEpochs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NodeAuditSignatureEpochRow>(e =>
        {
            e.ToTable("node_audit_signature_epochs");
            e.HasKey(r => r.EpochId);

            // EpochId is a caller-assigned monotonic id (sealed at reseed time), not store-generated.
            e.Property(r => r.EpochId).ValueGeneratedNever().IsRequired();
            // ISO-8601-UTC string so SQLite can ORDER BY / compare the reseed boundary.
            e.Property(r => r.BoundaryAt)
                .HasConversion(Iso8601UtcConverter)
                .HasMaxLength(33)
                .IsRequired();
            e.Property(r => r.IssuerId).HasMaxLength(256).IsRequired();

            e.Property(r => r.SealedPublicKey)
                .HasColumnName("sealed_public_key")
                .HasColumnType("bytea")
                .IsRequired();

            // Resolve the epoch covering a pre-reseed record by issuer + boundary.
            e.HasIndex(r => r.IssuerId).HasDatabaseName("ix_node_audit_signature_epochs_issuer");
            e.HasIndex(r => r.BoundaryAt).HasDatabaseName("ix_node_audit_signature_epochs_boundary");
        });
    }
}
