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

    internal static SubjectErasureService Service(
        SearchTestStore store,
        ISubjectTombstoneStore tombstones,
        IAuditTrail trail,
        IOperationSigner signer,
        ISubjectErasurePropagator propagator,
        IRecoveryClock? clock = null) => new(
        new NodeEfSubjectErasureRegistry(store.Factory, TimeProvider.System),
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

        public Task PropagateErasureAsync(TenantId tenant, SubjectId subject, CancellationToken ct) =>
            FailAll || Failing.Contains(subject.Value)
                ? throw new InvalidOperationException("T-1048 injected purge fault")
                : Task.CompletedTask;
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
