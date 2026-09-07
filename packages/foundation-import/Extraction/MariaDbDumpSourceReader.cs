// ---------------------------------------------------------------------------
// CLEAN-ROOM ATTRIBUTION (ADR 0100 C4 / C-CLEANROOM (b); spec §9.5)
//
// This adapter reads the DATA FORMAT of an ERPNext / Frappe MariaDB dump only —
// the public data-interchange shape (table rows and column names). It derives NO
// code from ERPNext or Frappe: no controllers, validators, workflow logic, or
// DocType-definition JSON. ERPNext and the Frappe Framework are projects of
// Frappe Technologies Pvt. Ltd., licensed under the GNU GPLv3. This file is
// FORMAT-REFERENCE-ONLY; NO GPLv3 code is derived or copied. Harborline Software
// code is MIT-licensed (see Directory.Build.props PackageLicenseExpression).
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Import.Extraction;

/// <summary>
/// v1's SOLE extraction adapter (ADR 0100 C6): reads a STATIC MariaDB dump file
/// — the output of <c>mysqldump</c> of CIC's ERPNext instance — and projects the
/// ERPNext <c>tab&lt;DocType&gt;</c> table rows into access-mode-agnostic
/// <see cref="SourceRow"/>s for the upsert passes.
/// </summary>
/// <remarks>
/// <para>
/// <b>READ-ONLY, OFFLINE, CLEAN-ROOM (ADR 0100 C4).</b> This adapter reads a
/// static <c>.sql</c> text file with BCL file I/O only. It:
/// <list type="bullet">
///   <item>opens the dump file <b>read-only</b> (<see cref="FileAccess.Read"/>);</item>
///   <item>NEVER writes / updates / deletes against the source (no write method exists);</item>
///   <item>NEVER opens a network connection, an HTTP/REST client, or a live MariaDB / Frappe
///         connection — it does not execute SQL, it <b>parses</b> the dump's <c>INSERT</c>
///         statements (C-CLEANROOM (d): no live-Frappe connection surface in v1);</item>
///   <item>reads ERPNext's DATA format only (table rows), never Frappe controllers / validators /
///         workflow code / DocType-definition JSON (spec §9.1; clean-room license posture).</item>
/// </list>
/// Because there is no DB connection and no network, the C4 read-only posture is
/// <b>uniformly provable</b> — the reason ADR 0100 C6 collapsed v1 to dump-only.
/// </para>
/// <para>
/// <b>Credential handling (C9).</b> The dump FILE itself carries no live
/// credential; the one-time <c>mysqldump</c> credential is a CIC-side concern
/// consumed at export time, never by this adapter. This adapter accepts only a
/// filesystem path and never echoes any secret.
/// </para>
/// <para>
/// <b>DocType ↔ table mapping.</b> ERPNext stores each DocType in a table named
/// <c>tab&lt;DocType&gt;</c> (e.g. DocType "Account" → table <c>tabAccount</c>,
/// "Sales Invoice" → <c>tabSales Invoice</c>). This adapter strips the <c>tab</c>
/// prefix to recover the DocType name. Column order is taken from the table's
/// <c>CREATE TABLE</c> block; row values from its <c>INSERT INTO ... VALUES</c>
/// statements.
/// </para>
/// </remarks>
public sealed class MariaDbDumpSourceReader : ISourceReader
{
    private readonly IReadOnlyDictionary<string, DumpTable> _tablesByDocType;

    private MariaDbDumpSourceReader(IReadOnlyDictionary<string, DumpTable> tablesByDocType)
    {
        _tablesByDocType = tablesByDocType;
    }

    /// <inheritdoc />
    public SourceAccessMode Mode => SourceAccessMode.MariaDbDump;

    /// <inheritdoc />
    public IReadOnlyCollection<string> AvailableDocTypes => _tablesByDocType.Keys.ToArray();

    /// <summary>
    /// Loads a MariaDB dump from a filesystem path, parsing its
    /// <c>tab&lt;DocType&gt;</c> tables into an in-memory index ready for streaming.
    /// Opens the file READ-ONLY; never connects to a database (C4 / C-CLEANROOM).
    /// </summary>
    /// <param name="dumpFilePath">Absolute path to the static <c>mysqldump</c> <c>.sql</c> file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A reader over the dump's DocType tables.</returns>
    /// <exception cref="FileNotFoundException">The dump file does not exist.</exception>
    public static async Task<MariaDbDumpSourceReader> LoadAsync(string dumpFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFilePath);
        if (!File.Exists(dumpFilePath))
        {
            throw new FileNotFoundException("MariaDB dump file not found.", dumpFilePath);
        }

        // READ-ONLY open — explicit FileAccess.Read; no write/append/create.
        await using var stream = new FileStream(
            dumpFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        var tables = MariaDbDumpParser.Parse(sql);
        return new MariaDbDumpSourceReader(tables);
    }

    /// <summary>
    /// Builds a reader directly from already-parsed dump SQL text (offline; no file).
    /// Useful for tests with an in-memory fixture dump.
    /// </summary>
    /// <param name="dumpSql">The dump SQL text.</param>
    /// <returns>A reader over the parsed DocType tables.</returns>
    public static MariaDbDumpSourceReader FromSql(string dumpSql)
    {
        ArgumentNullException.ThrowIfNull(dumpSql);
        return new MariaDbDumpSourceReader(MariaDbDumpParser.Parse(dumpSql));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceRow> ReadDocTypeAsync(
        string docType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);

        if (!_tablesByDocType.TryGetValue(docType, out var table))
        {
            yield break;
        }

        foreach (var row in table.Rows)
        {
            ct.ThrowIfCancellationRequested();

            var columns = new Dictionary<string, string?>(table.Columns.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < table.Columns.Count && i < row.Count; i++)
            {
                columns[table.Columns[i]] = row[i];
            }

            yield return new SourceRow(docType, columns);

            // Cooperative async yield point so a very large dump streams without
            // blocking the scheduler; the parse is synchronous, the stream is not.
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public Task<int> CountDocTypeAsync(string docType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);
        var count = _tablesByDocType.TryGetValue(docType, out var table) ? table.Rows.Count : 0;
        return Task.FromResult(count);
    }
}
