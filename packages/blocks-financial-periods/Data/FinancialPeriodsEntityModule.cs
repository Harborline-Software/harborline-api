using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialPeriods.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for fiscal-year and fiscal-period persistence.
/// </summary>
public sealed class FinancialPeriodsEntityModule : IHarborlineEntityModule
{
    private static readonly ValueConverter<Instant, DateTimeOffset> InstantConverter =
        new(value => value.Value, value => new Instant(value));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(
            value => value == null ? (DateTimeOffset?)null : value.Value.Value,
            value => value == null ? (Instant?)null : new Instant(value.Value));

    private static readonly ValueConverter<JournalEntryId?, string?> NullableJournalEntryIdConverter =
        new(
            value => value == null ? null : value.Value.Value,
            value => value == null ? (JournalEntryId?)null : new JournalEntryId(value));

    private static readonly ValueConverter<ChartOfAccountsId, string> ChartIdConverter =
        new(value => value.Value, value => new ChartOfAccountsId(value));

    private static readonly ValueConverter<FiscalPeriodId, string> FiscalPeriodIdConverter =
        new(value => value.Value, value => new FiscalPeriodId(value));

    private static readonly ValueConverter<FiscalYearId, string> FiscalYearIdConverter =
        new(value => value.Value, value => new FiscalYearId(value));

    private static readonly ValueConverter<FiscalPeriodKind, string> FiscalPeriodKindConverter =
        new(value => value.ToString(), value => Enum.Parse<FiscalPeriodKind>(value));

    private static readonly ValueConverter<FiscalPeriodStatus, string> FiscalPeriodStatusConverter =
        new(value => value.ToString(), value => Enum.Parse<FiscalPeriodStatus>(value));

    private static readonly ValueConverter<FiscalYearStatus, string> FiscalYearStatusConverter =
        new(value => value.ToString(), value => Enum.Parse<FiscalYearStatus>(value));

    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.financial-periods";

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FiscalYear>(entity =>
        {
            entity.ToTable("fiscal_years");
            entity.HasKey(year => year.Id);

            entity.Property(year => year.Id).HasConversion(FiscalYearIdConverter).HasMaxLength(128).IsRequired();
            entity.Property(year => year.ChartId).HasConversion(ChartIdConverter).HasMaxLength(128).IsRequired();
            entity.Property(year => year.Label).HasMaxLength(64).IsRequired();
            entity.Property(year => year.StartDate).IsRequired();
            entity.Property(year => year.EndDate).IsRequired();
            entity.Property(year => year.Status).HasConversion(FiscalYearStatusConverter).HasMaxLength(32).IsRequired();
            entity.Property(year => year.ClosedAtUtc).HasConversion(NullableInstantConverter);
            entity.Property(year => year.ClosingJournalEntryId).HasConversion(NullableJournalEntryIdConverter).HasMaxLength(128);
            entity.Property(year => year.ExternalRef).HasMaxLength(512);
            entity.Property(year => year.ExternalModifiedAtUtc).HasConversion(NullableInstantConverter);
            entity.Property(year => year.CreatedAtUtc).HasConversion(InstantConverter).IsRequired();
            entity.Property(year => year.Version).IsRequired();
            entity.HasIndex(year => year.ChartId).HasDatabaseName("ix_fiscal_years_chart_id");
            entity.HasIndex(year => year.ExternalRef)
                .HasFilter("\"ExternalRef\" IS NOT NULL")
                .HasDatabaseName("ix_fiscal_years_external_ref");
        });

        modelBuilder.Entity<FiscalPeriod>(entity =>
        {
            entity.ToTable("fiscal_periods");
            entity.HasKey(period => period.Id);

            entity.Property(period => period.Id).HasConversion(FiscalPeriodIdConverter).HasMaxLength(128).IsRequired();
            entity.Property(period => period.FiscalYearId).HasConversion(FiscalYearIdConverter).HasMaxLength(128).IsRequired();
            entity.Property(period => period.ChartId).HasConversion(ChartIdConverter).HasMaxLength(128).IsRequired();
            entity.Property(period => period.StartDate).IsRequired();
            entity.Property(period => period.EndDate).IsRequired();
            entity.Property(period => period.Label).HasMaxLength(64).IsRequired();
            entity.Property(period => period.Kind).HasConversion(FiscalPeriodKindConverter).HasMaxLength(32).IsRequired();
            entity.Property(period => period.Status).HasConversion(FiscalPeriodStatusConverter).HasMaxLength(32).IsRequired();
            entity.Property(period => period.SoftClosedAtUtc).HasConversion(NullableInstantConverter);
            entity.Property(period => period.LockedAtUtc).HasConversion(NullableInstantConverter);
            entity.Property(period => period.ClosingJournalEntryId).HasConversion(NullableJournalEntryIdConverter).HasMaxLength(128);
            entity.Property(period => period.CreatedAtUtc).HasConversion(InstantConverter).IsRequired();
            entity.Property(period => period.Version).IsRequired();
            entity.HasIndex(period => new { period.ChartId, period.StartDate, period.EndDate })
                .HasDatabaseName("ix_fiscal_periods_chart_dates");
            entity.HasIndex(period => period.FiscalYearId).HasDatabaseName("ix_fiscal_periods_fiscal_year_id");
        });
    }
}
