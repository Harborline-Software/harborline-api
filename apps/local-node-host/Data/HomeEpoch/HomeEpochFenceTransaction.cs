using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// The single source of truth for an <b>atomic fence-read-through-write</b>. Opens an explicit SQLite
/// <c>BEGIN IMMEDIATE</c> transaction on any SQLite-backed <see cref="DbContext"/> — taking the connection's write
/// (RESERVED) lock <em>before</em> the fence read runs — then runs the caller's fence-read → stage →
/// <c>SaveChangesAsync</c> body and commits, all under that single held lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>BEGIN IMMEDIATE</c> and not a plain transaction (the bug this closes — md2 Finding 1).</b>
/// The previous implementation did an <c>AsNoTracking</c> epoch fence READ, staged rows, then a single
/// <c>SaveChangesAsync</c> with NO explicit transaction. EF Core wraps only the WRITE in the implicit
/// <c>SaveChanges</c> transaction; the prior read ran as its OWN SQLite autocommit statement and released
/// its SHARED lock BEFORE the write (EXCLUSIVE) lock was taken — a TOCTOU window in which a concurrent
/// <c>HomeEfHomeEpochStore.AdvanceAsync</c> promotion could commit a higher epoch, after which the stale
/// home's effect committed anyway (split-brain double-commit).
/// </para>
/// <para>
/// A plain <c>BeginTransactionAsync</c> does NOT fix it: on <c>Microsoft.Data.Sqlite</c> a default
/// transaction is <c>DEFERRED</c> — the write lock is taken lazily, on the first WRITE, so the fence read
/// (which happens first) still runs under a SHARED lock that is upgraded only later, leaving the same gap.
/// <c>SqliteConnection.BeginTransaction(IsolationLevel.Serializable, deferred: false)</c> issues
/// <c>BEGIN IMMEDIATE</c>, which reserves the write lock AT <c>BEGIN</c> — proven empirically: while this
/// transaction is open a concurrent writer on another connection gets <c>SQLITE_BUSY</c> immediately, even
/// before this transaction has issued any statement. So the fence read runs <em>under the write lock</em>:
/// a concurrent promotion either (a) committed its higher epoch BEFORE this <c>BEGIN IMMEDIATE</c> — the
/// fence read sees it and rejects the stale write — or (b) is serialized AFTER this transaction's commit /
/// rollback. There is no interleaving in which a stale read commits against an epoch a concurrent promotion
/// raced in. The read-through-write is genuinely atomic.
/// </para>
/// <para>
/// <b>Single source for fenced sites (no A4 drift).</b> <c>NodeEfJournalStore.SaveAtomicAsync</c> (the
/// JE-post site, through its kernel-boundary port and <see cref="BeginAsync"/>),
/// <c>NodeEfInvoiceNumberingService.NextNumberAsync</c> (the Gap-2b sequence-allocation site),
/// and <c>NodeEfGrantStore</c> mutations run their fenced unit-of-work through this one helper, so the
/// IMMEDIATE-locked read-through-write semantics are identical at all sites. The G-4 arch-test asserts every
/// site routes through here.
/// </para>
/// </remarks>
public static class HomeEpochFenceTransaction
{
    /// <summary>
    /// Runs <paramref name="body"/> inside an explicit <c>BEGIN IMMEDIATE</c> transaction on
    /// <paramref name="ctx"/> and commits. The write (RESERVED) lock is held for the WHOLE duration —
    /// across the invariant-bearing fence read the body is expected to perform, the rows it stages, and the
    /// <c>SaveChangesAsync</c> it issues — so the fence read and the effect commit are atomic. Any exception
    /// thrown by <paramref name="body"/> (notably a stale-authority refusal) rolls the whole
    /// transaction back; nothing persists.
    /// </summary>
    /// <param name="ctx">The in-flight SQLite-backed context the effect is staged on.</param>
    /// <param name="body">
    /// The fenced unit-of-work: it MUST perform its invariant-bearing fence read and the effect's
    /// <c>SaveChangesAsync</c> on <paramref name="ctx"/>. It MUST NOT open its own transaction or commit —
    /// this helper owns the transaction lifecycle.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RunAsync(
        DbContext ctx,
        Func<Task> body,
        CancellationToken ct = default)
    {
        await RunAsync<object?>(ctx, async () =>
        {
            await body().ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a value-producing body under the same single BEGIN IMMEDIATE fence.</summary>
    public static async Task<T> RunAsync<T>(
        DbContext ctx,
        Func<Task<T>> body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await using var fence = await BeginAsync(ctx, ct).ConfigureAwait(false);
        try
        {
            // The fence read + the effect's SaveChangesAsync — both run here, under the held write lock.
            var result = await body().ConfigureAwait(false);
            await fence.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            // A stale-epoch rejection (or any failure) rolls the whole read-through-write back: nothing persists.
            await fence.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens the same single <c>BEGIN IMMEDIATE</c> fence for a caller that owns the commit point itself (the
    /// journal store's Platform <c>KernelTransactionBoundary</c> port). Disposing an uncommitted fence rolls it back.
    /// </summary>
    public static async Task<Held> BeginAsync(DbContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Take the underlying ADO connection and open it (EF may not have opened it yet). We begin the
        // transaction directly on the SqliteConnection so we can pass deferred:false (= BEGIN IMMEDIATE);
        // EF's ctx.Database.BeginTransactionAsync() offers no deferred flag and would issue a DEFERRED
        // begin, which does NOT take the write lock before the read (see remarks).
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await ctx.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }

        // deferred:false ⇒ BEGIN IMMEDIATE ⇒ the write/RESERVED lock is acquired NOW, before the fence read.
        // The explicit (IsolationLevel, deferred) overload is used deliberately rather than EF's
        // BeginTransactionAsync() or the parameterless ADO begin: the documented default for a plain SQLite
        // begin is DEFERRED (write lock taken lazily on first write — too late, the fence read precedes it).
        // Passing deferred:false makes the IMMEDIATE intent explicit and version-independent.
        SqliteTransaction? sqliteTx = null;
        try
        {
            sqliteTx = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

            // Enlist EF onto this same ADO transaction so the body's SaveChangesAsync commits inside it (rather
            // than EF opening its own implicit transaction on a fresh write lock).
            await ctx.Database.UseTransactionAsync(sqliteTx, ct).ConfigureAwait(false);
            return new Held(connection, sqliteTx, openedHere);
        }
        catch
        {
            if (sqliteTx is not null) await sqliteTx.DisposeAsync().ConfigureAwait(false);
            if (openedHere) await connection.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>An open <c>BEGIN IMMEDIATE</c> fence: the write lock is held until commit, rollback or dispose.</summary>
    public sealed class Held : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly bool _openedHere;

        internal Held(SqliteConnection connection, SqliteTransaction transaction, bool openedHere)
        {
            _connection = connection;
            _transaction = transaction;
            _openedHere = openedHere;
        }

        /// <summary>Commits everything saved under the fence.</summary>
        public Task CommitAsync(CancellationToken ct = default) => _transaction.CommitAsync(ct);

        /// <summary>Best-effort rollback: if the connection already faulted, dispose finalizes it.</summary>
        public async Task RollbackAsync(CancellationToken ct = default)
        {
            try { await _transaction.RollbackAsync(ct).ConfigureAwait(false); } catch { /* already rolled back */ }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            if (_openedHere && _connection.State == ConnectionState.Open)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
