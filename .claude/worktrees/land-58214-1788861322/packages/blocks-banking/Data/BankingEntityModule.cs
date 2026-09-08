using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.Banking.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the Banking cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 4).
///
/// <para>
/// Covers entity configurations for:
/// <list type="bullet">
///   <item><see cref="BankAccount"/> — bank/credit-card/cash account master
///         (tombstone-not-delete; <see cref="IArchivable"/> with stored
///         <see cref="BankAccount.ArchivedAt"/> per ADR 0108 Option 1)</item>
///   <item><see cref="StatementLine"/> — append-and-correct; no hard-delete;
///         <see cref="ImportSourceRef"/> stored as JSONB (value-object struct)</item>
///   <item><see cref="MatchLink"/> — many-to-many statement-line ↔ ledger-transaction
///         link table (ADR 0112 fin-acct C3); reverse-not-delete</item>
///   <item><see cref="Reconciliation"/> — per-account / per-period reconciliation
///         aggregate; bank-rec lock INDEPENDENT of fiscal-period lock (ADR 0112
///         fin-acct C1)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Global tenant query filters.</b> All four entities implement
/// <see cref="IMustHaveTenant"/>; <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/>
/// applies the ambient-tenant filter automatically. EF repos use
/// <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// + explicit TenantId WHERE clause for defence-in-depth (ADR 0092).
/// </para>
///
/// <para>
/// <b>RawProviderBlob / credential handling (ADR 0112 sec-eng C2).</b>
/// <see cref="StatementLine.RawProviderBlob"/> is persisted as a plain opaque
/// column. The EF layer NEVER emits it in log messages or exception detail;
/// the column is excluded from any diagnostic / error projection. The field is
/// nullable (null for file-import lines) and its retention is bounded by the
/// standard Postgres row-level archival policy for the banking cluster.
/// </para>
///
/// <para>
/// <b>JSONB for ImportSourceRef.</b> <see cref="StatementLine.Source"/> is a
/// value-object struct with three fields. Storing it as JSONB avoids fighting
/// EF's owned-entity mechanics for a struct and matches the
/// <c>JournalEntryLine</c> JSONB precedent.
/// </para>
/// </summary>
public sealed class BankingEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.banking";

    // ── Shared JSON options ─────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Common converters ───────────────────────────────────────────────────

    private static readonly ValueConverter<TenantId, string> TenantIdConverter =
        new(v => v.Value, v => new TenantId(v));

    private static readonly ValueConverter<Instant, DateTimeOffset> InstantConverter =
        new(v => v.Value, v => new Instant(v));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(v => v == null ? (DateTimeOffset?)null : v.Value.Value,
            v => v == null ? (Instant?)null : new Instant(v.Value));

    // ── BankAccount typed-id converters ─────────────────────────────────────

    private static readonly ValueConverter<BankAccountId, string> BankAccountIdConverter =
        new(v => v.Value, v => new BankAccountId(v));

    private static readonly ValueConverter<BankAccountKind, string> BankAccountKindConverter =
        new(v => v.ToString(), v => Enum.Parse<BankAccountKind>(v));

    private static readonly ValueConverter<GLAccountId, string> GlAccountIdConverter =
        new(v => v.Value, v => new GLAccountId(v));

    private static readonly ValueConverter<ChartOfAccountsId, string> ChartIdConverter =
        new(v => v.Value, v => new ChartOfAccountsId(v));

    // LedgerAccountRef: readonly record struct — OwnsOne does not work with value types in EF Core.
    // Store as JSONB (two-field struct; compact; consistent with other value-object JSONB pattern).
    private static readonly ValueConverter<LedgerAccountRef, string> LedgerAccountRefConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<LedgerAccountRef>(v, JsonOptions));

    // LedgerTransactionRef: single-field readonly record struct wrapping JournalEntryId.
    // Store the JournalEntryId value directly as a string column.
    private static readonly ValueConverter<LedgerTransactionRef, string> LedgerTransactionRefConverter =
        new(v => v.JournalEntryId.Value,
            v => new LedgerTransactionRef(new JournalEntryId(v)));

    // ── StatementLine typed-id + enum converters ────────────────────────────

    private static readonly ValueConverter<StatementLineId, string> StatementLineIdConverter =
        new(v => v.Value, v => new StatementLineId(v));

    private static readonly ValueConverter<ReconciliationState, string> ReconciliationStateConverter =
        new(v => v.ToString(), v => Enum.Parse<ReconciliationState>(v));

    // ── MatchLink typed-id + enum converters ────────────────────────────────

    private static readonly ValueConverter<MatchLinkId, string> MatchLinkIdConverter =
        new(v => v.Value, v => new MatchLinkId(v));

    private static readonly ValueConverter<MatchLinkState, string> MatchLinkStateConverter =
        new(v => v.ToString(), v => Enum.Parse<MatchLinkState>(v));

    private static readonly ValueConverter<JournalEntryId, string> JournalEntryIdConverter =
        new(v => v.Value, v => new JournalEntryId(v));

    // ── Reconciliation typed-id + enum converters ────────────────────────────

    private static readonly ValueConverter<ReconciliationId, string> ReconciliationIdConverter =
        new(v => v.Value, v => new ReconciliationId(v));

    private static readonly ValueConverter<BankReconciliationLockState, string> LockStateConverter =
        new(v => v.ToString(), v => Enum.Parse<BankReconciliationLockState>(v));

    // FiscalPeriodId re-used from financial-periods cluster
    private static readonly ValueConverter<FiscalPeriodId, string> FiscalPeriodIdConverter =
        new(v => v.Value, v => new FiscalPeriodId(v));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureBankAccounts(modelBuilder);
        ConfigureStatementLines(modelBuilder);
        ConfigureMatchLinks(modelBuilder);
        ConfigureReconciliations(modelBuilder);
    }

    // ────────────────────────────────────────────────────────────────────────
    // BankAccount
    // ────────────────────────────────────────────────────────────────────────
    private void ConfigureBankAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAccount>(e =>
        {
            e.ToTable("bank_accounts");
            e.HasKey(a => a.Id);

            e.Property(a => a.Id)
                .HasConversion(BankAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(a => a.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            // EntityId is a struct with Scheme/Authority/LocalPart — store as its ToString()
            // representation ("scheme:authority/localPart"). EntityId.Parse is the canonical inverse.
            e.Property(a => a.EntityId)
                .HasConversion(
                    v => v.ToString(),
                    v => EntityId.Parse(v))
                .HasMaxLength(512)
                .IsRequired();

            e.Property(a => a.Kind)
                .HasConversion(BankAccountKindConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(a => a.DisplayName)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(a => a.InstitutionName)
                .HasMaxLength(256);

            e.Property(a => a.Currency)
                .HasConversion(
                    v => v.Iso4217,
                    v => new CurrencyCode(v))
                .HasMaxLength(8)
                .IsRequired();

            // LedgerAccountRef: readonly record struct — stored as JSONB (OwnsOne requires reference type).
            e.Property(a => a.LinkedLedgerAccount)
                .HasColumnName("linked_ledger_account_json")
                .HasColumnType("jsonb")
                .HasConversion(LedgerAccountRefConverter)
                .IsRequired();

            e.Property(a => a.OpeningBalance)
                .HasPrecision(18, 4)
                .IsRequired();

            e.Property(a => a.CutoverAsOf)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(a => a.ArchivedAt)
                .HasConversion(NullableInstantConverter);

            e.Property(a => a.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(a => a.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            // Tenant-scoped list (IBankAccountRepository.ListAsync).
            e.HasIndex(a => a.TenantId).HasDatabaseName("ix_bank_accounts_tenant_id");
            // Entity-scoped lookup.
            e.HasIndex(a => new { a.TenantId, a.EntityId })
                .HasDatabaseName("ix_bank_accounts_tenant_entity");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // StatementLine
    // ────────────────────────────────────────────────────────────────────────
    private void ConfigureStatementLines(ModelBuilder modelBuilder)
    {
        // ImportSourceRef is a value-object struct with 3 fields (Kind, BatchId, OrdinalWithinBatch).
        // Store as JSONB to avoid owned-entity mechanics for a struct. Matches the JournalEntryLine
        // JSONB precedent.
        var sourceConverter = new ValueConverter<ImportSourceRef, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<ImportSourceRef>(v, JsonOptions));
        var sourceComparer = new ValueComparer<ImportSourceRef>(
            (a, b) => a == b,
            v => v.GetHashCode(),
            v => v);

        modelBuilder.Entity<StatementLine>(e =>
        {
            e.ToTable("statement_lines");
            e.HasKey(s => s.Id);

            e.Property(s => s.Id)
                .HasConversion(StatementLineIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(s => s.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(s => s.AccountId)
                .HasConversion(BankAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(s => s.ProviderTxnId)
                .HasMaxLength(256);

            e.Property(s => s.PostedAt)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(s => s.Amount)
                .HasPrecision(18, 4)
                .IsRequired();

            e.Property(s => s.Currency)
                .HasConversion(
                    v => v.Iso4217,
                    v => new CurrencyCode(v))
                .HasMaxLength(8)
                .IsRequired();

            e.Property(s => s.Description)
                .HasMaxLength(1024)
                .IsRequired();

            e.Property(s => s.Pending).IsRequired();

            e.Property(s => s.State)
                .HasConversion(ReconciliationStateConverter)
                .HasMaxLength(32)
                .IsRequired();

            // ImportSourceRef stored as JSONB (struct — 3 fields: Kind, BatchId, OrdinalWithinBatch).
            e.Property(s => s.Source)
                .HasColumnName("source_json")
                .HasColumnType("jsonb")
                .HasConversion(sourceConverter, sourceComparer)
                .IsRequired();

            // ADR 0112 sec-eng C2: RawProviderBlob is persisted as an opaque sealed column.
            // It is NEVER emitted in logs, errors, or diagnostic projections.
            // Null for file-import lines; nullable for feed lines.
            // Max length uncapped — raw bank feed payloads can be large; stored as text.
            e.Property(s => s.RawProviderBlob)
                .HasColumnName("raw_provider_blob")
                .HasMaxLength(65535);

            e.Property(s => s.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            // Account-scoped reads (ListByAccountAsync + ListByBatchAsync — primary access pattern).
            e.HasIndex(s => new { s.TenantId, s.AccountId })
                .HasDatabaseName("ix_statement_lines_tenant_account");
            // Provider-txn dedup (FindByProviderTxnIdAsync — per ADR 0112 fin-acct N1).
            e.HasIndex(s => new { s.TenantId, s.AccountId, s.ProviderTxnId })
                .HasFilter("\"ProviderTxnId\" IS NOT NULL")
                .HasDatabaseName("ix_statement_lines_tenant_account_provider_txn");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // MatchLink
    // ────────────────────────────────────────────────────────────────────────
    private void ConfigureMatchLinks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MatchLink>(e =>
        {
            e.ToTable("match_links");
            e.HasKey(m => m.Id);

            e.Property(m => m.Id)
                .HasConversion(MatchLinkIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(m => m.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(m => m.StatementLine)
                .HasConversion(StatementLineIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            // LedgerTransactionRef: single-field readonly record struct wrapping JournalEntryId.
            // Stored as a string column containing just the JournalEntryId value.
            e.Property(m => m.LedgerTransaction)
                .HasConversion(LedgerTransactionRefConverter)
                .HasColumnName("JournalEntryId")
                .HasMaxLength(128)
                .IsRequired();

            e.Property(m => m.Amount)
                .HasPrecision(18, 4)
                .IsRequired();

            e.Property(m => m.State)
                .HasConversion(MatchLinkStateConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(m => m.AcceptedAt)
                .HasConversion(NullableInstantConverter);

            // Statement-line–scoped reads (ListByStatementLineAsync).
            e.HasIndex(m => new { m.TenantId, m.StatementLine })
                .HasDatabaseName("ix_match_links_tenant_statement_line");
            // Tenant-scoped reads (general + ListByLedgerTransactionAsync filters in-memory
            // on JournalEntryId from the owned column; index covers the tenant prefix).
            e.HasIndex(m => m.TenantId)
                .HasDatabaseName("ix_match_links_tenant_id");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // Reconciliation
    // ────────────────────────────────────────────────────────────────────────
    private void ConfigureReconciliations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reconciliation>(e =>
        {
            e.ToTable("reconciliations");
            e.HasKey(r => r.Id);

            e.Property(r => r.Id)
                .HasConversion(ReconciliationIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(r => r.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(r => r.AccountId)
                .HasConversion(BankAccountIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            // FiscalPeriodId (from blocks-financial-periods) — reuse FiscalPeriodIdConverter.
            e.Property(r => r.PeriodId)
                .HasConversion(FiscalPeriodIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(r => r.OpeningBalance)
                .HasPrecision(18, 4)
                .IsRequired();

            e.Property(r => r.StatementClosingBalance)
                .HasPrecision(18, 4)
                .IsRequired();

            e.Property(r => r.ClearedMovement)
                .HasPrecision(18, 4)
                .IsRequired();

            // ADR 0112 fin-acct C1: bank-rec lock is INDEPENDENT of fiscal-period lock.
            e.Property(r => r.LockState)
                .HasConversion(LockStateConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(r => r.LockedAt)
                .HasConversion(NullableInstantConverter);

            // LockedByPrincipalId is an opaque principal string (not PartyId-typed in the model).
            e.Property(r => r.LockedByPrincipalId)
                .HasMaxLength(256);

            e.Property(r => r.CreatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(r => r.UpdatedAtUtc)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(r => r.Version)
                .IsConcurrencyToken()
                .IsRequired();

            // Account-scoped reads (ListByAccountAsync).
            e.HasIndex(r => new { r.TenantId, r.AccountId })
                .HasDatabaseName("ix_reconciliations_tenant_account");
            // Per-account/period uniqueness (GetByAccountPeriodAsync — one reconciliation per pair).
            e.HasIndex(r => new { r.TenantId, r.AccountId, r.PeriodId })
                .IsUnique()
                .HasDatabaseName("ux_reconciliations_tenant_account_period");
        });
    }
}
