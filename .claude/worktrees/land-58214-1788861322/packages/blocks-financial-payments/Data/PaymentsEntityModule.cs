using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialPayments.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the Payments cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 3).
///
/// <para>
/// Covers entity configurations for:
/// <list type="bullet">
///   <item><see cref="Payment"/> — cash-movement events (CRDT envelope, no soft-delete,
///         no hard-delete per CIC ruling 2026-06-03 — reversal-only via BounceAsync)</item>
///   <item><see cref="PaymentApplication"/> — the many-to-many link between a
///         Payment and its target Invoice/Bill</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Global tenant query filters.</b> Both <see cref="Payment"/> and
/// <see cref="PaymentApplication"/> implement <see cref="IMustHaveTenant"/>;
/// <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/> applies the
/// ambient-tenant filter automatically. EF repos use
/// <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// + explicit TenantId WHERE clause for defence-in-depth (per ADR 0092).
/// </para>
///
/// <para>
/// <b>Payment.Applications snapshot.</b> <see cref="Payment.Applications"/> is
/// a load-time read-only snapshot populated by the read model — it is NOT the
/// authoritative store of application records (that is <see cref="PaymentApplication"/>
/// table). The column is stored as JSONB for the snapshot convenience. The EF
/// repo does NOT write this field back; it is always the empty list on upsert
/// and populated by the service layer from <see cref="PaymentApplication"/> rows.
/// </para>
/// </summary>
public sealed class PaymentsEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.financial-payments";

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

    // ── Payments typed-id converters ────────────────────────────────────────

    private static readonly ValueConverter<PaymentId, string> PaymentIdConverter =
        new(v => v.Value, v => new PaymentId(v));

    private static readonly ValueConverter<PaymentApplicationId, string> PaymentApplicationIdConverter =
        new(v => v.Value, v => new PaymentApplicationId(v));

    private static readonly ValueConverter<PaymentApplicationId?, string?> NullablePaymentApplicationIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (PaymentApplicationId?)null : new PaymentApplicationId(v));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (Instant?)null : new Instant(v.Value));

    private static readonly ValueConverter<ChartOfAccountsId, string> ChartIdConverter =
        new(v => v.Value, v => new ChartOfAccountsId(v));

    private static readonly ValueConverter<GLAccountId?, string?> NullableGlAccountIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (GLAccountId?)null : new GLAccountId(v));

    private static readonly ValueConverter<JournalEntryId?, string?> NullableJournalEntryIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (JournalEntryId?)null : new JournalEntryId(v));

    private static readonly ValueConverter<PartyId, string> PartyIdConverter =
        new(v => v.Value, v => new PartyId(v));

    private static readonly ValueConverter<PartyId?, string?> NullablePartyIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (PartyId?)null : new PartyId(v));

    private static readonly ValueConverter<PaymentStatus, string> PaymentStatusConverter =
        new(v => v.ToString(), v => Enum.Parse<PaymentStatus>(v));

    private static readonly ValueConverter<PaymentDirection, string> PaymentDirectionConverter =
        new(v => v.ToString(), v => Enum.Parse<PaymentDirection>(v));

    private static readonly ValueConverter<PaymentMethod, string> PaymentMethodConverter =
        new(v => v.ToString(), v => Enum.Parse<PaymentMethod>(v));

    private static readonly ValueConverter<AppliedTo, string> AppliedToConverter =
        new(v => v.ToString(), v => Enum.Parse<AppliedTo>(v));

    private static readonly ValueConverter<AppliedTo?, string?> NullableAppliedToConverter =
        new(v => v == null ? null : v.Value.ToString(),
            v => v == null ? (AppliedTo?)null : Enum.Parse<AppliedTo>(v));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigurePayments(modelBuilder);
        ConfigurePaymentApplications(modelBuilder);
    }

    private void ConfigurePayments(ModelBuilder modelBuilder)
    {
        // Payment.Applications is a load-time read-only snapshot (not authoritative store).
        // Stored as jsonb for convenience; the EF repo never writes this field — it is always
        // the empty list on upsert and rehydrated by the service layer from the PaymentApplication
        // table. See class-level XML doc for rationale.
        var applicationsConverter = new ValueConverter<IReadOnlyList<PaymentApplication>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<PaymentApplication>>(v, JsonOptions)
                 ?? new List<PaymentApplication>());
        var applicationsComparer = new ValueComparer<IReadOnlyList<PaymentApplication>>(
            (a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : v.Count,
            v => v.ToList());

        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(p => p.Id);

            e.Property(p => p.Id)
                .HasConversion(PaymentIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(p => p.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(p => p.ChartId)
                .HasConversion(ChartIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(p => p.Direction)
                .HasConversion(PaymentDirectionConverter)
                .HasMaxLength(16)
                .IsRequired();

            e.Property(p => p.PaymentNumber)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(p => p.PartyId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(p => p.IntendedTargetType)
                .HasConversion(NullableAppliedToConverter)
                .HasMaxLength(16);

            e.Property(p => p.IntendedTargetId)
                .HasMaxLength(128);

            e.Property(p => p.BankAccountId)
                .HasConversion(NullableGlAccountIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.PaymentDate).IsRequired();

            e.Property(p => p.Amount).HasPrecision(18, 4).IsRequired();
            e.Property(p => p.UnappliedAmount).HasPrecision(18, 4).IsRequired();

            e.Property(p => p.Currency)
                .HasMaxLength(8)
                .IsRequired()
                .HasDefaultValue("USD");

            e.Property(p => p.Method)
                .HasConversion(PaymentMethodConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(p => p.Reference).HasMaxLength(256);

            e.Property(p => p.Status)
                .HasConversion(PaymentStatusConverter)
                .HasMaxLength(32)
                .IsRequired();

            // Applications snapshot stored as jsonb (load-time convenience — not authoritative).
            e.Property(p => p.Applications)
                .HasColumnName("applications_json")
                .HasColumnType("jsonb")
                .HasConversion(applicationsConverter, applicationsComparer)
                .IsRequired();

            e.Property(p => p.JournalEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.BouncedByEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.Notes).HasMaxLength(2048);
            e.Property(p => p.ExternalRef).HasMaxLength(512);
            e.Property(p => p.ExternalRefVersion).HasMaxLength(256);
            e.Property(p => p.SourceReference).HasMaxLength(512);

            // CRDT envelope (Payment has no DeletedAtUtc — reversal-only; no soft-delete).
            e.Property(p => p.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(p => p.CreatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(p => p.UpdatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.Version).IsRequired();

            // Tenant-scoped primary access pattern.
            e.HasIndex(p => p.TenantId).HasDatabaseName("ix_payments_tenant_id");
            // Chart-scoped list ordered by date (ListByChartAsync — DESC order done in code).
            e.HasIndex(p => new { p.TenantId, p.ChartId, p.PaymentDate })
                .HasDatabaseName("ix_payments_tenant_chart_date");
            // Party-scoped list (ListByPartyAsync).
            e.HasIndex(p => new { p.TenantId, p.ChartId, p.PartyId })
                .HasDatabaseName("ix_payments_tenant_chart_party");
            // Record-time settlement intent supports target-scoped pending-payment reads before
            // a clearing journal permits creation of the authoritative PaymentApplication row.
            e.HasIndex(p => new { p.TenantId, p.IntendedTargetType, p.IntendedTargetId })
                .HasDatabaseName("ix_payments_tenant_intended_target");
            // External-ref lookup (GetByExternalRefAsync).
            e.HasIndex(p => new { p.TenantId, p.ChartId, p.ExternalRef })
                .HasFilter("\"ExternalRef\" IS NOT NULL")
                .HasDatabaseName("ix_payments_tenant_chart_external_ref");
            // Payment-number uniqueness per (tenant, chart).
            e.HasIndex(p => new { p.TenantId, p.ChartId, p.PaymentNumber })
                .IsUnique()
                .HasDatabaseName("ux_payments_tenant_chart_number");

            // SourceReference (tenant-scoped) is the durable dedupe substrate for the payment-WRITE
            // idempotency key (ADR 0122 §D4 T2 — SourceReference ALONE, mirroring the JournalEntry
            // ux_journal_entries_tenant_source_ref unique index from P1). A re-driven record (network
            // retry / double-submit carrying the same SourceReference) cannot double-create. Keyed on
            // SourceReference alone (NOT (SourceKind, SourceReference) — SourceKind is reporting-only).
            // Manual/native payments (null SourceReference) are excluded:
            //   • Postgres — the PG-quoted partial filter "SourceReference" IS NOT NULL excludes them.
            //   • SQLite — LocalNodeDbContext's post-config sweep STRIPS the PG-quoted filter, but
            //     SQLite already treats NULLs as distinct in a unique index, so null-SourceReference
            //     rows still never collide. Non-null SourceReference rows are unique per tenant.
            e.HasIndex(p => new { p.TenantId, p.SourceReference })
                .IsUnique()
                .HasFilter("\"SourceReference\" IS NOT NULL")
                .HasDatabaseName("ux_payments_tenant_source_ref");
        });
    }

    private void ConfigurePaymentApplications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentApplication>(e =>
        {
            e.ToTable("payment_applications");
            e.HasKey(a => a.Id);

            e.Property(a => a.Id)
                .HasConversion(PaymentApplicationIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(a => a.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(a => a.PaymentId)
                .HasConversion(PaymentIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(a => a.AppliedTo)
                .HasConversion(AppliedToConverter)
                .HasMaxLength(16)
                .IsRequired();

            // TargetId is a string union — InvoiceId.Value or BillId.Value.
            e.Property(a => a.TargetId)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(a => a.AmountApplied).HasPrecision(18, 4).IsRequired();
            e.Property(a => a.DiscountAmount).HasPrecision(18, 4).IsRequired();
            e.Property(a => a.WriteoffAmount).HasPrecision(18, 4).IsRequired();

            e.Property(a => a.AppliedDate).IsRequired();

            e.Property(a => a.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(a => a.ReversedAtUtc)
                .HasConversion(NullableInstantConverter);

            e.Property(a => a.ReversedByApplicationId)
                .HasConversion(NullablePaymentApplicationIdConverter)
                .HasMaxLength(128);

            e.Property(a => a.ReversesApplicationId)
                .HasConversion(NullablePaymentApplicationIdConverter)
                .HasMaxLength(128);

            // Payment-scoped list (ListByPaymentAsync).
            e.HasIndex(a => new { a.TenantId, a.PaymentId })
                .HasDatabaseName("ix_payment_applications_tenant_payment");
            // Target-scoped list (ListByTargetAsync — invoice/bill drill-down).
            e.HasIndex(a => new { a.TenantId, a.TargetId })
                .HasDatabaseName("ix_payment_applications_tenant_target");

            // At most one contra row may reverse a given application for a tenant.
            e.HasIndex(a => new { a.TenantId, a.ReversesApplicationId })
                .IsUnique()
                .HasFilter("\"ReversesApplicationId\" IS NOT NULL")
                .HasDatabaseName("ux_payment_applications_tenant_reverses");
        });
    }
}
