using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> mapping the Wave-5 spatial-frame tables
/// (<see cref="SpatialFrameDescriptorRow"/> + <see cref="SpatialFrameQuarantineRow"/>) into
/// <see cref="LocalNodeDbContext"/> — Pattern A, following <c>HomeEpochEntityModule</c>.
/// </summary>
/// <remarks>
/// <para><b>Pattern A is necessary:</b> the frame-epoch tip read must run on the same connection
/// as the staged append, inside the <c>BEGIN IMMEDIATE</c> fence transaction, together with the
/// in-transaction home-claim re-read against <c>home_epochs</c> (0168 D2-A4(b)).</para>
/// <para><b>Host-local module caveat ([A13]):</b> legitimate only while there is no Bridge
/// projection of these tables (none exists — the registry has no replication surface); this moves
/// to the ADR 0015 <c>-data</c> sidecar convention the moment a second provider appears.</para>
/// <para><b>The composite PK is the D2-A3 layer-3 storage backstop</b> — a duplicate
/// <c>(tenant, anchor, frameCode, frameEpoch)</c> append is impossible at the storage layer.</para>
/// </remarks>
public sealed class SpatialFrameEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.spatial-frames";

    /// <summary>ISO-8601-UTC string converter (the audit/home-epoch module convention) so SQLite
    /// can ORDER BY / compare the instants.</summary>
    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter =
        new(v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SpatialFrameDescriptorRow>(e =>
        {
            e.ToTable("spatial_frame_descriptors");

            e.HasKey(r => new { r.TenantId, r.AnchorId, r.FrameCode, r.FrameEpoch });

            // HasMaxLength on every string key column ([A14] / HomeEpochEntityModule precedent).
            e.Property(r => r.TenantId).HasMaxLength(256).IsRequired();
            e.Property(r => r.AnchorId).HasMaxLength(256).IsRequired();
            e.Property(r => r.FrameCode).HasMaxLength(128).IsRequired();
            e.Property(r => r.FrameEpoch).ValueGeneratedNever().IsRequired();

            e.Property(r => r.AxisConvention).HasMaxLength(128).IsRequired();
            // CP-4: the column holds the tenant-DEK EncryptedField envelope JSON, not the prose —
            // widened from the original 2048 for base64url + envelope overhead. (SQLite TEXT does
            // not enforce either bound; the metadata is kept honest for provider portability.)
            e.Property(r => r.OriginDescription).HasMaxLength(6144).IsRequired();
            e.Property(r => r.LengthUnit).HasMaxLength(16).IsRequired();
            e.Property(r => r.GeoreferenceJson);

            // A column per signed field — the row alone re-verifies the mint signature.
            e.Property(r => r.Issuer).HasMaxLength(256).IsRequired();
            e.Property(r => r.Nonce).IsRequired();
            e.Property(r => r.IssuedAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();
            e.Property(r => r.Signature).HasMaxLength(256).IsRequired();
            e.Property(r => r.HomeDeviceId).HasMaxLength(256).IsRequired();
            e.Property(r => r.GrantingHomeEpoch).IsRequired();
            e.Property(r => r.PreviousEpoch).IsRequired();
            e.Property(r => r.ContentHash).HasMaxLength(64).IsRequired();

            // The tip lookup: highest FrameEpoch per (tenant, anchor, frameCode).
            e.HasIndex(r => new { r.TenantId, r.AnchorId, r.FrameCode, r.FrameEpoch })
                .HasDatabaseName("ix_spatial_frame_descriptors_series_epoch");
        });

        modelBuilder.Entity<SpatialFrameQuarantineRow>(e =>
        {
            e.ToTable("spatial_frame_quarantine");

            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();

            e.Property(r => r.TenantId).HasMaxLength(256).IsRequired();
            e.Property(r => r.AnchorId).HasMaxLength(256).IsRequired();
            e.Property(r => r.FrameCode).HasMaxLength(128).IsRequired();
            e.Property(r => r.AttemptedEpoch).IsRequired();
            e.Property(r => r.TipEpochAtDetection).IsRequired();
            e.Property(r => r.Reason).HasMaxLength(32).IsRequired();
            e.Property(r => r.AxisConvention).HasMaxLength(128).IsRequired();
            // CP-4 envelope JSON, same bound as the descriptor column.
            e.Property(r => r.OriginDescription).HasMaxLength(6144).IsRequired();
            e.Property(r => r.LengthUnit).HasMaxLength(16).IsRequired();
            e.Property(r => r.GeoreferenceJson);
            e.Property(r => r.DetectedAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();

            e.HasIndex(r => new { r.TenantId, r.AnchorId, r.FrameCode })
                .HasDatabaseName("ix_spatial_frame_quarantine_series");
        });
    }
}
