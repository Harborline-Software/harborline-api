using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Diagnostics;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Diagnostics;

/// <summary>
/// Tests for the dev COMMS DIAGNOSTIC-LOGGING flag (default-on pre-release): the flag GATES the verbose
/// <c>[comms-diag]</c> output (ON emits, OFF suppresses), and NO secret value is ever formatted into a line.
/// </summary>
/// <remarks>
/// These are the two VALIDATE assertions from the task: (1) the flag gates the verbose output; (2) a secret value
/// passed alongside the diagnostic NEVER appears in the emitted text (the helpers only admit lengths / public ids
/// and deliberately accept no token identifier — they cannot interpolate a key/token/seed value).
/// </remarks>
public sealed class CommsDiagnosticsTests
{
    // ── (1) The flag gates the verbose output ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOn_EmitsVerboseAdmitRejectLine_WithTheRealReason()
    {
        var logger = new CapturingLogger();
        var diag = new CommsDiagnostics(logger, enabled: true);

        diag.AdmitOutcome(
            accepted: false, reason: "invite_rejected", joiningPartyId: "os:bob#deadbeef");

        Assert.True(diag.IsEnabled);
        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("[comms-diag]", line.Message, StringComparison.Ordinal);
        Assert.Contains("invite_rejected", line.Message, StringComparison.Ordinal); // the REAL reason is visible
    }

    [Fact]
    public void FlagOff_SuppressesVerboseOutput_NormalLoggingOnly()
    {
        var logger = new CapturingLogger();
        var diag = new CommsDiagnostics(logger, enabled: false);

        // Exercise every emit method — NONE should write when the flag is OFF.
        diag.AdmitOutcome(accepted: false, "invite_rejected", "os:bob#deadbeef");
        diag.JoinOutcome(succeeded: false, "anchor_mismatch", "team-1");
        diag.RouteRejectMapping("POST /admission/redeem", "admit_rejected");
        diag.PeerTrustDecision(trusted: false, presentedKeyLength: 32, trustedSetCount: 1);
        diag.TeamAdoptionStep("active-team-switch", "team-1", "detail");
        diag.KeyScoping("transport", "team-1", 32);
        diag.TokenRedeemStep("verify-request");
        diag.RosterRebuild(adopted: false, "stale_seed", "team-1");

        Assert.False(diag.IsEnabled);
        Assert.Empty(logger.Entries); // OFF ⇒ no verbose [comms-diag] lines at all
    }

    [Fact]
    public void Disabled_StaticInstance_IsNeverEnabled_AndEmitsNothing()
    {
        // The production-off posture / never-null fallback.
        Assert.False(CommsDiagnostics.Disabled.IsEnabled);
        // No logger wired ⇒ a no-op even though the call is made (must not throw).
        CommsDiagnostics.Disabled.PeerTrustDecision(trusted: false, presentedKeyLength: 32, trustedSetCount: 0);
    }

    [Fact]
    public void FlagOn_PeerUntrusted_EmitsLengthAndCount_NotAKeyValue()
    {
        var logger = new CapturingLogger();
        var diag = new CommsDiagnostics(logger, enabled: true);

        diag.PeerTrustDecision(trusted: false, presentedKeyLength: 32, trustedSetCount: 3);

        var line = Assert.Single(logger.Entries);
        Assert.Contains("PEER_UNTRUSTED", line.Message, StringComparison.Ordinal);
        Assert.Contains("presentedKeyLen=32", line.Message, StringComparison.Ordinal); // a LENGTH, not a key
        Assert.Contains("trustedSetCount=3", line.Message, StringComparison.Ordinal); // a COUNT, not the keys
    }

    // ── (2) No secret value is ever formatted ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OpaqueTokenIdentifier_IsNotAcceptedOrFormatted()
    {
        var logger = new CapturingLogger();
        var diag = new CommsDiagnostics(logger, enabled: true);

        // The diagnostic API deliberately has no token-id parameter. Exercise the three enrollment events and pin
        // the emitted wire image so the old tokenId field cannot return unnoticed.
        const string fullTokenId = "tok-PUBLICPREFIX-SECRETTAILMUSTNOTAPPEAR";
        diag.AdmitOutcome(accepted: false, "invite_rejected", "os:bob#deadbeef");
        diag.JoinOutcome(succeeded: false, "no_response", "team-1");
        diag.TokenRedeemStep("verify-request");

        var all = string.Join("\n", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("SECRETTAILMUSTNOTAPPEAR", all, StringComparison.Ordinal);
        Assert.DoesNotContain(fullTokenId, all, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenId", all, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyScoping_FormatsTeamScopeAndLength_NeverAKeyValue()
    {
        var logger = new CapturingLogger();
        var diag = new CommsDiagnostics(logger, enabled: true);

        diag.KeyScoping("transport", teamScope: "team-7", keyLength: 32);

        var line = Assert.Single(logger.Entries);
        Assert.Contains("keyLen=32B", line.Message, StringComparison.Ordinal); // a length
        Assert.Contains("teamScope=team-7", line.Message, StringComparison.Ordinal); // a public scope id
    }

    // ── Test capturing logger ─────────────────────────────────────────────────────────────────────────────────

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
