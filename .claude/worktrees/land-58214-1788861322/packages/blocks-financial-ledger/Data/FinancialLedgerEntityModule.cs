using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialLedger.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the financial-ledger cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 1).
///
/// <para>
/// Covers entity configurations for:
/// <list type="bullet">
///   <item><see cref="JournalEntry"/> + <see cref="JournalEntryLine"/> (lines as JSONB)</item>
///   <item><see cref="GLAccount"/> (chart-of-accounts accounts)</item>
///   <item><see cref="ChartOfAccounts"/></item>
///   <item><see cref="LegalEntity"/> + <see cref="LegalEntityOwnership"/></item>
///   <item><c>TenantChartMapping</c> (tenant → default chart lookup)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>JSONB for nested value objects.</b> <see cref="JournalEntryLine"/> has constructor-
/// validated invariants and no stable EF-owned-entity mapping surface; storing the lines
/// list as a Postgres <c>jsonb</c> column avoids fighting EF's owned-entity mechanics and
/// matches the <c>SupportContacts</c> pattern already used in <c>TenantRegistration</c>.
/// Reads reconstruct the lines via <see cref="JsonSerializer"/> — the same approach is
/// correct for any value-object list that has immutable construction semantics.
/// </para>
/// </summary>
public sealed class FinancialLedgerEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.financial-ledger";

    // ── Shared JSON options (mirrors TenantRegistration pattern) ────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Value converters (reused across entity configurations) ────────────────

    private static readonly ValueConverter<TenantId, string> TenantIdConverter =
        new(v => v.Value, v => new TenantId(v));

    private static readonly ValueConverter<Instant, DateTimeOffset> InstantConverter =
        new(v => v.Value, v => new Instant(v));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(v => v == null ? (DateTimeOffset?)null : v.Value.Value,
            v => v == null ? (Instant?)null : new Instant(v.Value));

    // ── IJournalStore mapping ─────────────────────────────────────────────────

    private static readonly ValueConverter<JournalEntryId, string> JournalEntryIdConverter =
        new(v => v.Value, v => new JournalEntryId(v));

    private static readonly ValueConverter<JournalEntryId?, string?> NullableJournalEntryIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (JournalEntryId?)null : new JournalEntryId(v));

    private static readonly ValueConverter<ChartOfAccountsId, string> ChartIdConverter =
        new(v => v.Value, v => new ChartOfAccountsId(v));

    private static readonly ValueConverter<ChartOfAccountsId?, string?> NullableChartIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (ChartOfAccountsId?)null : new ChartOfAccountsId(v));

    private static readonly ValueConverter<FiscalPeriodId, string> FiscalPeriodIdConverter =
        new(v => v.Value, v => new FiscalPeriodId(v));

    private static readonly ValueConverter<FiscalPeriodId?, string?> NullableFiscalPeriodIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (FiscalPeriodId?)null : new FiscalPeriodId(v));

    private static readonly ValueConverter<JournalEntryStatus, string> JournalEntryStatusConverter =
        new(v => v.ToString(), v => Enum.Parse<JournalEntryStatus>(v));

    private static readonly ValueConverter<JournalEntrySource, string> JournalEntrySourceConverter =
        new(v => v.ToString(), v => Enum.Parse<JournalEntrySource>(v));

    // ── IAccountResolver / IChartCatalogService mapping ──────────────────────

    private static readonly ValueConverter<GLAccountId, string> GlAccountIdConverter =
        new(v => v.Value, v => new GLAccountId(v));

    private static readonly ValueConverter<GLAccountId?, string?> NullableGlAccountIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (GLAccountId?)null : new GLAccountId(v));

    private static readonly ValueConverter<GLAccountType, string> GlAccountTypeConverter =
        new(v => v.ToString(), v => Enum.Parse<GLAccountType>(v));

    private static readonly ValueConverter<AccountSubtype?, string?> NullableAccountSubtypeConverter =
        new(v => v == null ? null : v.Value.ToString(),
            v => v == null ? (AccountSubtype?)null : Enum.Parse<AccountSubtype>(v));

    private static readonly ValueConverter<NormalBalance?, string?> NullableNormalBalanceConverter =
        new(v => v == null ? null : v.Value.ToString(),
            v => v == null ? (NormalBalance?)null : Enum.Parse<NormalBalance>(v));

    // ── ILegalEntityRepository mapping ───────────────────────────────────────

    private static readonly ValueConverter<LegalEntityId, string> LegalEntityIdConverter =
        new(v => v.Value, v => new LegalEntityId(v));

    private static readonly ValueConverter<LegalEntityOwnershipId, string> LegalEntityOwnershipIdConverter =
        new(v => v.Value, v => new LegalEntityOwnershipId(v));

    private static readonly ValueConverter<EntityKind, string> EntityKindConverter =
        new(v => v.ToString(), v => Enum.Parse<EntityKind>(v));

    private static readonly ValueConverter<TaxClassification, string> TaxClassificationConverter =
        new(v => v.ToString(), v => Enum.Parse<TaxClassification>(v));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureJournalEntries(modelBuilder);
        ConfigureGlAccounts(modelBuilder);
        ConfigureChartOfAccounts(modelBuilder);
        ConfigureTenantChartMappings(modelBuilder);
        ConfigureLegalEntities(modelBuilder);
        ConfigureLegalEntityOwnerships(modelBuilder);
    }

    private void ConfigureJournalEntries(ModelBuilder modelBuilder)
    {
        // JournalEntryLine list stored as jsonb. EF can't reconstruct the immutable
        // sealed record (validated constructor) through owned-entity navigation; jsonb
        // serialization round-trips cleanly and is indexed for containment queries.
        var linesConverter = new ValueConverter<IReadOnlyList<JournalEntryLine>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<JournalEntryLine>>(v, JsonOptions)
                 ?? new List<JournalEntryLine>());
        var linesComparer = new ValueComparer<IReadOnlyList<JournalEntryLine>>(
            (a, b) => (a ?? new List<JournalEntryLine>()).SequenceEqual(b ?? new List<JournalEntryLine>()),
            v => v == null ? 0 : v.Aggregate(0, (h, l) => HashCode.Combine(h, l.AccountId.Value, l.Debit, l.Credit)),
            v => v.ToList());

        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.ToTable("journal_entries");
            e.HasKey(j => j.Id);

            e.Property(j => j.Id)
                .HasConversion(JournalEntryIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(j => j.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(j => j.EntryDate).IsRequired();
            e.Property(j => j.Memo).HasMaxLength(1024).IsRequired();

            e.Property(j => j.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(j => j.SourceReference).HasMaxLength(512);

            e.Property(j => j.ChartId)
                .HasConversion(NullableChartIdConverter)
                .HasMaxLength(128);

            e.Property(j => j.PostedAtUtc)
                .HasConversion(NullableInstantConverter);

            e.Property(j => j.Status)
                .HasConversion(JournalEntryStatusConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(j => j.SourceKind)
                .HasConversion(JournalEntrySourceConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(j => j.ReversalOf)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(j => j.ReversedBy)
                .HasConversion(NullableJournalEntryIdConverter)
                .HasMaxLength(128);

            e.Property(j => j.PeriodId)
                .HasConversion(NullableFiscalPeriodIdConverter)
                .HasMaxLength(128);

            e.Property(j => j.ExternalRef).HasMaxLength(512);

            // Lines stored as jsonb — see class-level comment.
            e.Property(j => j.Lines)
                .HasColumnName("lines_json")
                .HasColumnType("jsonb")
                .HasConversion(linesConverter, linesComparer)
                .IsRequired();

            // Tenant isolation index — primary access pattern for Snapshot() and list reads.
            e.HasIndex(j => j.TenantId).HasDatabaseName("ix_journal_entries_tenant_id");
            // Chart-scoped reads.
            e.HasIndex(j => new { j.TenantId, j.ChartId })
                .HasDatabaseName("ix_journal_entries_tenant_chart");

            // ADR 0122 §D2 — posting idempotency key. A UNIQUE index over the persisted
            // SourceReference column (tenant-scoped) is the durable dedupe substrate the
            // JournalPostingService phase-1.5 lookup races against: a re-driven source
            // event (same SourceReference) cannot double-post. Keyed on SourceReference
            // ALONE (SourceKind stays a reporting dimension only — it is inconsistently
            // populated and a composite risks a false-negative double-post). The dedupe
            // state IS the recoverable record (mirrors the ADR-0100 ExternalRef precedent
            // — ix_fiscal_years_external_ref above), NEVER a seed-keyed/in-memory KV index
            // (the kernel-ledger.PostingEngine _byIdempotencyKey shape the SC-4 gate
            // forbids). Manual JEs (null SourceReference) are excluded:
            //   • Postgres — the PG-quoted partial filter "SourceReference" IS NOT NULL
            //     keeps the uniqueness constraint off NULL rows (many manual JEs allowed).
            //   • SQLite — LocalNodeDbContext's post-config sweep STRIPS the PG-quoted
            //     filter (C2 SqliteModel_HasNoPgQuotedIndexFilters); the index degrades to
            //     a plain UNIQUE index, and SQLite treats NULLs as distinct in a unique
            //     index, so manual JEs still never collide. Non-null SourceReference rows
            //     remain unique per tenant on both providers.
            e.HasIndex(j => new { j.TenantId, j.SourceReference })
                .IsUnique()
                .HasFilter("\"SourceReference\" IS NOT NULL")
                .HasDatabaseName("ux_journal_entries_tenant_source_ref");
        });
    }

    private void ConfigureGlAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GLAccount>(e =>
        {
            e.ToTable("gl_accounts");
            e.HasKey(a => a.Id);

            e.Property(a => a.Id)
                .HasConversion(GlAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(a => a.ChartId)
                .HasConversion(NullableChartIdConverter)
                .HasMaxLength(128);

            e.Property(a => a.Code).HasMaxLength(32).IsRequired();
            e.Property(a => a.Name).HasMaxLength(256).IsRequired();

            e.Property(a => a.Type)
                .HasConversion(GlAccountTypeConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(a => a.Subtype)
                .HasConversion(NullableAccountSubtypeConverter)
                .HasMaxLength(64);

            e.Property(a => a.NormalBalance)
                .HasConversion(NullableNormalBalanceConverter)
                .HasMaxLength(16);

            e.Property(a => a.ParentAccountId)
                .HasConversion(NullableGlAccountIdConverter)
                .HasMaxLength(128);

            e.Property(a => a.Description).HasMaxLength(1024);
            e.Property(a => a.Currency).HasMaxLength(8);
            e.Property(a => a.TaxLineMappingId).HasMaxLength(128);
            e.Property(a => a.ExternalRef).HasMaxLength(512);

            e.Property(a => a.IsActive).HasDefaultValue(true).IsRequired();
            e.Property(a => a.IsPostable).HasDefaultValue(true).IsRequired();

            e.Property(a => a.CreatedAtUtc)
                .HasConversion(NullableInstantConverter);
            e.Property(a => a.UpdatedAtUtc)
                .HasConversion(NullableInstantConverter);

            // Chart-scoped enumeration (IAccountResolver.EnumerateForChartAsync).
            e.HasIndex(a => a.ChartId).HasDatabaseName("ix_gl_accounts_chart_id");
            // Unique code within chart.
            e.HasIndex(a => new { a.ChartId, a.Code })
                .IsUnique()
                .HasFilter("\"ChartId\" IS NOT NULL")
                .HasDatabaseName("ux_gl_accounts_chart_code");
        });
    }

    private void ConfigureChartOfAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChartOfAccounts>(e =>
        {
            e.ToTable("charts_of_accounts");
            e.HasKey(c => c.Id);

            e.Property(c => c.Id)
                .HasConversion(ChartIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(c => c.LegalEntityId)
                .HasConversion(LegalEntityIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(c => c.Name).HasMaxLength(256).IsRequired();
            e.Property(c => c.BaseCurrency).HasMaxLength(8).IsRequired();
            e.Property(c => c.FiscalYearStartMonth).IsRequired();
            e.Property(c => c.FiscalYearStartDay).IsRequired();

            e.Property(c => c.RetainedEarningsAccountId)
                .HasConversion(NullableGlAccountIdConverter)
                .HasMaxLength(128);

            e.Property(c => c.IsActive).HasDefaultValue(true).IsRequired();

            e.Property(c => c.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(c => c.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.HasIndex(c => c.LegalEntityId).HasDatabaseName("ix_charts_of_accounts_entity_id");
        });
    }

    private static void ConfigureTenantChartMappings(ModelBuilder modelBuilder)
    {
        // Lookup table: (tenant_id, legal_entity_id) → chart_id.
        // Backs IChartCatalogService (null LegalEntityId = tenant default) and
        // IEntityChartResolver (non-null LegalEntityId = entity-scoped book context).
        // PK is composite (TenantId, LegalEntityId) where the default row uses the
        // empty string sentinel "" for LegalEntityId (Postgres cannot include
        // NULLs in a unique constraint / composite PK without special handling).
        modelBuilder.Entity<TenantChartMappingRow>(e =>
        {
            e.ToTable("tenant_chart_mappings");
            // Composite PK: (TenantId, LegalEntityId) — "" = default row.
            e.HasKey(m => new { m.TenantId, m.LegalEntityId });
            e.Property(m => m.TenantId).HasMaxLength(256).IsRequired();
            e.Property(m => m.LegalEntityId).HasMaxLength(128).IsRequired();
            e.Property(m => m.ChartId).HasMaxLength(128).IsRequired();

            // Tenant-scoped reads.
            e.HasIndex(m => m.TenantId).HasDatabaseName("ix_tenant_chart_mappings_tenant_id");
        });
    }

    private void ConfigureLegalEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LegalEntity>(e =>
        {
            e.ToTable("legal_entities");
            e.HasKey(l => l.Id);

            e.Property(l => l.Id)
                .HasConversion(LegalEntityIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(l => l.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(l => l.LegalName).HasMaxLength(512).IsRequired();

            e.Property(l => l.Kind)
                .HasConversion(EntityKindConverter)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(l => l.TaxClassification)
                .HasConversion(TaxClassificationConverter)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(l => l.CommonControlGroupId).HasMaxLength(128);

            e.Property(l => l.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(l => l.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            // Tenant-scoped queries (ListEntitiesAsync).
            e.HasIndex(l => l.TenantId).HasDatabaseName("ix_legal_entities_tenant_id");
        });
    }

    private void ConfigureLegalEntityOwnerships(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LegalEntityOwnership>(e =>
        {
            e.ToTable("legal_entity_ownerships");
            e.HasKey(o => o.Id);

            e.Property(o => o.Id)
                .HasConversion(LegalEntityOwnershipIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(o => o.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(o => o.ParentEntityId)
                .HasConversion(LegalEntityIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(o => o.OwnedEntityId)
                .HasConversion(LegalEntityIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(o => o.OwnershipPercent)
                .HasPrecision(7, 4)
                .IsRequired();

            e.Property(o => o.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(o => o.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.HasIndex(o => o.TenantId).HasDatabaseName("ix_legal_entity_ownerships_tenant_id");
            // Prevent duplicate edges.
            e.HasIndex(o => new { o.TenantId, o.ParentEntityId, o.OwnedEntityId })
                .IsUnique()
                .HasDatabaseName("ux_legal_entity_ownerships_edge");
        });
    }

}

/// <summary>
/// Flat projection row backing the <c>tenant_chart_mappings</c> table.
/// Stores both the tenant → default chart lookup (<see cref="IChartCatalogService"/>)
/// and the entity → chart reverse-lookup (<see cref="IEntityChartResolver"/>).
///
/// <para>
/// PK is composite (<see cref="TenantId"/>, <see cref="LegalEntityId"/>). The
/// sentinel value <see cref="DefaultLegalEntitySentinel"/> (<c>""</c>) is used for the
/// tenant-default row (null is not valid inside a composite PK in EF Core / Postgres).
/// </para>
/// </summary>
public sealed class TenantChartMappingRow
{
    /// <summary>Sentinel for the default-chart row (no entity scope). Stored as the empty string.</summary>
    public const string DefaultLegalEntitySentinel = "";

    /// <summary>The tenant this mapping belongs to (PK part 1).</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// The legal entity this mapping belongs to (PK part 2).
    /// <see cref="DefaultLegalEntitySentinel"/> (<c>""</c>) for the tenant-default chart row.
    /// </summary>
    public required string LegalEntityId { get; init; }

    /// <summary>The chart-of-accounts id for this tenant / entity.</summary>
    public required string ChartId { get; init; }
}
