using System.Globalization;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  D5 + D7 — the externalized, effective-dated decision table (the rules layer).
//
//  ADR 0135 D5: "decisions/calculations are externalized as effective-dated
//  decision tables (DMN-style) that workflows, UI, and reports all consult. The
//  process orchestrates; rules decide; capabilities enforce invariants."
//
//  ADR 0135 D7: "Decision-table evaluation pins as-of the step's business time and
//  records which version/row fired." A locked instance always re-resolves replay
//  against the pinned version — a replayed step must be deterministic, so it cannot
//  re-resolve against a newer table.
//
//  This is the MINIMAL v1 decision table the two named processes need: the
//  invoice-approval threshold (`> $5k` → human approval; else auto-post). It is
//  effective-dated (a version list ordered by EffectiveFrom) so a future rule change
//  (e.g. the threshold rises to $10k on a date) adds a NEW version without mutating
//  the old one, and an in-flight instance pinned to the old version stays on it.
//
//  Lives in blocks-workflow (no financial dependency) — it operates on a plain
//  decimal amount + a business-time DateTimeOffset, so it composes with any caller.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One outcome a decision row can yield. The handler maps the typed outcome onto a
/// <see cref="WorkflowStepOutcome"/> — auto-execute (no human) vs require a human-task.
/// </summary>
public enum ApprovalDecision
{
    /// <summary>The step may execute autonomously — an AP (auto-process) step, no human gate.</summary>
    AutoApprove = 0,

    /// <summary>The step requires a human approval before it may execute — a CP (controlled-process) gate.</summary>
    RequireApproval = 1,
}

/// <summary>
/// One row of a threshold decision table: amounts <c>&gt;= MinAmountInclusive</c> (and below the next row's
/// floor) yield <see cref="Decision"/>. <see cref="RowId"/> is the stable identifier recorded as "the row
/// that fired" (ADR 0135 D7 / the FE-1 basis payload).
/// </summary>
/// <param name="RowId">Stable, human-legible row identifier (e.g. <c>"under-5k"</c>, <c>"over-5k"</c>).</param>
/// <param name="MinAmountInclusive">The inclusive lower bound this row matches.</param>
/// <param name="Decision">The decision this row yields.</param>
public readonly record struct ThresholdDecisionRow(string RowId, decimal MinAmountInclusive, ApprovalDecision Decision);

/// <summary>
/// One effective-dated VERSION of the threshold table — a version string + the rows it contains, valid from
/// <see cref="EffectiveFrom"/> until superseded by a later version. ADR 0135 D7: an in-flight instance pins
/// THIS version; replay re-resolves against it (deterministic), never against a newer one.
/// </summary>
public sealed class ThresholdDecisionTableVersion
{
    /// <summary>The pinned version identifier (e.g. <c>"2026-06-23.1"</c>). Recorded on the instance (D7).</summary>
    public required string Version { get; init; }

    /// <summary>The instant this version takes effect (business-time, UTC). Versions sort by this.</summary>
    public required DateTimeOffset EffectiveFrom { get; init; }

    /// <summary>The rows, in any order (evaluation sorts by floor descending and takes the first match).</summary>
    public required IReadOnlyList<ThresholdDecisionRow> Rows { get; init; }

    /// <summary>
    /// Evaluates <paramref name="amount"/> against this version's rows: the highest-floor row whose
    /// <see cref="ThresholdDecisionRow.MinAmountInclusive"/> the amount meets fires. Returns the typed
    /// decision + the firing row id + this version string — the full D7 / FE-1 provenance.
    /// </summary>
    public ThresholdDecisionResult Evaluate(decimal amount)
    {
        ThresholdDecisionRow? match = null;
        foreach (var row in Rows)
        {
            if (amount >= row.MinAmountInclusive &&
                (match is null || row.MinAmountInclusive > match.Value.MinAmountInclusive))
            {
                match = row;
            }
        }

        if (match is null)
        {
            throw new InvalidOperationException(
                $"Threshold decision table version '{Version}' has no row matching amount {amount.ToString(CultureInfo.InvariantCulture)} " +
                "(every table must carry a floor-0 catch-all row).");
        }

        return new ThresholdDecisionResult(Version, match.Value.RowId, match.Value.Decision, amount);
    }
}

/// <summary>
/// The provenance of one decision-table evaluation — the version that was consulted, the row that fired, the
/// decision, and the amount it was evaluated against. This IS the D7 record + the FE-1 basis (the rule/row +
/// version that fired) the CP human-task must carry.
/// </summary>
/// <param name="Version">The pinned table version that fired (ADR 0135 D7).</param>
/// <param name="RowId">The row that matched.</param>
/// <param name="Decision">The decision the row yielded.</param>
/// <param name="EvaluatedAmount">The amount the table was evaluated against.</param>
public readonly record struct ThresholdDecisionResult(
    string Version,
    string RowId,
    ApprovalDecision Decision,
    decimal EvaluatedAmount);

/// <summary>
/// An effective-dated threshold decision table (D5/D7). Holds an ordered set of versions; resolving "the
/// version effective as-of a business time" picks the latest version whose <see cref="ThresholdDecisionTableVersion.EffectiveFrom"/>
/// is at or before that time. A pinned instance always evaluates against its pinned version (D7), never the
/// as-of-now version — <see cref="ResolveVersion"/> is used ONLY at instantiation to choose what to pin.
/// </summary>
public sealed class ThresholdDecisionTable
{
    private readonly IReadOnlyList<ThresholdDecisionTableVersion> _versionsNewestFirst;

    /// <summary>Constructs a table from its versions (any order; stored sorted newest-effective-first).</summary>
    /// <exception cref="ArgumentException">If <paramref name="versions"/> is null/empty.</exception>
    public ThresholdDecisionTable(IEnumerable<ThresholdDecisionTableVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _versionsNewestFirst = versions
            .OrderByDescending(v => v.EffectiveFrom)
            .ToList();
        if (_versionsNewestFirst.Count == 0)
        {
            throw new ArgumentException("A decision table must carry at least one version.", nameof(versions));
        }
    }

    /// <summary>
    /// Resolves the version effective as-of <paramref name="businessTime"/> (the latest version whose
    /// <see cref="ThresholdDecisionTableVersion.EffectiveFrom"/> is &lt;= the time). Used at INSTANTIATION
    /// to choose the version to pin onto the instance (ADR 0135 D7). Replay never calls this — it evaluates
    /// the already-pinned version via <see cref="EvaluatePinned"/>.
    /// </summary>
    public ThresholdDecisionTableVersion ResolveVersion(DateTimeOffset businessTime)
    {
        foreach (var v in _versionsNewestFirst)
        {
            if (v.EffectiveFrom <= businessTime)
            {
                return v;
            }
        }

        throw new InvalidOperationException(
            $"No decision-table version is effective as-of business time {businessTime:O} " +
            $"(earliest version effective from {_versionsNewestFirst[^1].EffectiveFrom:O}).");
    }

    /// <summary>
    /// Evaluates <paramref name="amount"/> against the EXACT pinned <paramref name="version"/> (ADR 0135 D7 —
    /// deterministic replay resolves against the pinned version, never the as-of-now one). Throws if the
    /// pinned version is unknown to this table (a determinism break the engine must surface, not paper over).
    /// </summary>
    public ThresholdDecisionResult EvaluatePinned(string version, decimal amount)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        var pinned = _versionsNewestFirst.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Pinned decision-table version '{version}' is not present in the table — a replay cannot " +
                "deterministically re-resolve against a missing version (ADR 0135 D7).");
        return pinned.Evaluate(amount);
    }
}
