namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The progress-reporting seam the orchestrator drives as it runs Pass 1→6, mapped to the
/// terminal UX in importer spec §8.2. Keeping progress behind an interface lets the orchestrator
/// stay free of <see cref="System.Console"/> / TTY concerns: the CLI host supplies a TTY-repaint
/// implementation (with a non-TTY periodic-line-rewrite fallback), and tests supply
/// <see cref="NullImportProgress"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a log; still allowlist-clean.</b> This drives the on-screen run UX, not the structured
/// audit log. The orchestrator only ever passes ADR 0100 C9-safe descriptors — pass labels, opaque
/// counts, and DocType names — never PII, monetary amounts, account values, or raw record contents.
/// The <see cref="Verbose"/> channel carries per-record <i>outcome</i> lines (DocType + opaque
/// externalRef + action), held to the same allowlist as the redacted log.
/// </para>
/// <para>
/// Every method is fire-and-forget from the orchestrator's perspective: implementations must not
/// throw (a broken progress writer must never fail the import) and must be cheap (called per pass /
/// per sub-pass / optionally per record under <c>--verbose</c>).
/// </para>
/// </remarks>
public interface IImportProgress
{
    /// <summary>Print the run header: the source descriptor and the destination chart descriptor (spec §8.2 top).</summary>
    /// <param name="sourceLabel">A path or opaque label for the export source.</param>
    /// <param name="targetLabel">A human-readable label for the destination chart(s).</param>
    void RunStarting(string sourceLabel, string targetLabel);

    /// <summary>Mark a top-level step's start (e.g. <c>"Manifest validation"</c>, <c>"Pass 1 (Chart of accounts)"</c>) — opens its status line.</summary>
    /// <param name="stepLabel">The step label as it should appear at the head of the line.</param>
    void StepStarting(string stepLabel);

    /// <summary>Report in-progress completion of the current step for the in-place percentage bar (TTY repaint; periodic rewrite on non-TTY).</summary>
    /// <param name="stepLabel">The step the progress applies to.</param>
    /// <param name="completed">Records processed so far.</param>
    /// <param name="total">Total records expected; <c>0</c> when not countable (no bar drawn).</param>
    void StepProgress(string stepLabel, int completed, int total);

    /// <summary>Close a top-level step's status line with its terminal result (e.g. <c>"OK (1,247 accounts)"</c>).</summary>
    /// <param name="stepLabel">The step being closed.</param>
    /// <param name="resultSummary">A PII-free, amount-free completion descriptor.</param>
    void StepCompleted(string stepLabel, string resultSummary);

    /// <summary>Emit an indented sub-pass result line (e.g. <c>"2.1 Fiscal years + periods....... OK (5 FYs; 60 periods)"</c>).</summary>
    /// <param name="subStepLabel">The indented sub-step label.</param>
    /// <param name="resultSummary">A PII-free, amount-free completion descriptor.</param>
    void SubStepCompleted(string subStepLabel, string resultSummary);

    /// <summary>Emit a verbose per-record outcome line; only called when <c>--verbose</c> is set. Must stay allowlist-clean (DocType + opaque externalRef + action).</summary>
    /// <param name="line">A pre-redacted, allowlist-clean outcome descriptor.</param>
    void Verbose(string line);

    /// <summary>Print the final footer: where the run summary report was written (spec §8.2 bottom).</summary>
    /// <param name="reportPath">The path the <c>migration-report.md</c> was written to.</param>
    void RunFinished(string reportPath);
}

/// <summary>
/// A no-op <see cref="IImportProgress"/> for callers that don't render progress (tests, dry-run
/// harnesses, the desktop wizard before its own progress surface lands). Every method is a no-op,
/// so the orchestrator's progress calls are free.
/// </summary>
public sealed class NullImportProgress : IImportProgress
{
    /// <summary>The shared singleton — the no-op writer is stateless.</summary>
    public static NullImportProgress Instance { get; } = new();

    private NullImportProgress() { }

    /// <inheritdoc />
    public void RunStarting(string sourceLabel, string targetLabel) { }

    /// <inheritdoc />
    public void StepStarting(string stepLabel) { }

    /// <inheritdoc />
    public void StepProgress(string stepLabel, int completed, int total) { }

    /// <inheritdoc />
    public void StepCompleted(string stepLabel, string resultSummary) { }

    /// <inheritdoc />
    public void SubStepCompleted(string subStepLabel, string resultSummary) { }

    /// <inheritdoc />
    public void Verbose(string line) { }

    /// <inheritdoc />
    public void RunFinished(string reportPath) { }
}
