using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Foundation.Import.Census;

/// <summary>
/// The record-census-conservation primitive (ADR 0100 C2; .NET-architect
/// amendment H). Accumulates the per-record outcome counts for one pass (or a
/// whole run) and asserts the conservation invariant:
/// <c>count(Inserted) + count(Updated) + count(Skipped) + count(Rejected) +
/// count(Halted) == count(source records)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every source record has <b>exactly three legitimate exits</b>: a happy-path
/// arm (<see cref="ImportOutcome{T}.Inserted"/>/<see cref="ImportOutcome{T}.Updated"/>/<see cref="ImportOutcome{T}.Skipped"/>),
/// the <see cref="ImportOutcome{T}.Rejected"/> arm, or a pass-aborting halt
/// (recorded via <see cref="RecordHalted"/>). There is NO fourth exit — no
/// return-null, no swallow, no continue-without-recording. This counter is the
/// positive target the A-units and test-eng prove against so that
/// <b>no financial record vanishes without a corresponding report line</b>
/// (the orchestration-level expression of C5).
/// </para>
/// <para>
/// The census is itself content-free: it stores only counts, never the records
/// it counted — consistent with C9 (no PII / no financial-record contents in
/// the audit surface).
/// </para>
/// <para>This type is mutable and single-threaded; one instance per pass.</para>
/// </remarks>
public sealed class ImportCensus
{
    private int _inserted;
    private int _updated;
    private int _skipped;
    private int _rejected;
    private int _halted;

    /// <summary>Count of <see cref="ImportOutcome{T}.Inserted"/> outcomes recorded.</summary>
    public int Inserted => _inserted;

    /// <summary>Count of <see cref="ImportOutcome{T}.Updated"/> outcomes recorded.</summary>
    public int Updated => _updated;

    /// <summary>Count of <see cref="ImportOutcome{T}.Skipped"/> outcomes recorded.</summary>
    public int Skipped => _skipped;

    /// <summary>Count of <see cref="ImportOutcome{T}.Rejected"/> outcomes recorded.</summary>
    public int Rejected => _rejected;

    /// <summary>Count of records that triggered a pass-aborting halt before producing an outcome.</summary>
    public int Halted => _halted;

    /// <summary>
    /// Total source records accounted for so far —
    /// <see cref="Inserted"/> + <see cref="Updated"/> + <see cref="Skipped"/> +
    /// <see cref="Rejected"/> + <see cref="Halted"/>.
    /// </summary>
    public int Accounted => _inserted + _updated + _skipped + _rejected + _halted;

    /// <summary>
    /// Records the outcome of one source record by routing through the
    /// <see cref="ImportOutcome{T}"/> union exhaustively. This is the canonical
    /// recording path — it is structurally impossible to record an outcome
    /// without classifying it into exactly one census bucket.
    /// </summary>
    /// <typeparam name="T">The local domain record type carried by the outcome.</typeparam>
    /// <param name="outcome">The per-record outcome produced by an upserter.</param>
    public void Record<T>(ImportOutcome<T> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome)
        {
            case ImportOutcome<T>.Inserted:
                _inserted++;
                break;
            case ImportOutcome<T>.Updated:
                _updated++;
                break;
            case ImportOutcome<T>.Skipped:
                _skipped++;
                break;
            case ImportOutcome<T>.Rejected:
                _rejected++;
                break;
            default:
                // The union is closed to this assembly (private protected ctor),
                // so this is unreachable; it exists to make a future arm a loud
                // failure rather than a silent uncounted exit (no 4th exit).
                throw new InvalidOperationException(
                    $"Unhandled ImportOutcome arm '{outcome.GetType().Name}'. " +
                    "Every outcome MUST be counted (ADR 0100 C2 record-census conservation).");
        }
    }

    /// <summary>
    /// Records that one source record triggered a pass-aborting halt before it
    /// produced an outcome (a transactional-pass rollback, or a C5 hard-halt
    /// such as <c>UnknownAccountType</c>). Counts toward the conservation total.
    /// </summary>
    public void RecordHalted() => _halted++;

    /// <summary>
    /// Verifies the conservation invariant against the known source-record count.
    /// </summary>
    /// <param name="sourceRecordCount">The number of source records this census should account for.</param>
    /// <returns><see langword="true"/> iff <see cref="Accounted"/> equals <paramref name="sourceRecordCount"/>.</returns>
    public bool IsConserved(int sourceRecordCount) => Accounted == sourceRecordCount;

    /// <summary>
    /// Throws <see cref="ImportCensusViolationException"/> if the census does not
    /// conserve the source count — surfaces a vanished/double-counted record as a
    /// loud failure (ADR 0100 C2/C5; "no record vanishes without a report line").
    /// </summary>
    /// <param name="sourceRecordCount">The number of source records this census should account for.</param>
    public void AssertConserved(int sourceRecordCount)
    {
        if (!IsConserved(sourceRecordCount))
        {
            throw new ImportCensusViolationException(sourceRecordCount, Accounted);
        }
    }
}

/// <summary>
/// Thrown when an <see cref="ImportCensus"/> fails the record-census-conservation
/// invariant (ADR 0100 C2) — the count of accounted-for records does not equal
/// the source-record count, meaning a record vanished or was double-counted.
/// </summary>
public sealed class ImportCensusViolationException : Exception
{
    /// <summary>The number of source records the census should have accounted for.</summary>
    public int ExpectedSourceCount { get; }

    /// <summary>The number of records the census actually accounted for.</summary>
    public int AccountedCount { get; }

    /// <summary>Initializes the exception with the expected and actual record counts.</summary>
    public ImportCensusViolationException(int expectedSourceCount, int accountedCount)
        : base($"Import census conservation violated: {accountedCount} records accounted for, " +
               $"but {expectedSourceCount} source records were expected. A record vanished or was " +
               "double-counted (ADR 0100 C2 record-census conservation).")
    {
        ExpectedSourceCount = expectedSourceCount;
        AccountedCount = accountedCount;
    }
}
