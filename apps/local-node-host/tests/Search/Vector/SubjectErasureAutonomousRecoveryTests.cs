using System.Diagnostics;
using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// T-1048 (DES-0029 ck-6): an erasure interrupted after its registry commit is finished by the host's recovery
/// pass, with no client retry, from the approval evidence the mark recorded; the evidence is cleared in the commit
/// that stages the SubjectErased audit in the outbox. Real durable files throughout; the first test loses the
/// erasing process for real (<see cref="Process.Kill(bool)"/>).
/// </summary>
public sealed class SubjectErasureAutonomousRecoveryTests : IAsyncLifetime
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    internal static readonly TenantId Tenant = TenantId.FromString("tenant-erasure-recovery");
    internal static readonly SubjectId Alice = new("subject-alice");

    private SearchTestStore _store = null!;

    public async Task InitializeAsync() => _store = await SearchTestStore.CreateAsync();

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact(DisplayName = "T-1048: an erasing process killed after the registry commit is finished by the startup recovery alone — one audit with the literal evidence, evidence cleared")]
    public async Task KilledProcess_StartupRecoveryFinishesTheErasureOnce()
    {
        await RunChildUntilKilledAsync(_store.DatabasePath);

        // The kill left a marked erasure with its evidence, no tombstone and no audit.
        var pending = await RowAsync(Alice);
        Assert.Equal("[\"captain\",\"officer\"]", pending.ApprovingActorsJson);
        Assert.Equal("erasure-ticket", pending.LegalBasis);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), pending.ApprovedAtUnixMs);
        Assert.Null(pending.CompletedAtUnixMs);
        Assert.Equal(0, await CountAsync("search_subject_tombstones"));
        Assert.Equal(0, await CountAsync("search_audit_outbox"));

        // A fresh host over the same file: only its startup pass runs, no request is retried.
        var restarted = SearchTestStore.Reopen(_store);
        var trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(restarted.Factory));
        var signer = new Ed25519Signer(KeyPair.Generate());
        using var outbox = new NodeAuditOutbox(
            restarted.Factory, trail, trail, signer, TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
        var service = Service(restarted, new NodeEfSubjectTombstoneStore(restarted.Factory), trail, signer, new ScriptedPropagator());
        using var daemon = new NodeAuditOutboxDrainDaemon(
            outbox, TimeProvider.System, NullLogger<NodeAuditOutboxDrainDaemon>.Instance, service);
        await daemon.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while ((await RowAsync(Alice)).CompletedAtUnixMs is null || await CountAsync("search_audit_trail") == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The startup recovery did not finish the erasure within 60s.");
                await Task.Delay(100);
            }
        }
        finally
        {
            await daemon.StopAsync(CancellationToken.None);
        }

        var audit = Assert.Single(await ErasedAuditsAsync(trail));
        Assert.Equal(Now, audit.OccurredAt);
        Assert.Equal(new[] { "captain", "officer" }, Assert.IsType<string[]>(audit.Payload.Payload.Body["approving_actors"]));
        Assert.Equal("erasure-ticket", audit.Payload.Payload.Body["legal_basis"]);
        var tombstone = await new NodeEfSubjectTombstoneStore(restarted.Factory)
            .FindAsync(Tenant, SubjectPseudonym.Derive(Tenant, Alice));
        Assert.NotNull(tombstone);
        Assert.Equal(Now, tombstone!.ErasedAt);
        await AssertEvidenceClearedAsync(Alice);

        // A second recovery adds nothing.
        Assert.Equal(0, await service.RecoverInterruptedAsync(32));
        await outbox.DrainAsync();
        Assert.Single(await ErasedAuditsAsync(trail));
        Assert.Equal(1, await CountAsync("search_audit_outbox"));
    }

    [Fact(DisplayName = "T-1048: a crash after the audit reached the durable trail but before the evidence was cleared is completed once by recovery; a crash after clearing leaves nothing to do")]
    public async Task CrashBetweenSecuringTheAuditAndClearing_RecoveryCompletesOnce()
    {
        var trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(_store.Factory));
        var signer = new Ed25519Signer(KeyPair.Generate());
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), trail, signer, new ScriptedPropagator());

        // The completion commit fails: the trail append (its own commit) holds the audit; the evidence stays.
        await ExecuteAsync("CREATE TRIGGER t1048_clear BEFORE UPDATE ON search_subject_erasures BEGIN SELECT RAISE(ABORT, 't1048'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(Alice)));
        await ExecuteAsync("DROP TRIGGER t1048_clear;");
        Assert.Single(await ErasedAuditsAsync(trail));
        Assert.Equal(0, await CountAsync("search_audit_outbox"));
        Assert.Equal("erasure-ticket", (await RowAsync(Alice)).LegalBasis);

        Assert.Equal(1, await service.RecoverInterruptedAsync(32));

        await AssertEvidenceClearedAsync(Alice);
        Assert.Equal(1, await CountAsync("search_audit_outbox"));
        Assert.Single(await ErasedAuditsAsync(trail));

        // After clearing: recovery, a drain and a client retry add nothing.
        Assert.Equal(0, await service.RecoverInterruptedAsync(32));
        using var outbox = new NodeAuditOutbox(
            _store.Factory, trail, trail, signer, TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
        await outbox.DrainAsync();
        Assert.Equal(SubjectErasureOutcome.AlreadyErased, (await service.EraseAsync(Request(Alice))).Outcome);
        Assert.Single(await ErasedAuditsAsync(trail));
        Assert.Equal(1, await CountAsync("search_audit_outbox"));
    }

    [Fact(DisplayName = "T-1048: the evidence is never cleared before the audit is staged durably — a failed outbox stage keeps it, so an in-memory append alone is not enough")]
    public async Task FailedOutboxStage_KeepsTheEvidence()
    {
        var memoryTrail = new InMemoryAuditTrail();
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), memoryTrail,
            new Ed25519Signer(KeyPair.Generate()), new ScriptedPropagator());

        await ExecuteAsync("CREATE TRIGGER t1048_stage BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't1048'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(Alice)));
        await ExecuteAsync("DROP TRIGGER t1048_stage;");

        // The in-memory append succeeded, yet the evidence stays until the audit is durable.
        Assert.Single(await ErasedAuditsAsync(memoryTrail));
        var row = await RowAsync(Alice);
        Assert.Equal("erasure-ticket", row.LegalBasis);
        Assert.Null(row.CompletedAtUnixMs);

        Assert.Equal(1, await service.RecoverInterruptedAsync(32));
        await AssertEvidenceClearedAsync(Alice);
        Assert.Equal(1, await CountAsync("search_audit_outbox"));
        Assert.Single(await ErasedAuditsAsync(memoryTrail));
    }

    [Fact(DisplayName = "T-1048: a refused erasure leaves no registry row, no evidence and no audit")]
    public async Task RefusedErasure_LeavesNothing()
    {
        var trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(_store.Factory));
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), trail,
            new Ed25519Signer(KeyPair.Generate()), new ScriptedPropagator());

        await Assert.ThrowsAsync<SubjectErasureRejectedException>(() =>
            service.EraseAsync(new SubjectErasureRequest(Tenant, Alice, Now, [new ActorId("captain")], "erasure-ticket")));

        Assert.Equal(0, await CountAsync("search_subject_erasures"));
        Assert.Equal(0, await CountAsync("search_audit_outbox"));
        Assert.Empty(await ErasedAuditsAsync(trail));
        Assert.Equal(0, await service.RecoverInterruptedAsync(32));
    }

    [Fact(DisplayName = "T-1048: limit+1 permanently failing erasures do not stop a newer one from completing — backoff skips rows not yet due, and none is abandoned")]
    public async Task PermanentlyFailingRows_DoNotStarveANewerRow()
    {
        const int limit = 2;
        var clock = new MutableClock();
        var propagator = new ScriptedPropagator { FailAll = true };
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), new InMemoryAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()), propagator, clock);

        var failing = Enumerable.Range(0, limit + 1).Select(i => new SubjectId($"subject-failing-{i}")).ToArray();
        for (var i = 0; i < failing.Length; i++)
        {
            clock.At = Now.AddSeconds(i);
            await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(failing[i])));
        }
        var newer = new SubjectId("subject-newer");
        clock.At = Now.AddMinutes(1);
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(newer)));

        propagator.FailAll = false;
        propagator.Failing.UnionWith(failing.Select(s => s.Value));
        clock.At = Now.AddMinutes(2);

        Assert.Equal(0, await service.RecoverInterruptedAsync(limit));
        Assert.Null((await RowAsync(newer)).CompletedAtUnixMs);
        Assert.Equal(1, await service.RecoverInterruptedAsync(limit));

        await AssertEvidenceClearedAsync(newer);
        foreach (var subject in failing)
        {
            var row = await RowAsync(subject);
            Assert.Equal(1, row.RecoveryAttempts);
            Assert.Equal(Now.AddMinutes(3).ToUnixTimeMilliseconds(), row.NextRecoveryAtUnixMs);
            Assert.Equal("erasure-ticket", row.LegalBasis);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDeferralCommit_LaterFetchedErasureCompletes_AndFailedEvidenceSurvivesRestart(bool foreignCancellation)
    {
        var clock = new MutableClock();
        var propagator = new ScriptedPropagator { FailAll = true };
        var trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(_store.Factory));
        var signer = new Ed25519Signer(KeyPair.Generate());
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), trail, signer, propagator, clock);
        var newer = new SubjectId("subject-newer-deferral-failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EraseAsync(Request(Alice)));
        clock.At = Now.AddSeconds(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EraseAsync(Request(newer)));
        propagator.FailAll = false;
        if (foreignCancellation) propagator.Canceling.Add(Alice.Value);
        else propagator.Failing.Add(Alice.Value);
        clock.At = Now.AddMinutes(1);
        await ExecuteAsync("CREATE TRIGGER t1048_defer BEFORE UPDATE OF recovery_attempts ON search_subject_erasures WHEN OLD.subject_id = 'subject-alice' BEGIN SELECT RAISE(ABORT, 't1048 deferral commit'); END;");

        Assert.Equal(1, await service.RecoverInterruptedAsync(2));
        var failed = await RowAsync(Alice);
        Assert.Equal("[\"captain\",\"officer\"]", failed.ApprovingActorsJson);
        Assert.Equal("erasure-ticket", failed.LegalBasis);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), failed.ApprovedAtUnixMs);
        Assert.Null(failed.CompletedAtUnixMs);
        Assert.Equal(0, failed.RecoveryAttempts);
        Assert.Null(failed.NextRecoveryAtUnixMs);
        await AssertEvidenceClearedAsync(newer);
        var audit = Assert.Single(await ErasedAuditsAsync(trail));
        Assert.Equal(Now.AddSeconds(1), audit.OccurredAt);
        Assert.Equal("SubjectErased", audit.EventType.Value);
        Assert.Equal("14f86897d1a58c76cdc6bcbefb82eb550e6a8b58b43c1ff60f632147234edb4a", audit.Payload.Payload.Body["pseudonym"]);
        Assert.Equal(new[] { "captain", "officer" }, Assert.IsType<string[]>(audit.Payload.Payload.Body["approving_actors"]));
        Assert.Equal("erasure-ticket", audit.Payload.Payload.Body["legal_basis"]);

        await ExecuteAsync("DROP TRIGGER t1048_defer;");
        await using var restarted = SearchTestStore.Reopen(_store);
        var restartedTrail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(restarted.Factory));
        var restartedService = Service(restarted, new NodeEfSubjectTombstoneStore(restarted.Factory), restartedTrail,
            signer, new ScriptedPropagator(), clock);
        Assert.Equal(1, await restartedService.RecoverInterruptedAsync(2));
        Assert.Equal(0, await restartedService.RecoverInterruptedAsync(2));
        using var outbox = new NodeAuditOutbox(restarted.Factory, restartedTrail, restartedTrail, signer,
            TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
        await outbox.DrainAsync();
        await outbox.DrainAsync();
        await AssertEvidenceClearedAsync(Alice);
        var audits = await ErasedAuditsAsync(restartedTrail);
        Assert.Equal(2, audits.Count);
        Assert.Single(audits, item => Equals(item.Payload.Payload.Body["pseudonym"], "7d75714985fb847758daa05d46ba162ddbc461eb60d784dd80bc0353b1dfb987"));
        Assert.Single(audits, item => Equals(item.Payload.Payload.Body["pseudonym"], "14f86897d1a58c76cdc6bcbefb82eb550e6a8b58b43c1ff60f632147234edb4a"));
        Assert.Equal(2, await CountAsync("search_audit_outbox"));
    }

    [Fact(DisplayName = "T-1057: the registry dates the mark with the erasing act's instant and the completion with the finishing pass's, never a clock of its own")]
    public async Task TheRegistryIsDatedByItsCallers()
    {
        var clock = new MutableClock();
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), new InMemoryAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()), new ScriptedPropagator(), clock);

        // The erase act at 2026-01-01T00:00Z marks; its outbox stage fails, so the recovery pass finishes it later.
        await ExecuteAsync("CREATE TRIGGER t1057_stage BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't1057'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(Alice)));
        await ExecuteAsync("DROP TRIGGER t1057_stage;");
        Assert.Equal(1767225600000, (await RowAsync(Alice)).ErasedAtUnixMs);
        Assert.Null((await RowAsync(Alice)).CompletedAtUnixMs);

        // The recovery pass at 03:00Z completes it, dated with that pass's instant.
        clock.At = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, await service.RecoverInterruptedAsync(32));
        Assert.Equal(1767236400000, (await RowAsync(Alice)).CompletedAtUnixMs);

        // A mark without evidence records the instant its caller hands it.
        var bob = new SubjectId("subject-bob");
        Assert.True(await new NodeEfSubjectErasureRegistry(_store.Factory).MarkErasedAsync(
            Tenant, bob, new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero)));
        Assert.Equal(1770091506000, (await RowAsync(bob)).ErasedAtUnixMs);
    }

    [Fact]
    public async Task RecoveryEvidenceMigration_UpgradesMainPredecessorWithoutInventingLegacyApproval()
    {
        await using var historical = await SearchTestStore.CreateAtMigrationAsync("20261002190000_AuditOutboxPredecessor");
        await using var db = historical.CreateContext();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO search_subject_erasures (tenant_id, subject_id, erased_at_unix_ms) VALUES ('legacy-tenant', 'legacy-subject', 1767225600000);");
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261002190000_AuditOutboxPredecessor", await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains("20261004130000_SubjectErasureRecoveryEvidence", await db.Database.GetAppliedMigrationsAsync());
        var row = await db.SubjectErasures.AsNoTracking().SingleAsync();
        Assert.Equal("legacy-tenant", row.TenantId);
        Assert.Equal("legacy-subject", row.SubjectId);
        Assert.Equal(1767225600000, row.ErasedAtUnixMs);
        Assert.Null(row.ApprovingActorsJson);
        Assert.Null(row.LegalBasis);
        Assert.Null(row.ApprovedAtUnixMs);
        Assert.Null(row.CompletedAtUnixMs);
        Assert.Null(row.NextRecoveryAtUnixMs);
        Assert.Equal(0, row.RecoveryAttempts);
        Assert.Empty(await new NodeEfSubjectErasureRegistry(historical.Factory).ListDueAsync(Now, 32));
    }

    [Fact]
    public async Task RequestedCancellationDuringDeferral_PropagatesWithoutCompletingLaterRows()
    {
        var propagator = new ScriptedPropagator { FailAll = true };
        var trail = new InMemoryAuditTrail();
        var signer = new Ed25519Signer(KeyPair.Generate());
        var clock = new MutableClock();
        var seed = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), trail, signer, propagator, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.EraseAsync(Request(Alice)));
        var newer = new SubjectId("subject-newer-cancel-defer");
        clock.At = Now.AddSeconds(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.EraseAsync(Request(newer)));
        propagator.FailAll = false;
        propagator.Failing.Add(Alice.Value);
        using var pass = new CancellationTokenSource();
        var registry = new CancelOnDeferralRegistry(new NodeEfSubjectErasureRegistry(_store.Factory), pass);
        var service = new SubjectErasureService(registry, new NodeEfSubjectTombstoneStore(_store.Factory), trail, signer,
            new NoopTenantKeyDestroyer(), clock, minimumWindow: TimeSpan.Zero, propagators: [propagator]);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecoverInterruptedAsync(2, pass.Token));
        Assert.Same(registry.Cancellation, exception);
        Assert.Equal(pass.Token, exception.CancellationToken);
        Assert.Equal(0, (await RowAsync(Alice)).RecoveryAttempts);
        Assert.Null((await RowAsync(Alice)).NextRecoveryAtUnixMs);
        Assert.Equal("erasure-ticket", (await RowAsync(Alice)).LegalBasis);
        Assert.Null((await RowAsync(newer)).CompletedAtUnixMs);
        Assert.Equal("erasure-ticket", (await RowAsync(newer)).LegalBasis);
        Assert.Empty(await ErasedAuditsAsync(trail));
        Assert.Equal(0, await CountAsync("search_audit_outbox"));
    }

    private sealed class CancelOnDeferralRegistry(ISubjectErasureRecoveryRegistry inner, CancellationTokenSource pass)
        : ISubjectErasureRecoveryRegistry
    {
        public OperationCanceledException? Cancellation { get; private set; }

        public ValueTask<bool> IsErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
            => inner.IsErasedAsync(tenant, subject, ct);
        public ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, DateTimeOffset erasedAt, CancellationToken ct = default)
            => inner.MarkErasedAsync(tenant, subject, erasedAt, ct);
        public ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, SubjectErasureEvidence evidence, CancellationToken ct = default)
            => inner.MarkErasedAsync(tenant, subject, evidence, ct);
        public ValueTask<SubjectErasureEvidence?> FindEvidenceAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
            => inner.FindEvidenceAsync(tenant, subject, ct);
        public ValueTask<bool> IsCompletedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
            => inner.IsCompletedAsync(tenant, subject, ct);
        public ValueTask CompleteAsync(SubjectId subject, AuditRecord audit, DateTimeOffset completedAt, CancellationToken ct = default)
            => inner.CompleteAsync(subject, audit, completedAt, ct);
        public ValueTask<IReadOnlyList<InterruptedSubjectErasure>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
            => inner.ListDueAsync(now, limit, ct);
        public async ValueTask DeferAsync(TenantId tenant, SubjectId subject, DateTimeOffset now, CancellationToken ct = default)
        {
            pass.Cancel();
            try { await inner.DeferAsync(tenant, subject, now, ct); }
            catch (OperationCanceledException exception) { Cancellation = exception; throw; }
        }
    }

    internal static SubjectErasureService Service(
        SearchTestStore store,
        ISubjectTombstoneStore tombstones,
        IAuditTrail trail,
        IOperationSigner signer,
        ISubjectErasurePropagator propagator,
        IRecoveryClock? clock = null) => new(
        new NodeEfSubjectErasureRegistry(store.Factory),
        tombstones,
        trail,
        signer,
        new NoopTenantKeyDestroyer(),
        clock ?? new MutableClock(),
        minimumWindow: TimeSpan.Zero,
        propagators: [propagator]);

    internal static SubjectErasureRequest Request(SubjectId subject) =>
        new(Tenant, subject, Now, [new ActorId("captain"), new ActorId("officer")], "erasure-ticket");

    private static async Task RunChildUntilKilledAsync(string databasePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetMuxer(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // The child signals by a file. Its output is awaited only once it has exited (reading it sooner blocks until exit).
        var readyFile = databasePath + ".t1048g-ready";
        startInfo.ArgumentList.Add(typeof(SubjectErasureAutonomousRecoveryTests).Assembly.Location);
        startInfo.Environment[SubjectErasureChildProcess.DatabaseVariable] = databasePath;
        using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("The erasing child did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!File.Exists(readyFile))
            {
                if (child.HasExited)
                    Assert.Fail("The erasing child exited before the injected point: " + await stdout + await stderr);
                Assert.True(DateTime.UtcNow < deadline, "The erasing child did not reach the injected point within 120s.");
                await Task.Delay(50);
            }

            // The real stop: the process is killed while the tombstone write is in progress.
            child.Kill(entireProcessTree: true);
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync(exit.Token);
            Assert.NotEqual(SubjectErasureChildProcess.DeadlineExitCode, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    private static string DotnetMuxer()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        var processPath = Environment.ProcessPath;
        return !string.IsNullOrEmpty(processPath)
            && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : "dotnet";
    }

    private async Task<SubjectErasureRow> RowAsync(SubjectId subject)
    {
        await using var db = _store.CreateContext();
        return await db.SubjectErasures.AsNoTracking()
            .SingleAsync(r => r.TenantId == Tenant.Value && r.SubjectId == subject.Value);
    }

    private async Task AssertEvidenceClearedAsync(SubjectId subject)
    {
        var row = await RowAsync(subject);
        Assert.NotNull(row.CompletedAtUnixMs);
        Assert.Null(row.ApprovingActorsJson);
        Assert.Null(row.LegalBasis);
        Assert.Null(row.ApprovedAtUnixMs);
    }

    [Fact(DisplayName = "T-1048: a retry finishes an interrupted erasure with the evidence its first mark recorded, never the retry's approvers, basis or time")]
    public async Task RetryAfterInterruption_KeepsTheOriginalEvidence()
    {
        var clock = new MutableClock { At = Now };
        var trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(_store.Factory));
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), trail,
            new Ed25519Signer(KeyPair.Generate()), new ScriptedPropagator(), clock);

        // The first attempt marks with captain + officer at Now, then dies before its tombstone.
        await ExecuteAsync("CREATE TRIGGER t1048_tombstone BEFORE INSERT ON search_subject_tombstones BEGIN SELECT RAISE(ABORT, 't1048'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(Alice)));
        await ExecuteAsync("DROP TRIGGER t1048_tombstone;");

        // A retry an hour later carries other approvers and another basis.
        clock.At = Now.AddHours(1);
        var retry = new SubjectErasureRequest(
            Tenant, Alice, Now, [new ActorId("auditor"), new ActorId("counsel")], "other-ticket");
        var result = await service.EraseAsync(retry);

        Assert.Equal(SubjectErasureOutcome.Erased, result.Outcome);
        Assert.Equal(["captain", "officer"], result.Tombstone!.ApprovingActors.Select(a => a.Value));
        Assert.Equal("erasure-ticket", result.Tombstone.LegalBasis);
        Assert.Equal(Now, result.Tombstone.ErasedAt);
        Assert.Single(await ErasedAuditsAsync(trail));
        await AssertEvidenceClearedAsync(Alice);
    }

    [Fact(DisplayName = "T-1048: ISubjectErasureService keeps its one member, so an existing implementation compiles; recovery is the separate ISubjectErasureRecovery")]
    public void An_erase_only_implementation_still_satisfies_the_service_interface()
    {
        ISubjectErasureService eraseOnly = new EraseOnlyService();
        Assert.IsNotAssignableFrom<ISubjectErasureRecovery>(eraseOnly);
        Assert.Equal([nameof(ISubjectErasureService.EraseAsync)], typeof(ISubjectErasureService).GetMethods().Select(m => m.Name));
    }

    private sealed class EraseOnlyService : ISubjectErasureService
    {
        public Task<SubjectErasureResult> EraseAsync(SubjectErasureRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact(DisplayName = "T-1048: a cancellation that is not the pass's own defers its row like any fault, so it cannot end the pass or starve a newer row; the pass's own cancellation ends it without a deferral")]
    public async Task ForeignCancellation_IsDeferred_ButThePassOwnCancellationEndsIt()
    {
        var clock = new MutableClock();
        var propagator = new ScriptedPropagator { FailAll = true };
        var service = Service(_store, new NodeEfSubjectTombstoneStore(_store.Factory), new InMemoryAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()), propagator, clock);
        var canceling = new SubjectId("subject-canceling");
        var newer = new SubjectId("subject-newer");
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(canceling)));
        clock.At = Now.AddSeconds(1);
        await Assert.ThrowsAnyAsync<Exception>(() => service.EraseAsync(Request(newer)));

        propagator.FailAll = false;
        propagator.Canceling.Add(canceling.Value);
        clock.At = Now.AddMinutes(1);

        // The pass's own token is cancelled while the row runs: the pass ends and defers nothing.
        using (var pass = new CancellationTokenSource())
        {
            propagator.CancelPass = pass;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecoverInterruptedAsync(32, pass.Token));
            propagator.CancelPass = null;
        }
        Assert.Equal(0, (await RowAsync(canceling)).RecoveryAttempts);

        // A foreign cancellation is a fault of that row: deferred, and the newer row still completes in the same pass.
        Assert.Equal(1, await service.RecoverInterruptedAsync(32));
        Assert.Equal(1, (await RowAsync(canceling)).RecoveryAttempts);
        Assert.Null((await RowAsync(canceling)).CompletedAtUnixMs);
        await AssertEvidenceClearedAsync(newer);
    }

    private async Task<int> CountAsync(string table)
    {
        await using var db = _store.CreateContext();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<List<AuditRecord>> ErasedAuditsAsync(IAuditTrail trail)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in trail.QueryAsync(new AuditQuery(Tenant, AuditEventType.SubjectErased)))
            records.Add(record);
        return records;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = _store.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    internal sealed class MutableClock : IRecoveryClock
    {
        public DateTimeOffset At { get; set; } = Now;

        public DateTimeOffset UtcNow() => At;
    }

    internal sealed class NoopTenantKeyDestroyer : ITenantKeyDestroyer
    {
        public Task DestroySubjectKeysAsync(TenantId tenant, SubjectId subject, CancellationToken ct) => Task.CompletedTask;

        public Task DestroyTenantKeysAsync(TenantId tenant, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class ScriptedPropagator : ISubjectErasurePropagator
    {
        public bool FailAll { get; set; }

        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Canceling { get; } = new(StringComparer.Ordinal);

        /// <summary>When set, a canceling subject first cancels this source: the pass's own cancellation.</summary>
        public CancellationTokenSource? CancelPass { get; set; }

        public Task PropagateErasureAsync(TenantId tenant, SubjectId subject, CancellationToken ct)
        {
            if (Canceling.Contains(subject.Value) && CancelPass is { } pass)
            {
                pass.Cancel();
                throw new OperationCanceledException(pass.Token);
            }

            return Canceling.Contains(subject.Value)
                ? throw new OperationCanceledException("T-1048 injected cancellation not tied to the pass")
                : FailAll || Failing.Contains(subject.Value)
                    ? throw new InvalidOperationException("T-1048 injected purge fault")
                    : Task.CompletedTask;
        }
    }
}

/// <summary>
/// T-1048: the erasing child process. When the test starts this assembly with
/// <see cref="DatabaseVariable"/> set, the module initializer erases Alice over that file and stops, alive, at
/// the tombstone write, after the registry commit and before the audit, and creates
/// <c>&lt;database&gt;.t1048g-ready</c> for the parent, which kills it.
/// </summary>
internal static class SubjectErasureChildProcess
{
    internal const string DatabaseVariable = "HARBORLINE_T1048G_ERASURE_CHILD_DB";
    internal const int DeadlineExitCode = 4;

    [ModuleInitializer]
    internal static void RunWhenChild()
    {
        var databasePath = Environment.GetEnvironmentVariable(DatabaseVariable);
        if (string.IsNullOrEmpty(databasePath)) return;
        var store = SearchTestStore.OpenExisting(databasePath);
        var service = SubjectErasureAutonomousRecoveryTests.Service(
            store, new StopAtTombstone(databasePath + ".t1048g-ready"), new InMemoryAuditTrail(), new Ed25519Signer(KeyPair.Generate()),
            new SubjectErasureAutonomousRecoveryTests.ScriptedPropagator());
        service.EraseAsync(SubjectErasureAutonomousRecoveryTests.Request(SubjectErasureAutonomousRecoveryTests.Alice))
            .GetAwaiter().GetResult();
        Environment.Exit(3); // Unreachable: the tombstone write never returns.
    }

    private sealed class StopAtTombstone(string readyFile) : ISubjectTombstoneStore
    {
        public ValueTask<SubjectTombstone?> FindAsync(TenantId tenant, string pseudonym, CancellationToken ct = default) =>
            ValueTask.FromResult<SubjectTombstone?>(null);

        public ValueTask WriteAsync(SubjectTombstone tombstone, CancellationToken ct = default)
        {
            File.WriteAllText(readyFile, "at tombstone");
            // Deadline: a parent that never kills this process does not leave it running.
            Thread.Sleep(TimeSpan.FromMinutes(5));
            Environment.Exit(DeadlineExitCode);
            return ValueTask.CompletedTask;
        }
    }
}
