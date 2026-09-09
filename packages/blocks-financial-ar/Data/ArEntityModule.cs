using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialAr.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the AR (accounts-receivable) cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 2).
///
/// <para>
/// Covers entity configurations for:
/// <list type="bullet">
///   <item><see cref="Invoice"/> + <see cref="InvoiceLine"/> (lines as JSONB)</item>
///   <item><see cref="RecurringInvoiceSchedule"/> + <see cref="RecurringInvoiceLineTemplate"/>
///         (line templates as JSONB; generated-invoices map as JSONB)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>JSONB for nested value objects.</b> <see cref="InvoiceLine"/> and
/// <see cref="RecurringInvoiceLineTemplate"/> have immutable-record construction
/// semantics that do not map cleanly onto EF's owned-entity mechanics. Storing
/// these as <c>jsonb</c> columns matches the <c>JournalEntryLine</c> pattern
/// established in <see cref="Financial.FinancialLedgerEntityModule"/>.
/// </para>
///
/// <para>
/// <b>Global tenant query filters.</b> Both <see cref="Invoice"/> and
/// <see cref="RecurringInvoiceSchedule"/> implement <see cref="IMustHaveTenant"/>;
/// <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/> applies the
/// ambient-tenant filter automatically. EF repos use
/// <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// + explicit TenantId WHERE clause for defence-in-depth (per ADR 0092).
/// </para>
/// </summary>
public sealed class ArEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.financial-ar";

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

    // ── AR typed-id converters ──────────────────────────────────────────────

    private static readonly ValueConverter<InvoiceId, string> InvoiceIdConverter =
        new(v => v.Value, v => new InvoiceId(v));

    private static readonly ValueConverter<InvoiceId?, string?> NullableInvoiceIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (InvoiceId?)null : new InvoiceId(v));

    private static readonly ValueConverter<ChartOfAccountsId, string> ChartIdConverter =
        new(v => v.Value, v => new ChartOfAccountsId(v));

    private static readonly ValueConverter<GLAccountId, string> GlAccountIdConverter =
        new(v => v.Value, v => new GLAccountId(v));

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

    private static readonly ValueConverter<InvoiceStatus, string> InvoiceStatusConverter =
        new(v => v.ToString(), v => Enum.Parse<InvoiceStatus>(v));

    private static readonly ValueConverter<RecurringInvoiceScheduleId, string> RecurringScheduleIdConverter =
        new(v => v.Value, v => new RecurringInvoiceScheduleId(v));

    private static readonly ValueConverter<RecurringScheduleStatus, string> RecurringScheduleStatusConverter =
        new(v => v.ToString(), v => Enum.Parse<RecurringScheduleStatus>(v));

    // ── Sub-ledger typed-id converters (ADR 0120 PR-C additive FK) ─────────

    /// <summary>
    /// Nullable converter for <see cref="SubLedgerAccountId"/> FK on <see cref="Invoice"/>.
    /// Maps the opaque string identity to a varchar column; null when the invoice
    /// is not yet assigned to a sub-ledger account.
    /// </summary>
    private static readonly ValueConverter<SubLedgerAccountId?, string?> NullableSubLedgerAccountIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (SubLedgerAccountId?)null : new SubLedgerAccountId(v));

    // ── JSON deserialization helpers (must be methods, not lambdas, due to CS0834
    //    restriction on statement bodies in expression trees) ────────────────────

    private static IReadOnlyDictionary<DateOnly, InvoiceId> DeserializeGeneratedInvoices(string v)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions)
                  ?? new Dictionary<string, string>();
        return raw.ToDictionary(
            kv => DateOnly.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture),
            kv => new InvoiceId(kv.Value));
    }

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureInvoices(modelBuilder);
        ConfigureRecurringInvoiceSchedules(modelBuilder);
    }

    private void ConfigureInvoices(ModelBuilder modelBuilder)
    {
        // InvoiceLine list stored as jsonb. InvoiceLine has immutable sealed-record
        // construction semantics (validated constructor) — same rationale as
        // JournalEntryLine in FinancialLedgerEntityModule.
        var linesConverter = new ValueConverter<IReadOnlyList<InvoiceLine>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<InvoiceLine>>(v, JsonOptions)
                 ?? new List<InvoiceLine>());
        var linesComparer = new ValueComparer<IReadOnlyList<InvoiceLine>>(
            (a, b) => (a ?? new List<InvoiceLine>()).SequenceEqual(b ?? new List<InvoiceLine>()),
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

        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .HasConversion(InvoiceIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(i => i.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(i => i.ChartId)
                .HasConversion(ChartIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(i => i.InvoiceNumber)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(i => i.CustomerId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(i => i.PropertyId).HasMaxLength(256);

            e.Property(i => i.IssueDate).IsRequired();
            e.Property(i => i.DueDate).IsRequired();

            e.Property(i => i.Currency)
                .HasMaxLength(8)
                .IsRequired()
                .HasDefaultValue("USD");

            // Lines stored as jsonb.
            e.Property(i => i.Lines)
                .HasColumnName("lines_json")
                .HasColumnType("jsonb")
                .HasConversion(linesConverter, linesComparer)
                .IsRequired();

            e.Property(i => i.Subtotal).HasPrecision(18, 4).IsRequired();
            e.Property(i => i.TaxTotal).HasPrecision(18, 4).IsRequired();
            e.Property(i => i.Total).HasPrecision(18, 4).IsRequired();
            e.Property(i => i.AmountPaid).HasPrecision(18, 4).IsRequired();
            e.Property(i => i.Balance).HasPrecision(18, 4).IsRequired();

            e.Property(i => i.Status)
                .HasConversion(InvoiceStatusConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.ArAccountId)
                .HasConversion(GlAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(i => i.Notes).HasMaxLength(2048);
            e.Property(i => i.TermsId).HasMaxLength(128);
            e.Property(i => i.ExternalRef).HasMaxLength(512);

            // ADR 0120 PR-C: additive nullable FK to sub-ledger account.
            // Optional — null when invoice is not yet assigned to a sub-ledger.
            e.Property(i => i.SubLedgerAccountId)
                .HasConversion(NullableSubLedgerAccountIdConverter)
                .HasMaxLength(128);

            e.Property(i => i.JournalEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(i => i.VoidedByEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(i => i.WrittenOffByEntryId)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            // CRDT envelope.
            e.Property(i => i.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(i => i.CreatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(i => i.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(i => i.UpdatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(i => i.DeletedAtUtc)
                .HasConversion(NullableInstantConverter);

            e.Property(i => i.DeletedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(i => i.DeletedReason).HasMaxLength(512);

            e.Property(i => i.Version).IsRequired();

            e.Property(i => i.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(revisionVectorConverter, revisionVectorComparer)
                .IsRequired();

            // Tenant-scoped primary access pattern.
            e.HasIndex(i => i.TenantId).HasDatabaseName("ix_invoices_tenant_id");
            // Chart-scoped list (ListByChartAsync).
            e.HasIndex(i => new { i.TenantId, i.ChartId })
                .HasDatabaseName("ix_invoices_tenant_chart");
            // Customer-scoped list (ListByCustomerAsync).
            e.HasIndex(i => new { i.TenantId, i.ChartId, i.CustomerId })
                .HasDatabaseName("ix_invoices_tenant_chart_customer");
            // Invoice-number lookup (GetByNumberAsync) — unique per (tenant, chart).
            e.HasIndex(i => new { i.TenantId, i.ChartId, i.InvoiceNumber })
                .IsUnique()
                .HasDatabaseName("ux_invoices_tenant_chart_number");
            // External-ref lookup (GetByExternalRefAsync).
            e.HasIndex(i => new { i.TenantId, i.ChartId, i.ExternalRef })
                .HasFilter("\"ExternalRef\" IS NOT NULL")
                .HasDatabaseName("ix_invoices_tenant_chart_external_ref");
        });
    }

    private void ConfigureRecurringInvoiceSchedules(ModelBuilder modelBuilder)
    {
        // LineTemplates list stored as jsonb (immutable record, same rationale as InvoiceLine).
        var lineTemplatesConverter = new ValueConverter<IReadOnlyList<RecurringInvoiceLineTemplate>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<RecurringInvoiceLineTemplate>>(v, JsonOptions)
                 ?? new List<RecurringInvoiceLineTemplate>());
        var lineTemplatesComparer = new ValueComparer<IReadOnlyList<RecurringInvoiceLineTemplate>>(
            (a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : v.Count,
            v => v.ToList());

        // GeneratedInvoices map (DateOnly → InvoiceId) stored as jsonb.
        // Keys serialized as ISO date strings. Deserialization uses a helper method
        // (not an inline lambda) because statement-body lambdas cannot be expression trees.
        var generatedInvoicesConverter = new ValueConverter<IReadOnlyDictionary<DateOnly, InvoiceId>, string>(
            v => JsonSerializer.Serialize(
                     v.ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value.Value),
                     JsonOptions),
            v => DeserializeGeneratedInvoices(v));
        var generatedInvoicesComparer = new ValueComparer<IReadOnlyDictionary<DateOnly, InvoiceId>>(
            (a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : v.Count,
            v => v);

        // IncomeAccountId inside RecurringInvoiceLineTemplate — stored in JSONB via JsonSerializer.
        // GLAccountId serializes via its Value property; the JSON-opts round-trip is verified
        // in EfArPersistenceTests.RecurringSchedule_LineTemplates_Jsonb_RoundTrip.
        // No additional EF converter needed — the containing collection is already JSONB.

        modelBuilder.Entity<RecurringInvoiceSchedule>(e =>
        {
            e.ToTable("recurring_invoice_schedules");
            e.HasKey(s => s.Id);

            e.Property(s => s.Id)
                .HasConversion(RecurringScheduleIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(s => s.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(s => s.ChartId)
                .HasConversion(ChartIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(s => s.CustomerId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(s => s.ArAccountId)
                .HasConversion(GlAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(s => s.RecurrenceRule)
                .HasMaxLength(512)
                .IsRequired();

            e.Property(s => s.StartsOn).IsRequired();
            e.Property(s => s.EndsOn);
            e.Property(s => s.Timezone).HasMaxLength(64).IsRequired();

            e.Property(s => s.LineTemplates)
                .HasColumnName("line_templates_json")
                .HasColumnType("jsonb")
                .HasConversion(lineTemplatesConverter, lineTemplatesComparer)
                .IsRequired();

            e.Property(s => s.Status)
                .HasConversion(RecurringScheduleStatusConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(s => s.LookaheadHorizonDays).IsRequired();
            e.Property(s => s.GenerateLeadDays).IsRequired();

            e.Property(s => s.GeneratedInvoices)
                .HasColumnName("generated_invoices_json")
                .HasColumnType("jsonb")
                .HasConversion(generatedInvoicesConverter, generatedInvoicesComparer)
                .IsRequired();

            e.Property(s => s.LastGeneratedAtUtc);

            // Tenant-scoped list (ListSchedulesAsync).
            e.HasIndex(s => s.TenantId).HasDatabaseName("ix_recurring_invoice_schedules_tenant_id");
            // Status-filtered queries (active schedules for rent-run).
            e.HasIndex(s => new { s.TenantId, s.Status })
                .HasDatabaseName("ix_recurring_invoice_schedules_tenant_status");
        });
    }
}
