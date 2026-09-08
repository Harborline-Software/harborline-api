namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The v1 <see cref="IImportUnitOfWork"/> implementation for the in-memory
/// repository substrate the migration passes write to today.
/// </summary>
/// <remarks>
/// <para>
/// <b>No-op commit / rollback by construction.</b> Because the passes write to
/// in-memory domain repositories rather than a transactional store, there is
/// nothing to flush on commit and nothing to physically undo on rollback at this
/// layer. What this type DOES provide is the run-lifecycle CONTRACT the
/// orchestrator drives — and a truthful record of which terminal state the run
/// reached (<see cref="Committed"/> / <see cref="RolledBack"/>) so tests and the
/// progress reporter can assert the orchestrator took the right branch.
/// </para>
/// <para>
/// <b>Honest about its limitation.</b> A rolled-back in-memory run leaves the
/// already-written repository rows in place — the in-memory substrate has no
/// physical undo. This is acceptable for v1 BUILD (synthetic fixtures, fresh
/// repositories per run) and for <c>--dry-run</c> against a throwaway in-memory
/// host. The real durability guarantee arrives with the SQLite-backed
/// implementation behind the same <see cref="IImportUnitOfWork"/> seam; the
/// orchestrator needs no change when it does.
/// </para>
/// </remarks>
public sealed class InMemoryImportUnitOfWork : IImportUnitOfWork
{
    /// <summary>True once <see cref="CommitAsync"/> has been called.</summary>
    public bool Committed { get; private set; }

    /// <summary>True once <see cref="RollbackAsync"/> has been called.</summary>
    public bool RolledBack { get; private set; }

    /// <summary>True between <see cref="BeginAsync"/> and a terminal commit/rollback.</summary>
    public bool IsOpen { get; private set; }

    /// <inheritdoc />
    public Task BeginAsync(CancellationToken cancellationToken = default)
    {
        IsOpen = true;
        Committed = false;
        RolledBack = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        Committed = true;
        IsOpen = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        RolledBack = true;
        IsOpen = false;
        return Task.CompletedTask;
    }
}
