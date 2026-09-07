namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The outer transaction boundary the A7 orchestrator drives around a whole
/// import run (ADR 0100 §4.5/§4.6 "single SQLite transaction"; importer spec
/// §8 <c>--dry-run</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an ABSTRACTION SEAM, not a literal SQL transaction (yet).</b> The
/// migration passes write to in-memory domain repositories today — there is no
/// SQLite / <c>DbContext</c> / persistence layer beneath them. Building that
/// substrate is a separate effort (its own ADR). Rather than couple the
/// orchestrator's run lifecycle to a persistence layer that does not exist, the
/// orchestrator expresses its commit/rollback contract against THIS seam:
/// </para>
/// <list type="bullet">
///   <item><see cref="BeginAsync"/> opens the run scope.</item>
///   <item><see cref="CommitAsync"/> makes the run's writes durable (a real impl
///         commits the SQLite transaction; the in-memory impl is a no-op because
///         the writes already landed in the repositories).</item>
///   <item><see cref="RollbackAsync"/> discards the run's writes on a halting
///         outcome (<c>TrialBalanceMismatch</c>, over-threshold rejects, or
///         <c>--dry-run</c>).</item>
/// </list>
/// <para>
/// <b>Why a seam now.</b> The orchestrator's halt-then-roll-back behaviour is
/// correct and unit-testable today against the seam: a test asserts that a
/// trial-balance mismatch drove <see cref="RollbackAsync"/> and never
/// <see cref="CommitAsync"/>. When the SQLite persistence substrate lands, a
/// real <c>SqliteImportUnitOfWork</c> drops in behind this interface with ZERO
/// orchestrator change — the orchestrator already speaks Begin/Commit/Rollback.
/// </para>
/// <para>
/// <b>Single run scope.</b> One unit of work spans the entire Pass 1→6 run, not
/// per-pass. A halt in any pass rolls the whole run back so the user never sees a
/// half-imported book (importer spec §4.6 failure modes).
/// </para>
/// </remarks>
public interface IImportUnitOfWork
{
    /// <summary>
    /// Opens the run transaction scope. Called once, before Pass 1.
    /// </summary>
    Task BeginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the run's writes. Called once, after Pass 6 succeeds and the run
    /// is not a dry run. After this returns the import is durable.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the run's writes. Called on any halting verification outcome, an
    /// over-threshold reject halt, an unhandled fault, or a <c>--dry-run</c>
    /// invocation. Idempotent: safe to call without a prior
    /// <see cref="CommitAsync"/>.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
