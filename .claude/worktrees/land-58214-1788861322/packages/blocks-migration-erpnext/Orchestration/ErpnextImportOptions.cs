namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The run-shaping options the <c>anchor import erpnext</c> CLI exposes (importer spec §8.1),
/// in a form the orchestrator consumes directly. One option per CLI flag.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="DryRun"/> always rolls back.</b> A dry run executes all six passes (Pass 6 is
/// read-only verification) so the user gets a complete <c>migration-report.md</c> to inspect, then
/// the orchestrator drives <see cref="IImportUnitOfWork.RollbackAsync"/> at the commit gate instead
/// of <see cref="IImportUnitOfWork.CommitAsync"/> — nothing is made durable.
/// </para>
/// <para>
/// <b><see cref="FromPass"/> resume.</b> Honored by skipping passes &lt; N. Operationally meaningful
/// only against a persistent store that carries prior-run state across processes; against the v1
/// in-memory substrate it is testable by pre-seeding the repositories. Real cross-process resume
/// arrives with the SQLite substrate (same deferral family as the <see cref="IImportUnitOfWork"/>
/// seam).
/// </para>
/// <para>
/// <b><see cref="RejectThreshold"/>.</b> When set, the run halts-and-rolls-back at the commit gate if
/// the total reject count across all passes exceeds N. <see langword="null"/> = unlimited (spec default).
/// </para>
/// </remarks>
/// <param name="DryRun">Run all passes then roll back; produce the report but commit nothing (<c>--dry-run</c>).</param>
/// <param name="AllowAgingDrift">Accept Pass 6 AR/AP aging diffs over threshold without halting; the diff is still reported (<c>--allow-aging-drift</c>).</param>
/// <param name="AllowMultiCurrencySkip">Skip rather than reject transactional records whose currency ≠ the chart base currency (<c>--allow-multi-currency-skip</c>).</param>
/// <param name="FromPass">Resume from pass N (1..6); passes &lt; N are skipped (<c>--from-pass</c>). Default 1.</param>
/// <param name="RejectThreshold">Halt the run if total rejects exceed this count; <see langword="null"/> = unlimited (<c>--reject-threshold</c>).</param>
/// <param name="Verbose">Verbose per-record progress to stderr rather than one line per pass (<c>--verbose</c>).</param>
public sealed record ErpnextImportOptions(
    bool DryRun,
    bool AllowAgingDrift,
    bool AllowMultiCurrencySkip,
    int FromPass,
    int? RejectThreshold,
    bool Verbose)
{
    /// <summary>The spec-default option set: commit for real, halt on aging drift, reject on currency mismatch, start at Pass 1, unlimited rejects, progress-only logging.</summary>
    public static ErpnextImportOptions Default { get; } = new(
        DryRun: false,
        AllowAgingDrift: false,
        AllowMultiCurrencySkip: false,
        FromPass: 1,
        RejectThreshold: null,
        Verbose: false);
}
