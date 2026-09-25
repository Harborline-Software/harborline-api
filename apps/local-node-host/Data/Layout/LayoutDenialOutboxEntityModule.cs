using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.Layout;

/// <summary>The lifecycle of one Layout denial in the local outbox (T-731, owner ruling 2 of 2026-09-24).</summary>
public enum LayoutDenialOutboxState
{
    /// <summary>Recorded at the act; the signed gate-log append is not yet confirmed.</summary>
    Pending = 0,

    /// <summary>The signed record is in the gate log. Terminal.</summary>
    Appended = 1,

    /// <summary>An append attempt failed; the drain retries it.</summary>
    Failed = 2,
}

/// <summary>
/// One Layout related-binding denial held in the node's SQLCipher-backed <c>local-node.db</c> until its
/// signed record is in the authorization gate log. The fields are fixed at the act, so a retry appends
/// the record as it was then (ADR 0068 decision 1), never a reconstruction.
/// </summary>
public sealed class LayoutDenialOutboxRow
{
    /// <summary>Store-assigned append sequence, for oldest-first draining.</summary>
    public long Sequence { get; set; }

    /// <summary>The entry id, which is also the gate-log record's audit id (idempotent append).</summary>
    public required Guid EntryId { get; init; }

    /// <summary>The tenant the denial happened in.</summary>
    public required string TenantId { get; init; }

    /// <summary>The instant of the act.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The platform's <c>LayoutRelatedDenial</c>, serialized.</summary>
    public required string DenialJson { get; init; }

    /// <summary>The lifecycle state.</summary>
    public required LayoutDenialOutboxState State { get; set; }

    /// <summary>Failed append attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>The most recent append failure.</summary>
    public string? LastError { get; set; }
}

/// <summary>Maps the Layout denial outbox into <see cref="LocalNodeDbContext"/> and its migration path.</summary>
public sealed class LayoutDenialOutboxEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.layout-denial-outbox";

    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter = new(
        value => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<LayoutDenialOutboxRow>(entity =>
        {
            entity.ToTable("layout_denial_outbox");
            entity.HasKey(row => row.Sequence);
            entity.Property(row => row.Sequence).ValueGeneratedOnAdd();
            entity.Property(row => row.EntryId).IsRequired();
            entity.Property(row => row.TenantId).HasMaxLength(256).IsRequired();
            entity.Property(row => row.OccurredAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();
            entity.Property(row => row.DenialJson).HasColumnName("denial_json").HasColumnType("TEXT").IsRequired();
            entity.Property(row => row.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(row => row.Attempts).IsRequired();
            entity.Property(row => row.LastError).HasMaxLength(4096);
            entity.HasIndex(row => row.EntryId).IsUnique().HasDatabaseName("ux_layout_denial_outbox_entry_id");
            entity.HasIndex(row => new { row.State, row.Sequence }).HasDatabaseName("ix_layout_denial_outbox_state_sequence");
        });
    }
}
