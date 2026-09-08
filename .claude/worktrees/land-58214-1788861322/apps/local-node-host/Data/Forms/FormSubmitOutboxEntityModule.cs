using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// Maps the tenant-scoped form-submit outbox into the SQLCipher-backed
/// <see cref="LocalNodeDbContext"/> and therefore into its normal migration path.
/// </summary>
public sealed class FormSubmitOutboxEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.form-submit-outbox";

    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter =
        new(
            value => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            value => DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<FormSubmitOutboxRow>(entity =>
        {
            entity.ToTable("form_submit_outbox");

            entity.HasKey(row => row.Sequence);
            entity.Property(row => row.Sequence).ValueGeneratedOnAdd();

            entity.Property(row => row.EntryId).HasMaxLength(512).IsRequired();
            entity.Property(row => row.FormId).HasMaxLength(512).IsRequired();
            entity.Property(row => row.InstanceId).HasMaxLength(512).IsRequired();
            entity.Property(row => row.TenantId).HasMaxLength(256).IsRequired();
            entity.Property(row => row.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(row => row.SubmittedAt)
                .HasConversion(Iso8601UtcConverter)
                .HasMaxLength(33)
                .IsRequired();
            entity.Property(row => row.SubmittedValuesJson)
                .HasColumnName("submitted_values_json")
                .HasColumnType("TEXT")
                .IsRequired();
            entity.Property(row => row.CaseRef).HasMaxLength(512);
            entity.Property(row => row.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(row => row.Attempts).IsRequired();
            entity.Property(row => row.LastError).HasMaxLength(4096);

            // The interface's idempotency contract is by canonical instance id. Tenant remains explicit
            // on every row and on the recovery index so tenant/state scans cannot drift into an unscoped
            // table shape.
            entity.HasIndex(row => row.EntryId)
                .IsUnique()
                .HasDatabaseName("ux_form_submit_outbox_entry_id");
            entity.HasIndex(row => new { row.TenantId, row.State, row.Sequence })
                .HasDatabaseName("ix_form_submit_outbox_tenant_state_sequence");
        });
    }
}
