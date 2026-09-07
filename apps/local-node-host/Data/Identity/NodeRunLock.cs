using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Why <see cref="NodeRunLock.TryAcquire(string, out NodeRunLockFailure, bool)"/> could not take the lock.</summary>
public enum NodeRunLockFailure
{
    /// <summary>The lock was taken.</summary>
    None = 0,

    /// <summary>Another process holds the lock file — on a node's data directory, a running node.</summary>
    Locked,

    /// <summary>The directory exists but this caller may not write it (read-only, or another owner).</summary>
    DirectoryUnwritable,

    /// <summary>The directory does not exist, and the caller asked not to create it.</summary>
    DirectoryMissing,
}

/// <summary>
/// An exclusive OS file lock on <c>&lt;DataDirectory&gt;/node.lock</c>, held for as long as the node runs.
/// </summary>
/// <remarks>
/// <para>
/// It exists for exactly one caller: the offline administrator-recovery command, which ADR 0066 clause 8
/// requires to be "performed by the owner of the data directory <b>with the node stopped</b>". Without a
/// definitive liveness test, "the node is stopped" would be an instruction in a README rather than a
/// precondition the code enforces — and the recovery path writes administrative authority, so it must not
/// race a running node's own state gate.
/// </para>
/// <para>
/// A file lock, not a pid file: a pid file that outlives a crash refuses recovery forever, which turns the
/// safety check into the brick. An OS lock is released by the kernel when the process dies, however it dies.
/// </para>
/// <para>
/// <b>Failing to take the lock STOPS the node.</b> It used to be a warning, which quietly made the whole
/// mechanism decorative: recovery's precondition is "a running node holds this lock", and a node that
/// started without it falsifies that precondition while reporting success. The two ways it can fail are both
/// fatal on their own terms — another live node over the same data directory is the corruption case, and a
/// data directory this process cannot write is a store this process cannot use.
/// </para>
/// <para>
/// <b>Failure modes are distinguished.</b> <see cref="FileShare.None"/> conflicts with ANY handle, including
/// a virus scanner's or an indexer's, so a bare failure is not evidence a node is running. Transient sharing
/// violations are retried with backoff, and what remains is reported as one of
/// <see cref="NodeRunLockFailure"/> — a running node, an unwritable directory, and a missing directory are
/// three different operator actions and used to be one exit code.
/// </para>
/// </remarks>
public sealed class NodeRunLock : IHostedService, IDisposable
{
    /// <summary>The lock file's name inside the node's data directory.</summary>
    public const string FileName = "node.lock";

    private readonly IOptions<LocalNodeOptions> _options;
    private readonly ILogger<NodeRunLock> _logger;
    private FileStream? _handle;

    /// <summary>Constructs the run lock over the configured data directory.</summary>
    /// <param name="options">Host options carrying the data directory.</param>
    /// <param name="logger">Diagnostic sink.</param>
    public NodeRunLock(IOptions<LocalNodeOptions> options, ILogger<NodeRunLock> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The lock file path for a data directory.</summary>
    /// <param name="dataDirectory">The node's data directory.</param>
    public static string PathFor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, FileName);
    }

    /// <summary>
    /// True when no live node holds the lock for <paramref name="dataDirectory"/>. Acquires and immediately
    /// releases, so it answers about this instant only — the recovery command HOLDS the lock across its whole
    /// write instead of asking twice.
    /// </summary>
    /// <param name="dataDirectory">The node's data directory.</param>
    public static bool IsFree(string dataDirectory)
    {
        var acquired = TryAcquire(dataDirectory);
        if (acquired is null)
        {
            return false;
        }

        acquired.Dispose();
        return true;
    }

    /// <summary>
    /// Take the lock, or null when it cannot be taken. The caller owns the returned handle and must dispose
    /// it. Also the ownership probe: creating a file in the data directory is precisely the environment-derived
    /// authority ADR 0066 clause 4 names, so a caller that is not permitted to write there gets null.
    /// </summary>
    /// <param name="dataDirectory">The node's data directory.</param>
    /// <param name="createDirectory">
    /// Create the data directory when it is missing. The host does (it owns the directory); recovery does
    /// NOT — creating a directory there would build a fresh empty store beside the real one and report
    /// success against it.
    /// </param>
    public static FileStream? TryAcquire(string dataDirectory, bool createDirectory = true)
        => TryAcquire(dataDirectory, out _, createDirectory);

    /// <summary>
    /// Take the lock, reporting WHY when it cannot be taken. Transient sharing violations — a scanner, an
    /// indexer, a backup agent holding a momentary handle — are retried with backoff before being reported
    /// as a running node.
    /// </summary>
    /// <param name="dataDirectory">The node's data directory.</param>
    /// <param name="failure">The reason, or <see cref="NodeRunLockFailure.None"/> on success.</param>
    /// <param name="createDirectory">Create the data directory when it is missing.</param>
    public static FileStream? TryAcquire(
        string dataDirectory,
        out NodeRunLockFailure failure,
        bool createDirectory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        if (!createDirectory && !Directory.Exists(dataDirectory))
        {
            failure = NodeRunLockFailure.DirectoryMissing;
            return null;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (createDirectory)
                {
                    Directory.CreateDirectory(dataDirectory);
                }

                failure = NodeRunLockFailure.None;
                return new FileStream(
                    PathFor(dataDirectory),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (DirectoryNotFoundException)
            {
                failure = NodeRunLockFailure.DirectoryMissing;
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only directory, or one this account may not write. NOT a running node — and
                // reporting it as one sent the operator to stop a service that was already stopped.
                failure = NodeRunLockFailure.DirectoryUnwritable;
                return null;
            }
            catch (IOException) when (attempt < ShareRetryCount)
            {
                Thread.Sleep(ShareRetryDelayMilliseconds * (attempt + 1));
            }
            catch (IOException)
            {
                failure = NodeRunLockFailure.Locked;
                return null;
            }
        }
    }

    private const int ShareRetryCount = 4;
    private const int ShareRetryDelayMilliseconds = 50;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handle = TryAcquire(_options.Value.DataDirectory, out var failure);
        if (_handle is null)
        {
            // FATAL. A node that starts without this lock silently falsifies the offline recovery command's
            // one precondition — recovery would take the lock, conclude the node is stopped, and write
            // administrative authority underneath a live node's own state gate. The two failure modes are
            // independently fatal anyway: Locked means a second node over this data directory, and
            // DirectoryUnwritable means a store this process cannot write.
            var message =
                $"node.run_lock_unavailable ({failure}): could not take {FileName} in " +
                $"'{_options.Value.DataDirectory}'. The node will not start: without this lock the offline " +
                "administrator-recovery command cannot tell a running node from a stopped one.";
            _logger.LogCritical("{Message}", message);
            throw new InvalidOperationException(message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
