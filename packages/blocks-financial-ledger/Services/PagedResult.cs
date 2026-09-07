using System.Collections.Generic;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Generic paged result envelope used by <see cref="IJournalEntryQueryReadModel"/>.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed record PagedResult<T>
{
    /// <summary>The items on this page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Total count across all pages (null when unknown, e.g. cursor-only mode).</summary>
    public int? Total { get; init; }

    /// <summary>True when more items exist beyond this page.</summary>
    public bool HasMore { get; init; }

    /// <summary>Opaque cursor for the next page. Null when HasMore is false.</summary>
    public string? NextCursor { get; init; }

    /// <summary>The effective page size used for this query.</summary>
    public int Take { get; init; }
}
