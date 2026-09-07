using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> mapping the home-failover fencing-epoch table
/// (<see cref="HomeEpochRecord"/>) into <see cref="LocalNodeDbContext"/> — <b>Pattern A</b> (shared
/// context), the SAME pattern <see cref="Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule"/> uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pattern A is NECESSARY here, not a convenience</b> (but not, by itself, sufficient — see below). The
/// whole point of the fencing epoch (security verdict G-4) is that a write asserting a stale epoch is
/// rejected INSIDE the financial write's own SQLite transaction — no TOCTOU window. The fence read must
/// therefore run on <see cref="LocalNodeDbContext"/>'s own connection, so it can be enrolled in the same
/// transaction the JE write commits in. A separate context (Pattern B) could not join the JE unit-of-work,
/// which would re-introduce exactly the Gap-2a TOCTOU the verdict rejects ("read epoch from a separate
/// connection then commit the JE" = stale window).
/// </para>
/// <para>
/// <b>Sharing the context is necessary but NOT sufficient — the caller must hold the write lock across the
/// read.</b> EF's implicit <c>SaveChanges</c> transaction wraps only the WRITE; a fence read with no
/// explicit transaction still runs as its own autocommit statement and releases its SHARED lock before the
/// write lock is taken (security verdict earlier repository ticket #1365 Finding 1). So the fenced sites
/// (<c>NodeEfJournalStore.SaveAtomicAsync</c>, <c>NodeEfInvoiceNumberingService.NextNumberAsync</c>) run
/// the fence-read-through-save inside an explicit <c>BEGIN IMMEDIATE</c> transaction
/// (<see cref="HomeEpochFenceTransaction"/>) on this shared context, which takes the write lock BEFORE the
/// read. Pattern A makes that possible; the IMMEDIATE transaction makes it atomic.
/// </para>
/// <para>
/// <b>Host-local module.</b> Like the audit module, this lives in the local-node-host app and is
/// registered ONLY on the node — there is no Bridge home-epoch projection (the home concept is a
/// local-first residency primitive), so the both-provider parity obligation does not attach.
/// </para>
/// <para>
/// <b>Column types ride the SQLite sweep.</b> The signed-instant column declares no Postgres-only hint;
/// it is stored as an ISO-8601-UTC string (like the audit <c>OccurredAt</c>) so SQLite can ORDER BY /
/// compare it. The signatures + keys are Base64Url strings (not raw <c>bytea</c>), matching how the
/// roster stores admission signatures, so no BLOB sweep is needed.
/// </para>
/// </remarks>
public sealed class HomeEpochEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.home-epoch";

    /// <summary>
    /// <c>DateTimeOffset</c> → ISO-8601-UTC string converter (the same one the audit module uses). SQLite
    /// cannot ORDER BY / compare a <c>DateTimeOffset</c> column; an ISO-8601 UTC string sorts
    /// lexicographically identically to chronological order and round-trips losslessly.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter =
        new(v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HomeEpochRecord>(e =>
        {
            e.ToTable("home_epochs");

            // Composite key (TenantId, EpochNumber): the current home for a tenant is the row with the
            // highest EpochNumber; the PK makes a duplicate (tenant, epoch) append impossible at the
            // storage layer (the deterministic backstop behind the store's strict-monotonic guard).
            e.HasKey(r => new { r.TenantId, r.EpochNumber });

            e.Property(r => r.TenantId).HasMaxLength(256).IsRequired();
            // Caller-assigned monotonic number (not store-generated).
            e.Property(r => r.EpochNumber).ValueGeneratedNever().IsRequired();
            e.Property(r => r.PreviousEpochNumber).IsRequired();
            e.Property(r => r.HomeDeviceId).HasMaxLength(256).IsRequired();
            e.Property(r => r.PromotionKind).HasConversion<string>().HasMaxLength(32).IsRequired();

            // ISO-8601-UTC string so SQLite can ORDER BY / compare the issued instant.
            e.Property(r => r.IssuedAt)
                .HasConversion(Iso8601UtcConverter)
                .HasMaxLength(33)
                .IsRequired();
            e.Property(r => r.Nonce).IsRequired();

            // Roster-bound issuer + Ed25519 signature (Base64Url strings, as the roster stores them).
            e.Property(r => r.IssuerId).HasMaxLength(256).IsRequired();
            e.Property(r => r.Signature).HasMaxLength(256).IsRequired();

            // Co-approver pair — REQUIRED for recovery-failover (G-5 floor), null for planned-handoff.
            e.Property(r => r.CoApproverIssuerId).HasMaxLength(256);
            e.Property(r => r.CoApproverSignature).HasMaxLength(256);

            // The current-epoch tip lookup: highest EpochNumber per tenant.
            e.HasIndex(r => new { r.TenantId, r.EpochNumber })
                .HasDatabaseName("ix_home_epochs_tenant_epoch");
        });
    }
}
