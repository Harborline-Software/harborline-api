using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Session;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>The limiter's answer for one incoming login attempt.</summary>
/// <param name="IsAllowed">Whether the attempt may proceed to credential verification.</param>
/// <param name="RetryAfter">When refused by an active lockout, the remaining lockout time.</param>
public readonly record struct WebLoginAttemptDecision(bool IsAllowed, TimeSpan? RetryAfter);

/// <summary>
/// The web-client login rate limiter / lockout (ADR 0099 S11 — the consumer of the
/// <c>Auth.LoginFailed</c> signal). Tracks failed attempts per (username, source address) over a
/// sliding window; at the configured threshold it engages a temporary lockout with exponential
/// backoff (doubling per consecutive lockout, capped); a successful login resets the pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-process, concurrency-safe, local-first.</b> One process-wide lock serialises every
/// mutation (login is human-paced; contention is irrelevant — the same shape as
/// <c>PairingRedeemRateLimiter</c>). State is in-memory only: this is a single local node, and no
/// external service is consulted (nothing phones home). A restart forgets the counters, which is an
/// accepted bound for a local node — the Argon2id verify cost still rate-limits raw throughput.
/// </para>
/// <para>
/// <b>Fail-closed.</b> Any internal error while consulting or updating the limiter REFUSES the
/// attempt (<see cref="CheckAttempt"/> returns not-allowed) rather than admitting it; a full
/// tracking table refuses NEW keys rather than tracking unboundedly or admitting untracked.
/// </para>
/// <para>
/// <b>Non-enumerating.</b> The limiter never reveals lockout state to the caller's client: the
/// route returns the SAME 401 body for a locked-out attempt as for a wrong password. Engage and
/// release are audited server-side as <c>Auth.LoginLockout</c> / <c>Auth.LoginLockoutReleased</c>
/// (structured logging with the canonical label, exactly like the authority's other audit lines —
/// the node is not the Bridge audit sink). The password is never seen here, and the username is
/// logged best-effort exactly as <c>Auth.LoginFailed</c> already logs it.
/// </para>
/// </remarks>
public sealed class WebLoginRateLimiter
{
    // Keys are bounded so an attacker cannot inflate per-entry memory with a huge username or
    // source address. Long components are hashed in Truncate rather than losing identity suffixes.
    private const int MaxKeyComponentLength = 256;

    private readonly NodeWebLoginLockoutOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<WebLoginRateLimiter> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<LoginKey, Entry> _entries = new();
    private readonly LinkedList<LoginKey> _activityOrder = new();
    private readonly Dictionary<LoginKey, LinkedListNode<LoginKey>> _activityNodes = new();
    private DateTimeOffset? _lastPruneAt;
    private DateTimeOffset? _evictionRecheckAt;
    private bool _faulted;

