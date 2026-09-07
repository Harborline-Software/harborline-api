using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Server-backed read model for journal entry queries. Implementations wrap
/// the underlying data source (in-memory Snapshot + LINQ in v1;
/// SQLite FTS5 in the ADR-0113 swap). The seam is identical across both.
///
/// <para>
/// Filtering, sorting, and pagination execute in the read-model implementation,
/// NOT in the Bridge. The Bridge is a thin query-string → JournalEntryQuery →
/// QueryAsync → envelope translator (ADR 0121 §D2).
/// </para>
/// </summary>
public interface IJournalEntryQueryReadModel
{
    /// <summary>
    /// Execute a paged query over the journal entry store.
    /// </summary>
    /// <param name="query">Query parameters. TenantId is required.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paged result of <see cref="JournalEntrySummaryDto"/> items.</returns>
    Task<PagedResult<JournalEntrySummaryDto>> QueryAsync(
        JournalEntryQuery query,
        CancellationToken ct = default);
}
