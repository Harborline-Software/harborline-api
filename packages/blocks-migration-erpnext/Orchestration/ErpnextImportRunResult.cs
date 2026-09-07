using Harborline.Api.Blocks.Migration.Erpnext.Reporting;

namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>Which terminal branch the run took at the commit gate.</summary>
public enum ImportRunStatus
{
    /// <summary>All passes succeeded, no halt condition fired, and the unit of work was committed — the import is durable.</summary>
    Committed,

    /// <summary>A halt condition fired (or this was a dry run) and the unit of work was rolled back — nothing was committed.</summary>
    RolledBack,
}

/// <summary>
/// Why a run rolled back, for the report + the CLI exit-code mapping. <see cref="None"/> pairs with
/// <see cref="ImportRunStatus.Committed"/>; every other value pairs with
/// <see cref="ImportRunStatus.RolledBack"/>.
/// </summary>
public enum ImportHaltReason
{
    /// <summary>No halt — the run committed.</summary>
    None,

    /// <summary>The run was a <c>--dry-run</c>: all passes executed, then the run rolled back by design so nothing is made durable.</summary>
    DryRun,

    /// <summary>Pass 6 found the trial balance did not net to zero; the run rolled back (spec §4.6).</summary>
    TrialBalanceMismatch,

    /// <summary>Pass 6 found AR/AP aging diffs over threshold and <c>--allow-aging-drift</c> was not set; the run rolled back.</summary>
    AgingReconciliationFailed,

    /// <summary>Total rejects across all passes exceeded <c>--reject-threshold</c>; the run rolled back.</summary>
    RejectThresholdExceeded,
}

/// <summary>
/// The outcome of one orchestrated import run: which terminal branch it took, why, the fully
/// populated report aggregate, and where (if anywhere) the rendered <c>migration-report.md</c> was
/// written. The orchestrator always returns a complete <see cref="Report"/> — even on rollback —
/// because the report is the user's inspection artifact (spec §4.6 step 6).
/// </summary>
/// <param name="Status">Committed vs rolled back.</param>
/// <param name="HaltReason">Why it rolled back (<see cref="ImportHaltReason.None"/> when committed).</param>
/// <param name="Report">The complete report aggregate, populated across all six passes.</param>
/// <param name="ReportPath">Where the rendered markdown was written, or <see langword="null"/> when the caller asked the orchestrator not to write it (it can render <see cref="Report"/> itself).</param>
public sealed record ErpnextImportRunResult(
    ImportRunStatus Status,
    ImportHaltReason HaltReason,
    MigrationReportInput Report,
    string? ReportPath);
