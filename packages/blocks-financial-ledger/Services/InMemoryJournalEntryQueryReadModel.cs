using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory v1 implementation of <see cref="IJournalEntryQueryReadModel"/>.
/// Wraps <see cref="IJournalStore.Snapshot"/> + LINQ filtering/sorting/paging
/// behind the interface so the seam is identical to the future ADR-0113 SQLite path.
///
/// <para>
/// Cursor design (ADR 0121 D3): the cursor embeds the resolved skip offset plus
/// a SHA-256-truncated fingerprint of the active filter parameters. A cursor
/// minted under one filter set is rejected when replayed against a different
/// filter set (<see cref="InvalidCursorException"/>).
/// Offset fallback: <see cref="JournalEntryQuery.Skip"/> is used when no cursor provided.
/// </para>
/// </summary>
public sealed class InMemoryJournalEntryQueryReadModel : IJournalEntryQueryReadModel
{
    private readonly IJournalStore _store;

    /// <summary>Creates the read model backed by <paramref name="store"/>.</summary>
    public InMemoryJournalEntryQueryReadModel(IJournalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<PagedResult<JournalEntrySummaryDto>> QueryAsync(
        JournalEntryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.TenantId == default)
            throw new ArgumentException("TenantId is required.", nameof(query));

        var take = Math.Clamp(query.Take, 1, 200);
        var fingerprint = ComputeFingerprint(query);

        // Snapshot is already tenant-scoped by IJournalStore contract.
        var rows = _store.Snapshot(query.TenantId).AsEnumerable();

        // Chart filter.
        if (query.ChartId is not null)
            rows = rows.Where(e => e.ChartId == query.ChartId);

        // Account filter (ADR 0121 §D5): match entries with at least one line on the account.
        if (query.AccountId is GLAccountId account)
            rows = rows.Where(e => e.Lines.Any(l => l.AccountId == account));

        // Date range.
        if (query.FromDate is System.DateOnly from)
            rows = rows.Where(e => e.EntryDate >= from);
        if (query.ToDate is System.DateOnly to)
            rows = rows.Where(e => e.EntryDate <= to);

        // Status filter.
        if (query.Statuses is { Count: > 0 } statuses)
            rows = rows.Where(e => statuses.Contains(e.Status));

        // Source kind filter.
        if (query.SourceKinds is { Count: > 0 } kinds)
            rows = rows.Where(e => kinds.Contains(e.SourceKind));

        // Period filter.
        if (query.PeriodId is FiscalPeriodId period)
            rows = rows.Where(e => e.PeriodId == period);

        // Free-text substring search (FTS5 seam: same contract, different impl in ADR-0113).
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(e =>
                (e.Memo is not null && e.Memo.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (e.SourceReference is not null && e.SourceReference.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (e.ExternalRef is not null && e.ExternalRef.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        // Materialise for sort + total count.
        var allFiltered = rows.ToList();
        var total = allFiltered.Count;

        // Sort (Id is always the final tiebreaker for stable ordering).
        IEnumerable<JournalEntry> sorted = query.SortField switch
        {
            JournalEntrySortField.CreatedAt => query.SortDirection == JournalEntrySortDirection.Descending
                ? allFiltered.OrderByDescending(e => e.CreatedAtUtc.Value).ThenByDescending(e => e.Id.Value)
                : allFiltered.OrderBy(e => e.CreatedAtUtc.Value).ThenBy(e => e.Id.Value),

            JournalEntrySortField.Memo => query.SortDirection == JournalEntrySortDirection.Descending
                ? allFiltered.OrderByDescending(e => e.Memo, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.Id.Value)
                : allFiltered.OrderBy(e => e.Memo, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id.Value),

            // Default: EntryDate (CreatedAt secondary, Id final tiebreaker).
            _ => query.SortDirection == JournalEntrySortDirection.Descending
                ? allFiltered.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAtUtc.Value).ThenByDescending(e => e.Id.Value)
                : allFiltered.OrderBy(e => e.EntryDate).ThenBy(e => e.CreatedAtUtc.Value).ThenBy(e => e.Id.Value),
        };

        var sortedList = sorted.ToList();

        // Pagination: cursor takes precedence over offset.
        int skip;
        if (query.Cursor is string cursor)
        {
            skip = DecodeCursor(cursor, fingerprint, sortedList.Count);
        }
        else
        {
            skip = Math.Max(0, query.Skip ?? 0);
        }

        var page = sortedList.Skip(skip).Take(take).ToList();
        var hasMore = skip + page.Count < total;
        var nextSkip = skip + page.Count;
        var nextCursor = hasMore ? EncodeCursor(nextSkip, fingerprint) : null;

        var result = new PagedResult<JournalEntrySummaryDto>
        {
            Items = page.Select(ToSummaryDto).ToList(),
            Total = total,
            HasMore = hasMore,
            NextCursor = nextCursor,
            Take = take,
        };

        return Task.FromResult(result);
    }

    // ── Cursor encoding/decoding ──────────────────────────────────────────────

    /// <summary>
    /// Encodes an opaque cursor embedding the skip offset and filter fingerprint.
    /// The fingerprint ensures cursor cannot be replayed against a different filter.
    /// </summary>
    private static string EncodeCursor(int skip, string fingerprint)
    {
        var payload = JsonSerializer.Serialize(new { skip, fingerprint });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Decodes the cursor and validates the filter fingerprint.
    /// Returns the skip offset on success.
    /// Throws <see cref="InvalidCursorException"/> when the cursor is malformed or
    /// the fingerprint does not match (replay-across-filter-change).
    /// </summary>
    private static int DecodeCursor(string cursor, string expectedFingerprint, int listCount)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var skip = root.GetProperty("skip").GetInt32();
            var fingerprint = root.GetProperty("fingerprint").GetString();

            if (fingerprint != expectedFingerprint)
                throw new InvalidCursorException(
                    "Cursor fingerprint mismatch: cursor was minted under a different filter set.");

            return Math.Max(0, Math.Min(skip, listCount));
        }
        catch (InvalidCursorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidCursorException("Cursor is malformed.", ex);
        }
    }

    /// <summary>
    /// Computes a stable fingerprint from the query's filter parameters (not sort/page params).
    /// Used to bind cursors to their originating filter set.
    /// </summary>
    private static string ComputeFingerprint(JournalEntryQuery query)
    {
        // Normalise filter params to a deterministic JSON string then SHA-256 truncated.
        var filterRepr = JsonSerializer.Serialize(new
        {
            tenant   = query.TenantId.Value,
            chart    = query.ChartId?.Value,
            account  = query.AccountId?.Value,
            from     = query.FromDate?.ToString("O"),
            to       = query.ToDate?.ToString("O"),
            statuses = query.Statuses is not null
                ? string.Join(",", query.Statuses.Select(s => (int)s).OrderBy(x => x))
                : null,
            kinds    = query.SourceKinds is not null
                ? string.Join(",", query.SourceKinds.Select(k => (int)k).OrderBy(x => x))
                : null,
            period   = query.PeriodId?.Value,
            search   = query.Search?.Trim().ToLowerInvariant(),
            sort     = $"{query.SortField}:{query.SortDirection}",
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filterRepr));
        // Take first 16 bytes (128 bits) — sufficient for fingerprint, not a security secret.
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    // ── DTO mapping ───────────────────────────────────────────────────────────

    private static JournalEntrySummaryDto ToSummaryDto(JournalEntry e) =>
        new(
            Id:           e.Id.Value,
            EntryDate:    e.EntryDate.ToString("O"),
            Memo:         e.Memo,
            Status:       e.Status.ToString(),
            SourceKind:   e.SourceKind.ToString(),
            LineCount:    e.Lines.Count,
            TotalDebits:  (double)e.Lines.Sum(l => l.Debit),
            TotalCredits: (double)e.Lines.Sum(l => l.Credit),
            ReversalOf:   e.ReversalOf?.Value,
            ReversedBy:   e.ReversedBy?.Value,
            CreatedAt:    e.CreatedAtUtc.Value.ToString("O"),
            // ADR 0121 Phase 3a Account column: distinct account ids in first-appearance
            // order across the entry's lines. One → that account; multiple → "— Split —".
            AccountIds:   e.Lines
                              .Select(l => l.AccountId.Value)
                              .Distinct()
                              .ToList());
}

/// <summary>
/// Thrown when a cursor cannot be decoded or its fingerprint does not match
/// the active filter set.
/// </summary>
public sealed class InvalidCursorException : Exception
{
    public InvalidCursorException(string message) : base(message) { }
    public InvalidCursorException(string message, Exception inner) : base(message, inner) { }
}
