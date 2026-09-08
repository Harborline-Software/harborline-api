using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Unit proof of the S11 web-client login rate limiter: sliding window, lockout engage, exponential
/// backoff, release on expiry, reset on success, per-key independence, memory bound (fail-closed),
/// and concurrency safety.
/// </summary>
public sealed class WebLoginRateLimiterTests
{
    private const string User = "founder";
    private const string Source = "192.0.2.10";

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public bool ThrowOnRead { get; set; }
        public override DateTimeOffset GetUtcNow() => ThrowOnRead
            ? throw new InvalidOperationException("clock unavailable")
            : _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ThrowingLogger : ILogger<WebLoginRateLimiter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logging provider failed");
    }

    private static (WebLoginRateLimiter Limiter, MutableTimeProvider Clock) Build(
        NodeWebLoginLockoutOptions? lockout = null,
        ILogger<WebLoginRateLimiter>? logger = null)
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new NodeWebClientOptions
        {
            Lockout = lockout ?? new NodeWebLoginLockoutOptions(),
        });
        return (new WebLoginRateLimiter(
            options,
            clock,
            logger ?? NullLogger<WebLoginRateLimiter>.Instance), clock);
    }

    private static void Fail(WebLoginRateLimiter limiter, int times, string user = User, string source = Source)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.True(limiter.CheckAttempt(user, source).IsAllowed);
            limiter.RecordFailure(user, source);
        }
    }

    [Fact(DisplayName = "Below the threshold, attempts stay admitted")]
    public void BelowThreshold_Admitted()
    {
        var (limiter, _) = Build();
        Fail(limiter, 4);
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "The Nth failure in the window engages the lockout — even the CORRECT password is refused")]
    public void Threshold_EngagesLockout()
    {
        var (limiter, _) = Build();
        Fail(limiter, 5);
        var decision = limiter.CheckAttempt(User, Source);
        Assert.False(decision.IsAllowed);
        Assert.NotNull(decision.RetryAfter);
        Assert.True(decision.RetryAfter > TimeSpan.Zero);
    }

    [Fact(DisplayName = "The window SLIDES — failures older than the window do not count")]
    public void Window_Slides()
    {
        var (limiter, clock) = Build();
        Fail(limiter, 4);
        clock.Advance(TimeSpan.FromMinutes(16)); // all four age out of the 15-minute window
        Fail(limiter, 4);
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "The lockout releases after its duration and attempts are admitted again")]
    public void Lockout_Releases_AfterDuration()
    {
        var (limiter, clock) = Build();
        Fail(limiter, 5);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "Consecutive lockouts back off exponentially, capped at the maximum")]
    public void ConsecutiveLockouts_BackOffExponentially()
    {
        var (limiter, clock) = Build(new NodeWebLoginLockoutOptions
        {
            MaxFailures = 2,
            Window = TimeSpan.FromMinutes(15),
            BaseLockoutDuration = TimeSpan.FromMinutes(1),
            MaxLockoutDuration = TimeSpan.FromMinutes(3),
        });

        Fail(limiter, 2); // lockout #1 — 1 min
        var first = limiter.CheckAttempt(User, Source);
        Assert.False(first.IsAllowed);
        Assert.True(first.RetryAfter <= TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Fail(limiter, 2); // lockout #2 — 2 min
        var second = limiter.CheckAttempt(User, Source);
        Assert.False(second.IsAllowed);
        Assert.True(second.RetryAfter > TimeSpan.FromMinutes(1));
        Assert.True(second.RetryAfter <= TimeSpan.FromMinutes(2));

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Fail(limiter, 2); // lockout #3 — capped at 3 min (not 4)
        var third = limiter.CheckAttempt(User, Source);
        Assert.False(third.IsAllowed);
        Assert.True(third.RetryAfter <= TimeSpan.FromMinutes(3));
        Assert.True(third.RetryAfter > TimeSpan.FromMinutes(2));
    }

    [Fact(DisplayName = "A successful login RESETS the pair — counters and backoff forgotten")]
    public void Success_Resets()
    {
        var (limiter, clock) = Build();
        Fail(limiter, 4);
        limiter.RecordSuccess(User, Source);
        Fail(limiter, 4); // a fresh window — 4 more failures do not engage
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);

        // And the backoff escalation is forgotten too: engage once, release, succeed, re-engage —
        // the new lockout is the BASE duration again, not the doubled one.
        limiter.RecordFailure(User, Source); // 5th → lockout #1
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
        limiter.RecordSuccess(User, Source);
        Fail(limiter, 5);
        var relocked = limiter.CheckAttempt(User, Source);
        Assert.False(relocked.IsAllowed);
        Assert.True(relocked.RetryAfter <= TimeSpan.FromMinutes(1));
    }

    [Fact(DisplayName = "Keys are per (username, source): a different user or source is unaffected")]
    public void Keys_AreIndependent()
    {
        var (limiter, _) = Build();
        Fail(limiter, 5);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
        Assert.True(limiter.CheckAttempt("someone-else", Source).IsAllowed);
        Assert.True(limiter.CheckAttempt(User, "198.51.100.7").IsAllowed);
        Assert.True(limiter.CheckAttempt(null, Source).IsAllowed);
    }

    [Fact(DisplayName = "One account cannot obtain a second budget through case or whitespace variants")]
    public void KeyVariants_ShareOneBudget()
    {
        var (limiter, _) = Build();
        Fail(limiter, 5);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
        Assert.False(limiter.CheckAttempt("FOUNDER", Source).IsAllowed);
        Assert.False(limiter.CheckAttempt(" founder ", Source).IsAllowed);
    }

    [Fact(DisplayName = "Distinct long usernames do not collide through key hashing")]
    public void LongUsernames_DoNotCollide()
    {
        var (limiter, _) = Build();
        var sharedPrefix = new string('u', 256);
        var first = sharedPrefix + "-alice";
        var second = sharedPrefix + "-bob";

        Fail(limiter, 5, user: first);
        Assert.False(limiter.CheckAttempt(first, Source).IsAllowed);
        Assert.True(limiter.CheckAttempt(second, Source).IsAllowed);
    }

    [Fact(DisplayName = "A full tracking table evicts the least-recently-active unprotected key")]
    public void FullTable_EvictsLeastRecentlyActiveKey()
    {
        var (limiter, _) = Build(new NodeWebLoginLockoutOptions { MaxTrackedKeys = 2 });
        limiter.RecordFailure("a", Source);
        limiter.RecordFailure("b", Source);
        Assert.True(limiter.CheckAttempt("c", Source).IsAllowed);
    }

    [Fact(DisplayName = "A full tracking table refuses a new key when every entry is locked")]
    public void FullTable_RefusesNewKeys_WhenEveryEntryIsLocked()
    {
        var (limiter, _) = Build(new NodeWebLoginLockoutOptions { MaxTrackedKeys = 2 });
        Fail(limiter, 5, user: "a");
        Fail(limiter, 5, user: "b");

        Assert.False(limiter.CheckAttempt("c", Source).IsAllowed);
    }

    [Fact(DisplayName = "Stale entries are pruned so a full table recovers once windows age out")]
    public void FullTable_Recovers_AfterPrune()
    {
        var (limiter, clock) = Build(new NodeWebLoginLockoutOptions
        {
            MaxTrackedKeys = 2,
            MaxFailures = 1,
        });
        Fail(limiter, 1, user: "a");
        Fail(limiter, 1, user: "b");
        Assert.False(limiter.CheckAttempt("c", Source).IsAllowed);
        clock.Advance(TimeSpan.FromMinutes(16)); // both lockouts and windows have expired
        Assert.True(limiter.CheckAttempt("c", Source).IsAllowed);
        limiter.RecordFailure("c", Source);
        Assert.False(limiter.CheckAttempt("c", Source).IsAllowed);
    }

    [Fact(DisplayName = "Concurrent failures never under-count: parallel hammering engages the lockout")]
    public async Task Concurrency_EngagesLockout()
    {
        var (limiter, _) = Build();
        var admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                if (limiter.CheckAttempt(User, Source).IsAllowed)
                {
                    Interlocked.Increment(ref admitted);
                    limiter.RecordFailure(User, Source);
                }
            }
        })));
        Assert.InRange(admitted, 1, 5);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "Concurrent traffic across many keys stays internally consistent (no torn state)")]
    public async Task Concurrency_ManyKeys_NoTornState()
    {
        var (limiter, _) = Build();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var user = $"user-{worker % 4}";
                limiter.CheckAttempt(user, Source);
                limiter.RecordFailure(user, Source);
                if (i % 7 == 0)
                {
                    limiter.RecordSuccess(user, Source);
                }
            }
        })));
        // The property under test is that nothing threw and the limiter still answers coherently.
        Assert.True(limiter.CheckAttempt("fresh-user", Source).IsAllowed);
    }

    [Fact(DisplayName = "Nonsense configuration hardens rather than disables (threshold clamps to 1)")]
    public void NonsenseConfig_Hardens()
    {
        var (limiter, _) = Build(new NodeWebLoginLockoutOptions
        {
            MaxFailures = 0,
            Window = TimeSpan.Zero,
            BaseLockoutDuration = TimeSpan.Zero,
        });
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
        limiter.RecordFailure(User, Source);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "Extreme duration configuration cannot overflow the limiter clock arithmetic")]
    public void ExtremeDurations_Harden()
    {
        var (limiter, _) = Build(new NodeWebLoginLockoutOptions
        {
            Window = TimeSpan.MaxValue,
            BaseLockoutDuration = TimeSpan.MaxValue,
            MaxLockoutDuration = TimeSpan.MaxValue,
        });

        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
        limiter.RecordFailure(User, Source);
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "A limiter error poisons the gate so recovery cannot become fail-open")]
    public void LimiterError_PoisonsGate_FailClosed()
    {
        var (limiter, clock) = Build();
        clock.ThrowOnRead = true;

        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);

        // The dependency recovers, but the limiter does not silently reopen with unknown state.
        clock.ThrowOnRead = false;
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "A throwing logger cannot fault-close the limiter during audit emission")]
    public void LoggerFailure_DoesNotFaultClose()
    {
        var (limiter, clock) = Build(
            new NodeWebLoginLockoutOptions
            {
                MaxFailures = 1,
                BaseLockoutDuration = TimeSpan.FromMinutes(1),
                MaxLockoutDuration = TimeSpan.FromMinutes(1),
            },
            new ThrowingLogger());

        Fail(limiter, 1);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        // Both the engage and release audit calls throw; the state decision remains usable.
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
    }

    [Fact(DisplayName = "An abandoned reservation can be released without changing completed failures")]
    public void AbandonedReservation_Releases_OnlyPendingSlot()
    {
        var (limiter, _) = Build(new NodeWebLoginLockoutOptions { MaxFailures = 2 });

        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
        limiter.RecordFailure(User, Source);

        // This reservation is abandoned and must not erase the completed failure above.
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
        limiter.ReleaseAttempt(User, Source);

        // The completed failure still occupies one of the two slots: this next failure crosses the
        // threshold, proving ReleaseAttempt removed only the pending reservation.
        Assert.True(limiter.CheckAttempt(User, Source).IsAllowed);
        limiter.RecordFailure(User, Source);
        Assert.False(limiter.CheckAttempt(User, Source).IsAllowed);
    }
}
