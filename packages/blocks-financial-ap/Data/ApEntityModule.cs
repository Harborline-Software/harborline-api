using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialAp.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the AP (accounts-payable) cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 3).
///
/// <para>
/// Covers entity configuration for:
/// <list type="bullet">
///   <item><see cref="Bill"/> + <see cref="BillLine"/> (lines as JSONB)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>JSONB for nested value objects.</b> <see cref="BillLine"/> has immutable
/// sealed-record construction semantics that do not map cleanly onto EF's
/// owned-entity mechanics. Storing the lines list as a <c>jsonb</c> column
/// matches the <c>JournalEntryLine</c> pattern established in
/// <see cref="Financial.FinancialLedgerEntityModule"/> and the
/// <c>InvoiceLine</c> pattern in <see cref="ArEntityModule"/>.
/// </para>
///
/// <para>
/// <b>Global tenant query filters.</b> <see cref="Bill"/> implements
/// <see cref="IMustHaveTenant"/>; <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/>
/// applies the ambient-tenant filter automatically. EF repos use
/// <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// + explicit TenantId WHERE clause for defence-in-depth (per ADR 0092).
/// </para>
/// </summary>
public sealed class ApEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.financial-ap";

    // ── Shared JSON options ─────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Common value converters ─────────────────────────────────────────────

    private static readonly ValueConverter<TenantId, string> TenantIdConverter =
        new(v => v.Value, v => new TenantId(v));

    private static readonly ValueConverter<Instant, DateTimeOffset> InstantConverter =
        new(v => v.Value, v => new Instant(v));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(v => v == null ? (DateTimeOffset?)null : v.Value.Value,
            v => v == null ? (Instant?)null : new Instant(v.Value));

    // ── AP typed-id converters ──────────────────────────────────────────────

    private static readonly ValueConverter<BillId, string> BillIdConverter =
        new(v => v.Value, v => new BillId(v));

    private static readonly ValueConverter<ChartOfAccountsId, string> ChartIdConverter =
        new(v => v.Value, v => new ChartOfAccountsId(v));

    private static readonly ValueConverter<GLAccountId, string> GlAccountIdConverter =
        new(v => v.Value, v => new GLAccountId(v));

    private static readonly ValueConverter<JournalEntryId?, string?> NullableJournalEntryIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (JournalEntryId?)null : new JournalEntryId(v));

    private static readonly ValueConverter<PartyId, string> PartyIdConverter =
        new(v => v.Value, v => new PartyId(v));

    private static readonly ValueConverter<PartyId?, string?> NullablePartyIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (PartyId?)null : new PartyId(v));

    private static readonly ValueConverter<BillStatus, string> BillStatusConverter =
        new(v => v.ToString(), v => Enum.Parse<BillStatus>(v));

    // ── Sub-ledger typed-id converters (ADR 0120 PR-C additive FK) ─────────

    /// <summary>
    /// Nullable converter for <see cref="SubLedgerAccountId"/> FK on <see cref="Bill"/>.
    /// Maps the opaque string identity to a varchar column; null when the bill
    /// is not yet assigned to a sub-ledger account.
    /// </summary>
    private static readonly ValueConverter<SubLedgerAccountId?, string?> NullableSubLedgerAccountIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (SubLedgerAccountId?)null : new SubLedgerAccountId(v));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureBills(modelBuilder);
    }

    private void ConfigureBills(ModelBuilder modelBuilder)
    {
        // BillLine list stored as jsonb. BillLine has immutable sealed-record
        // construction semantics (validated constructor) — same rationale as
        // JournalEntryLine in FinancialLedgerEntityModule and InvoiceLine in ArEntityModule.
        var linesConverter = new ValueConverter<IReadOnlyList<BillLine>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<BillLine>>(v, JsonOptions)
                 ?? new List<BillLine>());
        var linesComparer = new ValueComparer<IReadOnlyList<BillLine>>(
            (a, b) => (a ?? new List<BillLine>()).SequenceEqual(b ?? new List<BillLine>()),
            v => v == null ? 0 : v.Aggregate(0, (h, l) => HashCode.Combine(h, l.Id.Value, l.Amount)),
            v => v.ToList());

        // RevisionVector stored as jsonb (Dictionary<string, long>).
        var revisionVectorConverter = new ValueConverter<IReadOnlyDictionary<string, long>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => (IReadOnlyDictionary<string, long>?)
                     JsonSerializer.Deserialize<Dictionary<string, long>>(v, JsonOptions)
                 ?? new Dictionary<string, long>());
        var revisionVectorComparer = new ValueComparer<IReadOnlyDictionary<string, long>>(
            (a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : v.GetHashCode(),
            v => v);

        modelBuilder.Entity<Bill>(e =>
        {
            e.ToTable("bills");
            e.HasKey(b => b.Id);

            e.Property(b => b.Id)
                .HasConversion(BillIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(b => b.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(b => b.ChartId)
                .HasConversion(ChartIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(b => b.BillNumber)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(b => b.VendorId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(b => b.PropertyId).HasMaxLength(256);

            e.Property(b => b.BillDate).IsRequired();
            e.Property(b => b.DueDate).IsRequired();
            e.Property(b => b.ReceivedDate).IsRequired();

            e.Property(b => b.Currency)
                .HasMaxLength(8)
                .IsRequired()
                .HasDefaultValue("USD");

            // Lines stored as jsonb.
            e.Property(b => b.Lines)
                .HasColumnName("lines_json")
                .HasColumnType("jsonb")
                .HasConversion(linesConverter, linesComparer)
                .IsRequired();

            e.Property(b => b.Subtotal).HasPrecision(18, 4).IsRequired();
            e.Property(b => b.TaxTotal).HasPrecision(18, 4).IsRequired();
            e.Property(b => b.Total).HasPrecision(18, 4).IsRequired();
            e.Property(b => b.AmountPaid).HasPrecision(18, 4).IsRequired();
            e.Property(b => b.Balance).HasPrecision(18, 4).IsRequired();

            e.Property(b => b.Status)
                .HasConversion(BillStatusConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(b => b.ApAccountId)
                .HasConversion(GlAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(b => b.Notes).HasMaxLength(2048);
            e.Property(b => b.TermsId).HasMaxLength(128);
            e.Property(b => b.ExternalRef).HasMaxLength(512);
            e.Property(b => b.ExternalRefVersion).HasMaxLength(256);

            // ADR 0120 PR-C: additive nullable FK to sub-ledger account.
            // Optional — null when bill is not yet assigned to a sub-ledger.
            e.Property(b => b.SubLedgerAccountId)
                .HasConversion(NullableSubLedgerAccountIdConverter)
                .HasMaxLength(128);

            e.Property(b => b.JournalEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(b => b.VoidedByEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(b => b.ApprovedByUserId).HasMaxLength(256);

            e.Property(b => b.ApprovedAtUtc)
                .HasConversion(NullableInstantConverter);

            // CRDT envelope.
            e.Property(b => b.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(b => b.CreatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(b => b.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(b => b.UpdatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(b => b.DeletedAtUtc)
                .HasConversion(NullableInstantConverter);

            e.Property(b => b.DeletedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(b => b.DeletedReason).HasMaxLength(512);

            e.Property(b => b.Version).IsRequired();

            e.Property(b => b.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(revisionVectorConverter, revisionVectorComparer)
                .IsRequired();

            // Tenant-scoped primary access pattern.
            e.HasIndex(b => b.TenantId).HasDatabaseName("ix_bills_tenant_id");
            // Chart-scoped list (ListByChartAsync).
            e.HasIndex(b => new { b.TenantId, b.ChartId })
                .HasDatabaseName("ix_bills_tenant_chart");
            // Vendor-scoped list (ListByVendorAsync).
            e.HasIndex(b => new { b.TenantId, b.ChartId, b.VendorId })
                .HasDatabaseName("ix_bills_tenant_chart_vendor");
            // Vendor-bill-number lookup (GetByVendorBillNumberAsync) — unique per (tenant, chart, vendor).
            e.HasIndex(b => new { b.TenantId, b.ChartId, b.VendorId, b.BillNumber })
                .IsUnique()
                .HasDatabaseName("ux_bills_tenant_chart_vendor_number");
            // External-ref lookup (GetByExternalRefAsync).
            e.HasIndex(b => new { b.TenantId, b.ChartId, b.ExternalRef })
                .HasFilter("\"ExternalRef\" IS NOT NULL")
                .HasDatabaseName("ix_bills_tenant_chart_external_ref");
        });
    }
}
