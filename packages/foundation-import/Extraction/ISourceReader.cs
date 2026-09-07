using System.Collections.Generic;
using System.Threading;

namespace Harborline.Api.Foundation.Import.Extraction;

/// <summary>
/// The extraction-adapter SEAM (ADR 0100 C6) — abstracts the ERPNext source so
/// the access mode is swappable behind a stable boundary. The upsert passes
/// (A1+) and the orchestrator (A7) depend ONLY on this interface and the
/// <see cref="SourceRow"/> currency — never on a concrete adapter type.
/// </summary>
/// <remarks>
/// <para>
/// <b>STRICTLY READ-ONLY (ADR 0100 C4 clean-room; C-CLEANROOM (a)).</b> This
/// interface exposes ONLY read operations — there is intentionally no
/// write / update / delete / upsert method against the source. The importer
/// never writes back to ERPNext; an arch-test asserts no member of this
/// interface (or any <c>*/Extraction/</c> type) performs a source write
/// (C-CLEANROOM). v1's sole implementation
/// (<see cref="MariaDbDumpSourceReader"/>) reads a static dump file offline with
/// no network/live-Frappe connection (C-CLEANROOM (d)).
/// </para>
/// <para>
/// <b>Access-mode-agnostic (C-MODE).</b> v1 ships exactly one implementation
/// (<see cref="SourceAccessMode.MariaDbDump"/>); REST/CSV are deferred future
/// modes behind this unchanged seam. A consumer cannot tell which mode produced
/// the rows except via <see cref="Mode"/> (the C6 provenance forward-hook).
/// </para>
/// </remarks>
public interface ISourceReader
{
    /// <summary>
    /// The access mode this reader implements — recorded by the orchestrator for
    /// migration-report provenance (ADR 0100 C6). v1's sole value is
    /// <see cref="SourceAccessMode.MariaDbDump"/>.
    /// </summary>
    SourceAccessMode Mode { get; }

    /// <summary>
    /// The ERPNext DocTypes this source actually contains (the subset of the v1
    /// mapping present in the supplied dump). Used by the orchestrator to skip
    /// absent DocTypes and to drive the per-DocType census (C-CENSUS).
    /// e.g. "Account", "Customer", "Sales Invoice".
    /// </summary>
    IReadOnlyCollection<string> AvailableDocTypes { get; }

    /// <summary>
    /// Streams every source row of <paramref name="docType"/> as access-mode-agnostic
    /// <see cref="SourceRow"/>s. Streaming (not a materialized list) keeps the
    /// memory profile flat across CIC's ~10K-record portfolio.
    /// </summary>
    /// <param name="docType">The ERPNext DocType to read (e.g. "Account").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of rows; empty if the DocType is absent.</returns>
    IAsyncEnumerable<SourceRow> ReadDocTypeAsync(string docType, CancellationToken ct = default);

    /// <summary>
    /// Counts the source rows of <paramref name="docType"/> WITHOUT materializing them —
    /// the source-record count the per-DocType census (C-CENSUS) conserves against.
    /// </summary>
    /// <param name="docType">The ERPNext DocType to count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows of the DocType present in the source (0 if absent).</returns>
    System.Threading.Tasks.Task<int> CountDocTypeAsync(string docType, CancellationToken ct = default);
}