    // @deviation-from-spec: ADR 0099 S11 specifies a SHA-256-prefix bucket key. This local-node
    // implementation deliberately uses a bounded credential-resolution identity so one account
    // maps to one auditable in-process lockout entry; the key never leaves this process except in
    // existing best-effort structured logging. BuildKey owns the canonical username transform so
    // every route shares one lockout bucket for one account.
    private readonly record struct LoginKey(string Username, string Source);

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();
        public Queue<DateTimeOffset> PendingAttempts { get; } = new();
        public DateTimeOffset? LockedUntil { get; set; }
        public int ConsecutiveLockouts { get; set; }
        public DateTimeOffset LastActivity { get; set; }
    }

    /// <summary>Constructs the limiter over the web-client options' lockout knobs.</summary>
    public WebLoginRateLimiter(
        IOptions<NodeWebClientOptions> options,
        TimeProvider time,
        ILogger<WebLoginRateLimiter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value?.Lockout ?? new NodeWebLoginLockoutOptions();
        _time = time;
        _logger = logger;
    }

    // Sanitized knobs — nonsense configuration hardens instead of disabling the limiter. The
    // magnitude cap also prevents extreme operator values from overflowing DateTimeOffset math.
    private static readonly TimeSpan MaxConfiguredDuration = TimeSpan.FromDays(365);

    private int MaxFailures => Math.Max(1, _options.MaxFailures);
    private TimeSpan Window => BoundedPositive(_options.Window, TimeSpan.FromMinutes(15));
    private TimeSpan BaseLockout => BoundedPositive(_options.BaseLockoutDuration, TimeSpan.FromMinutes(1));
    private TimeSpan MaxLockout
    {
        get
        {
            var configured = BoundedPositive(_options.MaxLockoutDuration, BaseLockout);
            return configured >= BaseLockout ? configured : BaseLockout;
        }
    }
    private int MaxTrackedKeys => Math.Max(1, _options.MaxTrackedKeys);
    private TimeSpan PruneInterval => BoundedPositive(_options.PruneInterval, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Gate one incoming attempt for (<paramref name="resolutionIdentity"/>, <paramref name="source"/>)
    /// BEFORE credential verification. Refuses while a lockout is active; on the first admitted
    /// attempt after a lockout expires it emits the <c>Auth.LoginLockoutReleased</c> audit. Reserves
    /// one bounded per-key attempt slot; <see cref="RecordFailure"/> finalizes that reservation and
    /// <see cref="RecordSuccess"/> releases one. Fail-closed: refuses on internal error.
    /// </summary>
    public WebLoginAttemptDecision CheckAttempt(string? resolutionIdentity, string? source)
    {
        try
        {
            var released = false;
            var tableFull = false;
            var tableCount = 0;
            WebLoginAttemptDecision decision;
            lock (_gate)
            {
                if (_faulted)
                {
                    return new WebLoginAttemptDecision(false, null);
                }

                var now = _time.GetUtcNow();
                var key = BuildKey(resolutionIdentity, source);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    if (_entries.Count >= MaxTrackedKeys)
                    {
                        PruneStaleIfDue(now);
                    }
                    if (_entries.Count >= MaxTrackedKeys && !TryEvictLeastRecentlyActive(now))
                    {
                        // Every tracked entry is protected by a live lockout or pending work. Do
                        // not admit an untracked attempt: its eventual result could become
                        // unaccounted-for and reopen the gate.
                        tableFull = true;
                        tableCount = _entries.Count;
                        decision = new WebLoginAttemptDecision(false, null);
                    }
                    else
                    {
                        entry = new Entry { LastActivity = now };
                        entry.PendingAttempts.Enqueue(now);
                        AddEntry(key, entry);
                        decision = new WebLoginAttemptDecision(true, null);
                    }
                }
                else
                {
                    entry.LastActivity = now;
                    TouchEntry(key);
                    PruneExpiredAttempts(entry, now);
                    if (entry.LockedUntil is { } until && until > now)
                    {
                        decision = new WebLoginAttemptDecision(false, until - now);
                    }
                    else
                    {
                        if (entry.LockedUntil is not null)
                        {
                            // Lockout expired — release it (attempts are admitted again). The
                            // consecutive-lockout count survives so a re-offender backs off
                            // exponentially; only a SUCCESSFUL login resets it.
                            entry.LockedUntil = null;
                            released = true;
                        }

                        var admitted = entry.Failures.Count + entry.PendingAttempts.Count;
                        if (admitted >= MaxFailures)
                        {
                            // The threshold attempt may still be inside Argon2id verification. Do
                            // not admit another one while it is pending; its eventual failure will
                            // engage the lockout, while a success releases its reservation.
                            decision = new WebLoginAttemptDecision(false, null);
                        }
                        else
                        {
                            entry.PendingAttempts.Enqueue(now);
                            decision = new WebLoginAttemptDecision(true, null);
                        }
                    }
                }
            }

            if (tableFull)
            {
                LogTableFull(tableCount, refusingUntrackedAttempt: true);
            }
            if (released)
            {
                LogLockoutReleased(resolutionIdentity, source);
            }
            return decision;
        }
        catch (Exception ex)
        {
            // FAIL CLOSED — a broken limiter must never become an open gate.
            FaultClosed(ex, "gating an attempt");
            return new WebLoginAttemptDecision(false, null);
        }
    }

    /// <summary>
    /// Record one credential-mismatch login (the <c>Auth.LoginFailed</c> outcome) for the pair. When the sliding
    /// window reaches the threshold this ENGAGES the lockout (emitting <c>Auth.LoginLockout</c>)
    /// with exponential backoff, and clears the window so the next cycle needs a fresh run of
    /// failures. If an internal error occurs, <see cref="FaultClosed"/> latches the limiter
    /// fail-closed and all later attempts are refused until process restart.
    /// </summary>
    public void RecordFailure(string? resolutionIdentity, string? source)
    {
        try
        {
            var engaged = false;
            var duration = default(TimeSpan);
            var lockoutCount = 0;
            var tableFull = false;
            var tableCount = 0;
            lock (_gate)
            {
                if (_faulted)
                {
                    return;
                }

                var now = _time.GetUtcNow();
                var key = BuildKey(resolutionIdentity, source);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    if (_entries.Count >= MaxTrackedKeys)
                    {
                        PruneStaleIfDue(now);
                    }
                    if (_entries.Count >= MaxTrackedKeys && !TryEvictLeastRecentlyActive(now))
                    {
                        // The attempt was already rejected on its own merits; do not grow beyond the
                        // bounded table when its reservation is missing.
                        tableFull = true;
                        tableCount = _entries.Count;
                    }
                    else
                    {
                        entry = new Entry();
                        AddEntry(key, entry);
                    }
                }

                if (!tableFull)
                {
                    entry!.LastActivity = now;
                    TouchEntry(key);
                    PruneExpiredAttempts(entry, now);
                    if (entry.PendingAttempts.Count > 0)
                    {
                        // CheckAttempt already reserved this attempt. The queue is only the
                        // bounded admission budget; completed failures live in Failures.
                        entry.PendingAttempts.Dequeue();
                    }

                    var lockoutActive = false;
                    if (entry.LockedUntil is { } until)
                    {
                        if (until > now)
                        {
                            // A concurrent attempt was already the threshold failure. Its lockout
                            // covers this in-flight failure too; do not escalate twice.
                            lockoutActive = true;
                        }
                        else
                        {
                            entry.LockedUntil = null;
                        }
                    }

                    if (!lockoutActive)
                    {
                        var cutoff = SubtractClamped(now, Window);
                        while (entry.Failures.Count > 0 && entry.Failures.Peek() <= cutoff)
                        {
                            entry.Failures.Dequeue();
                        }
                        entry.Failures.Enqueue(now);
                        if (entry.Failures.Count >= MaxFailures)
                        {
                            // Threshold crossed — engage, with exponential backoff on consecutive
                            // lockouts. Audit after releasing _gate so a throwing provider cannot
                            // poison the process-wide fail-closed latch.
                            entry.ConsecutiveLockouts++;
                            duration = LockoutDurationFor(entry.ConsecutiveLockouts);
                            entry.LockedUntil = AddClamped(now, duration);
                            entry.Failures.Clear();
                            engaged = true;
                            lockoutCount = entry.ConsecutiveLockouts;
                        }
                    }
                }
            }

            if (tableFull)
            {
                LogTableFull(tableCount, refusingUntrackedAttempt: false);
            }
            if (engaged)
            {
                LogLockoutEngaged(resolutionIdentity, source, duration, lockoutCount);
            }
        }
        catch (Exception ex)
        {
            // A failure that could not be recorded must poison the limiter. Otherwise the next
            // attempt could be admitted with an incomplete history, which is fail-open.
            FaultClosed(ex, "recording a failure");
        }
    }

    /// <summary>Reset the pair after a successful login (forgets failures, lockouts and backoff).</summary>
    public void RecordSuccess(string? resolutionIdentity, string? source)
    {
        try
        {
            lock (_gate)
            {
                if (_faulted)
                {
                    return;
                }

                var key = BuildKey(resolutionIdentity, source);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    return;
                }

                var now = _time.GetUtcNow();
                PruneExpiredAttempts(entry, now);
                entry.LastActivity = now;
                TouchEntry(key);
                if (entry.PendingAttempts.Count > 0)
                {
                    entry.PendingAttempts.Dequeue();
                }
                entry.Failures.Clear();
                entry.LockedUntil = null;
                entry.ConsecutiveLockouts = 0;
                if (entry.PendingAttempts.Count == 0)
                {
                    RemoveEntry(key);
                }
            }
        }
        catch (Exception ex)
        {
            // A reset that cannot be completed must also poison the limiter. Retaining stale state
            // is safe; admitting future attempts through a partially reset state is not.
            FaultClosed(ex, "recording a success");
        }
    }

    /// <summary>
    /// Releases an admitted attempt whose authority did not return an outcome, such as a cancelled
    /// request or a transient issuer/store fault. This only removes the pending reservation; it does
    /// not alter completed failures, lockouts, or backoff state.
    /// </summary>
    public void ReleaseAttempt(string? resolutionIdentity, string? source)
    {
        try
        {
            lock (_gate)
            {
                if (_faulted)
                {
                    return;
                }

                var key = BuildKey(resolutionIdentity, source);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    return;
                }

                var now = _time.GetUtcNow();
                PruneExpiredAttempts(entry, now);
                entry.LastActivity = now;
                TouchEntry(key);
                if (entry.PendingAttempts.Count > 0)
                {
                    entry.PendingAttempts.Dequeue();
                }

                var locked = entry.LockedUntil is { } until && until > now;
                if (!locked && entry.PendingAttempts.Count == 0 && entry.Failures.Count == 0)
                {
                    RemoveEntry(key);
                }
            }
        }
        catch (Exception ex)
        {
            // An unreleased reservation could become fail-open if the limiter then admitted beyond
            // the intended budget, so preserve the fail-closed latch on cleanup failure too.
            FaultClosed(ex, "releasing a pending attempt");
        }
    }

    private void FaultClosed(Exception exception, string operation)
    {
        lock (_gate)
        {
            _faulted = true;
        }

        // Logging is diagnostic only. A broken logger must not turn the fail-closed path into a
        // second exception or make the request surface a 500.
        try
        {
            _logger.LogError(
                exception,
                "Web-client login rate limiter failed while {Operation}; refusing all attempts fail-closed.",
                operation);
        }
        catch
        {
            // Deliberately empty: _faulted is the security decision.
        }
    }

    private TimeSpan LockoutDurationFor(int consecutiveLockouts)
    {
        var duration = BaseLockout;
        for (var i = 1; i < consecutiveLockouts; i++)
        {
            if (duration >= MaxLockout / 2)
            {
                return MaxLockout;
            }
            duration += duration;
        }
        return duration < MaxLockout ? duration : MaxLockout;
    }

    /// <summary>Drop entries with no live lockout and no failure newer than the window.</summary>
    private void PruneStale(DateTimeOffset now)
    {
        _evictionRecheckAt = null;
        var stale = new List<LoginKey>();
        foreach (var (key, entry) in _entries)
        {
            PruneExpiredAttempts(entry, now);
            var locked = entry.LockedUntil is { } until && until > now;
            if (!locked && entry.PendingAttempts.Count == 0 && entry.Failures.Count == 0)
            {
                stale.Add(key);
            }
        }
        foreach (var key in stale)
        {
            RemoveEntry(key);
        }
    }

    private void PruneStaleIfDue(DateTimeOffset now)
    {
        if (_lastPruneAt is { } lastPruneAt && now < AddClamped(lastPruneAt, PruneInterval))
        {
            return;
        }

        _lastPruneAt = now;
        PruneStale(now);
    }

    private bool TryEvictLeastRecentlyActive(DateTimeOffset now)
    {
        if (_evictionRecheckAt is { } recheckAt && now < recheckAt)
        {
            return false;
        }

        _evictionRecheckAt = null;
        DateTimeOffset? nextLockoutExpiry = null;
        for (var node = _activityOrder.First; node is not null; node = node.Next)
        {
            var key = node.Value;
            var entry = _entries[key];
            if (entry.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                if (nextLockoutExpiry is null || lockedUntil < nextLockoutExpiry)
                {
                    nextLockoutExpiry = lockedUntil;
                }
                continue;
            }

            if (entry.PendingAttempts.Count == 0)
            {
                RemoveEntry(key);
                return true;
            }
        }

        // When every entry is protected, avoid rescanning the full table on each miss. A live
        // lockout can become evictable when its expiry arrives; pending-only entries are revisited
        // on the next stale-prune interval, which also catches expired reservations.
        _evictionRecheckAt = nextLockoutExpiry ?? AddClamped(now, PruneInterval);
        return false;
    }

    private static LoginKey BuildKey(string? resolutionIdentity, string? source) =>
        new(Truncate(WebUsernameNormalizer.TryNormalize(resolutionIdentity)), Truncate(source));

    private void PruneExpiredAttempts(Entry entry, DateTimeOffset now)
    {
        var cutoff = SubtractClamped(now, Window);
        while (entry.Failures.Count > 0 && entry.Failures.Peek() <= cutoff)
        {
            entry.Failures.Dequeue();
        }
        while (entry.PendingAttempts.Count > 0 && entry.PendingAttempts.Peek() <= cutoff)
        {
            entry.PendingAttempts.Dequeue();
        }
    }

    private static TimeSpan BoundedPositive(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero
            ? (value <= MaxConfiguredDuration ? value : MaxConfiguredDuration)
            : fallback;

    private static DateTimeOffset SubtractClamped(DateTimeOffset value, TimeSpan amount) =>
        value < DateTimeOffset.MinValue + amount ? DateTimeOffset.MinValue : value - amount;

    private static DateTimeOffset AddClamped(DateTimeOffset value, TimeSpan amount) =>
        value > DateTimeOffset.MaxValue - amount ? DateTimeOffset.MaxValue : value + amount;

    private void LogTableFull(int count, bool refusingUntrackedAttempt)
    {
        try
        {
            _logger.LogError(
                "Web-client login rate limiter key table is full ({Count}); {Action} fail-closed.",
                count,
                refusingUntrackedAttempt
                    ? "refusing an untracked attempt"
                    : "not tracking a new key");
        }
        catch
        {
            // Diagnostic only; a throwing provider must not fault-close the limiter.
        }
    }

    private void LogLockoutReleased(string? username, string? source)
    {
        try
        {
            _logger.LogWarning(
                "{AuditEventType}: web-client login lockout released; attempted-user={User}, source={Source}.",
                AuditEventTypes.LoginLockoutReleased, Describe(username), Describe(source));
        }
        catch
        {
            // Diagnostic only; a throwing provider must not fault-close the limiter.
        }
    }

    private void LogLockoutEngaged(string? username, string? source, TimeSpan duration, int lockoutCount)
    {
        try
        {
            _logger.LogWarning(
                "{AuditEventType}: web-client login lockout engaged for {Duration} " +
                "(consecutive lockout #{LockoutCount}); attempted-user={User}, source={Source}.",
                AuditEventTypes.LoginLockout, duration, lockoutCount,
                Describe(username), Describe(source));
        }
        catch
        {
            // Diagnostic only; a throwing provider must not fault-close the limiter.
        }
    }

    private void AddEntry(LoginKey key, Entry entry)
    {
        _entries.Add(key, entry);
        _activityNodes.Add(key, _activityOrder.AddLast(key));
        _evictionRecheckAt = null;
    }

    private void TouchEntry(LoginKey key)
    {
        if (_activityNodes.TryGetValue(key, out var node))
        {
            _activityOrder.Remove(node);
        }

        _activityNodes[key] = _activityOrder.AddLast(key);
        _evictionRecheckAt = null;
    }

    private void RemoveEntry(LoginKey key)
    {
        _entries.Remove(key);
        if (_activityNodes.Remove(key, out var node))
        {
            _activityOrder.Remove(node);
        }

        _evictionRecheckAt = null;
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(none)";
        }

        return value.Length <= MaxKeyComponentLength
            ? value
            : Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private static string Describe(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;
}
