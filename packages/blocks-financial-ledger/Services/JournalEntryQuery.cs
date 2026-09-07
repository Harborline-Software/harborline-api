using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Server-side query parameters for <see cref="IJournalEntryQueryReadModel.QueryAsync"/>.
/// TenantId is always first (read-discipline idiom — ADR 0092 §A1).
/// </summary>
public sealed record JournalEntryQuery
{
    /// <summary>Required. Server-derived; never frontend-passed.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>Optional chart scope (resolved via IChartCatalogService for the entity).</summary>
    public ChartOfAccountsId? ChartId { get; init; }

    /// <summary>
    /// Optional GL account filter (ADR 0121 §D5 — the filter-input half of Phase 3a).
    /// When set, returns only entries with at least one line posted to this account.
    /// Null = all accounts. Pairs with <see cref="JournalEntrySummaryDto.AccountIds"/>
    /// (the output half that already landed).
    /// </summary>
    public GLAccountId? AccountId { get; init; }

    /// <summary>Inclusive lower bound on EntryDate.</summary>
    public System.DateOnly? FromDate { get; init; }

    /// <summary>Inclusive upper bound on EntryDate.</summary>
    public System.DateOnly? ToDate { get; init; }

    /// <summary>Optional status filter (Draft / Posted / Reversed). Null = all statuses.</summary>
    public IReadOnlyList<JournalEntryStatus>? Statuses { get; init; }

    /// <summary>Optional source-kind filter. Null = all kinds.</summary>
    public IReadOnlyList<JournalEntrySource>? SourceKinds { get; init; }

    /// <summary>Optional fiscal period filter.</summary>
    public FiscalPeriodId? PeriodId { get; init; }

    /// <summary>Free-text search (substring match in v1; FTS5 in ADR-0113 SQLite impl).</summary>
    public string? Search { get; init; }

    /// <summary>Sort field. Default: EntryDate.</summary>
    public JournalEntrySortField SortField { get; init; } = JournalEntrySortField.EntryDate;

    /// <summary>Sort direction. Default: Descending.</summary>
    public JournalEntrySortDirection SortDirection { get; init; } = JournalEntrySortDirection.Descending;

    /// <summary>
    /// Opaque cursor from a prior <see cref="PagedResult{T}.NextCursor"/>.
    /// Must embed a filter-fingerprint so a cursor minted under one filter
    /// set cannot be replayed against another (D3 integrity requirement).
    /// When both Cursor and Skip are provided, Cursor takes precedence.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>Offset fallback for "jump to page N" affordance. Ignored when Cursor is present.</summary>
    public int? Skip { get; init; }

    /// <summary>Page size. Default: 50. Maximum: 200.</summary>
    public int Take { get; init; } = 50;
}

/// <summary>Sort field options for <see cref="JournalEntryQuery"/>.</summary>
public enum JournalEntrySortField
{
    /// <summary>Sort by entry accounting date (default).</summary>
    EntryDate,
    /// <summary>Sort by creation timestamp.</summary>
    CreatedAt,
    /// <summary>Sort by memo text.</summary>
    Memo,
}

/// <summary>Sort direction for <see cref="JournalEntryQuery"/>.</summary>
public enum JournalEntrySortDirection
{
    Ascending,
    Descending,
}
