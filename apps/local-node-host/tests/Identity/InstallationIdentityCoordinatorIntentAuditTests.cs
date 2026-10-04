using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// T-1048 (DES-0029 ck-6, T-724 ruling 123): the membership saga is <c>intent-audit</c>. Its home decision
/// commits before the tenant finalize; the move to Completed with its installation envelope is one
/// serializable transaction; the recovery drain rolls an owed home forward without the client. Each test
/// pins one of ruling 123's five conditions. Oracles are the durable home row, the envelope chain and the
/// fixed clock.
/// </summary>
public sealed partial class InstallationIdentityCoordinatorServiceTests
{
    internal const string MembershipChildDirectoryVariable = "HARBORLINE_T1048_MEMBERSHIP_CHILD_DIRECTORY";
    private const int MembershipChildDeadlineExitCode = 4;
    private const string CompletedEvent = "TenantMembershipCoordinationCompleted";

    [Fact(DisplayName = "T-1048 ruling 123 (a): the home decision is durably Committing before any tenant finalize runs")]
    public async Task Home_decision_commits_before_the_tenant_effect()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(2);
        var observed = new List<InstallationIdentityCoordinatorState>();
        foreach (var tenant in fixture.Tenants)
            tenant.Authority = new ObserveHomeAtFinalizeStore(tenant.Authority, fixture.HomeFactory, observed);

        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));

        Assert.Equal(InstallationIdentityCoordinationStatus.Completed, result.Status);
        // The first finalize per tenant is the effect; Finalizing re-reads each receipt idempotently afterwards.
        Assert.Equal([InstallationIdentityCoordinatorState.Committing, InstallationIdentityCoordinatorState.Committing], observed.Take(2));
        Assert.All(observed, state => Assert.True(state is InstallationIdentityCoordinatorState.Committing
            or InstallationIdentityCoordinatorState.Finalizing, $"a tenant finalize ran while the home was {state}"));
    }

    [Fact(DisplayName = "T-1048 ruling 123 (c): an owed home days past any deadline is never abandoned; it stays fenced and owed, then completes once")]
    public async Task Owed_home_is_never_abandoned_on_a_deadline()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(1);
        var tenant = fixture.Tenants[0];
        var authority = tenant.Authority;
        tenant.Authority = new ThrowAfterFinalizeOnceStore(authority);
        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery,
            (await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants))).Status);
        tenant.Authority = authority;

        // The tenant is unreachable for three days of passes, far beyond the 30-second lease and any backoff.
        var clock = new ManualPeriodicTimeProvider(FixedNow);
        var coordinator = fixture.CoordinatorAt(clock);
        var recovery = new InstallationIdentityCoordinatorRecoveryService(fixture.HomeFactory, coordinator);
        tenant.Leases = new UnavailableLeaseCoordinator();
        for (var pass = 0; pass < 6; pass++)
        {
            clock.Advance(TimeSpan.FromHours(12));
            Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery,
                Assert.Single(await recovery.RecoverPendingAsync()).Status);
        }
        await using (var db = fixture.HomeFactory.CreateDbContext())
        {
            Assert.Equal(InstallationIdentityCoordinatorState.Committing,
                (await db.Coordinators.AsNoTracking().SingleAsync()).State);
            Assert.Equal(1, await db.AuditEnvelopes.CountAsync());
        }
        Assert.True(await coordinator.IsAccountFencedAsync(fixture.AccountId));

        // The tenant comes back; its leases run on the same clock as the coordinator.
        tenant.Leases = new ClockLeaseCoordinator(clock);
        Assert.Equal(InstallationIdentityCoordinationStatus.Completed,
            Assert.Single(await recovery.RecoverPendingAsync()).Status);
        await using var final = fixture.HomeFactory.CreateDbContext();
        Assert.Equal(InstallationIdentityCoordinatorState.Completed, (await final.Coordinators.AsNoTracking().SingleAsync()).State);
        Assert.Equal(1, await final.AuditEnvelopes.CountAsync(row => row.EventType == CompletedEvent));
    }

    [Fact(DisplayName = "T-1048 ruling 123 (d): a permanently failing home backs off, doubling and never giving up, and never blocks newer homes")]
    public async Task Permanently_failing_home_backs_off_without_blocking_newer_homes()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(2);
        var failing = fixture.Tenants[0];
        var healthy = fixture.Tenants[1];
        var failingAuthority = failing.Authority;
        var healthyAuthority = healthy.Authority;
        var account = new CoordinatorFixture.ActorVersion(fixture.AccountId, fixture.AccountOwnerVersion, fixture.AccountSecurityVersion);

        // The oldest owed home: its tenant keeps losing the finalize response, so every resume stays pending.
        failing.Authority = new ThrowAfterFinalizeOnceStore(failingAuthority);
        var stuck = fixture.CommandForAccount(account, [failing], "a-stuck", "stuck", "stuck");
        Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, (await fixture.Coordinator.ExecuteAsync(stuck)).Status);
        failing.Authority = new AlwaysFailAfterFinalizeStore(failingAuthority);

        var clock = new ManualPeriodicTimeProvider(FixedNow);
        var recovery = new InstallationIdentityCoordinatorRecoveryService(
            fixture.HomeFactory, fixture.Coordinator, timeProvider: clock);
        var newer = 0;
        async Task<string> OwedNewerHomeAsync()
        {
            var actor = await fixture.CreateActiveActorAsync();
            healthy.Authority = new ThrowAfterFinalizeOnceStore(healthyAuthority);
            var command = fixture.CommandForAccount(actor, [healthy], $"b-newer-{newer++:D2}", $"newer-{newer}", $"newer-{newer}");
            Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery, (await fixture.Coordinator.ExecuteAsync(command)).Status);
            healthy.Authority = healthyAuthority;
            return command.CorrelationId;
        }

        // Seconds after each attempt: the stuck home is next due 1, 2, 4, 8 ... then at most 60 minutes later.
        var attemptedAt = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        foreach (var step in new[] { 0, 30, 31, 60, 61, 120, 121, 240, 241, 3600, 3601, 3601 })
        {
            var advance = TimeSpan.FromSeconds(step);
            clock.Advance(advance);
            elapsed += advance;
            var owed = await OwedNewerHomeAsync();
            var results = await recovery.RecoverPendingAsync(limit: 1);
            // limit 1 still reaches the newer home: the stuck home never holds the page.
            results = [.. results, .. await recovery.RecoverPendingAsync(limit: 1)];
            Assert.Contains(results, result => result.CorrelationId == owed && result.Status == InstallationIdentityCoordinationStatus.Completed);
            if (results.Any(result => result.CorrelationId == stuck.CorrelationId))
            {
                Assert.Equal(InstallationIdentityCoordinationStatus.PendingRecovery,
                    results.First(result => result.CorrelationId == stuck.CorrelationId).Status);
                attemptedAt.Add(elapsed);
            }
        }

        // Attempts at 0s, 61s (+1m), 182s (+2m), 423s (+4m), then still retried after hours: never given up.
        Assert.Equal(new[] { 0, 61, 182, 423 }.Select(seconds => TimeSpan.FromSeconds(seconds)), attemptedAt.Take(4));
        Assert.True(attemptedAt.Count >= 5, "the stuck home must still be retried once its longer backoff is due");
        await using var db = fixture.HomeFactory.CreateDbContext();
        Assert.Equal(InstallationIdentityCoordinatorState.Committing,
            (await db.Coordinators.AsNoTracking().SingleAsync(row => row.CorrelationId == stuck.CorrelationId)).State);
        Assert.Equal(0, await db.AuditEnvelopes.CountAsync(row => row.CorrelationId == stuck.CorrelationId));
        Assert.Equal(newer, await db.AuditEnvelopes.CountAsync(row => row.EventType == CompletedEvent));
    }

    [Fact(DisplayName = "T-1048 backoff: one minute, doubling per attempt, capped at an hour")]
    public void Membership_recovery_backoff_doubles_to_an_hour()
    {
        Assert.Equal(
            new[] { 1, 2, 4, 8, 16, 32, 60, 60 }.Select(minutes => TimeSpan.FromMinutes(minutes)),
            Enumerable.Range(1, 8).Select(InstallationIdentityCoordinatorRecoveryService.MembershipRecoveryBackoff));
    }

    [Fact(DisplayName = "T-1048 ruling 123 (b, e): a killed process after the tenant finalize is recovered by a fresh host exactly once")]
    public async Task Killed_membership_process_is_recovered_exactly_once_by_a_fresh_host()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-t1048-membership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "committed.json");
        var readyPath = Path.Combine(directory, "ready");
        MembershipChildState? state = null;
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(InstallationIdentityCoordinatorServiceTests).Assembly.Location);
        start.Environment[MembershipChildDirectoryVariable] = directory;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The membership child did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        CoordinatorFixture? restarted = null;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!File.Exists(readyPath))
            {
                if (child.HasExited) Assert.Fail("The membership child exited before the tenant finalize: " + await stdout + await stderr);
                Assert.True(DateTime.UtcNow < deadline, "The membership child did not reach the tenant finalize within 120s.");
                await Task.Delay(50);
            }
            state = JsonSerializer.Deserialize<MembershipChildState>(await File.ReadAllTextAsync(manifestPath))!;

            child.Kill(entireProcessTree: true);
            using (var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                await child.WaitForExitAsync(exit.Token);
            Assert.NotEqual(MembershipChildDeadlineExitCode, child.ExitCode);

            restarted = await CoordinatorFixture.ReopenAsync(state.HomePath, state.AccountId, state.OwnerVersion,
                state.SecurityVersion, [(state.TenantPath, Convert.FromHexString(state.TenantKeyHex), state.TenantId)]);
            // What survived the kill: the tenant effect is durable, the home owes its envelope.
            Assert.NotNull(await restarted.Tenants[0].Authority.GetMembershipAsync(state.AccountId, CancellationToken.None));
            await using (var db = restarted.HomeFactory.CreateDbContext())
            {
                Assert.Equal(InstallationIdentityCoordinatorState.Committing, (await db.Coordinators.AsNoTracking().SingleAsync()).State);
                Assert.Equal(0, await db.AuditEnvelopes.CountAsync(row => row.EventType == CompletedEvent));
            }
            Assert.True(await restarted.Coordinator.IsAccountFencedAsync(state.AccountId));

            // A fresh host's drain alone delivers it, and a second pass adds nothing.
            var recovery = new InstallationIdentityCoordinatorRecoveryService(restarted.HomeFactory, restarted.Coordinator);
            Assert.Equal(InstallationIdentityCoordinationStatus.Completed, Assert.Single(await recovery.RecoverPendingAsync()).Status);
            Assert.Empty(await recovery.RecoverPendingAsync());
            await using (var db = restarted.HomeFactory.CreateDbContext())
            {
                var home = await db.Coordinators.AsNoTracking().SingleAsync();
                Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
                var completion = await db.AuditEnvelopes.AsNoTracking().SingleAsync(row => row.EventType == CompletedEvent);
                Assert.Equal("membership-command", completion.CorrelationId);
                Assert.Equal(FixedNow, completion.OccurredAtUtc);
                Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(completion));
                Assert.Equal(2, await db.AuditEnvelopes.CountAsync());
            }
            Assert.False(await restarted.Coordinator.IsAccountFencedAsync(state.AccountId));
            Assert.NotNull(await restarted.Coordinator.ResolveUsableMembershipAsync(state.AccountId, state.TenantId));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await child.WaitForExitAsync(exit.Token);
            }
            if (restarted is not null) await restarted.DisposeAsync();
            else if (state is not null)
            {
                File.Delete(state.HomePath);
                File.Delete(state.TenantPath);
            }
            if (state is not null)
                foreach (var path in new[] { state.HomePath, state.TenantPath })
                {
                    File.Delete(path + "-wal");
                    File.Delete(path + "-shm");
                }
            File.Delete(readyPath);
            File.Delete(manifestPath);
            Directory.Delete(directory);
        }
    }

    internal static async Task RunMembershipChildAsync(string directory)
    {
        var fixture = await CoordinatorFixture.CreateAsync(1);
        var tenant = fixture.Tenants[0];
        var state = new MembershipChildState(fixture.HomePath, fixture.AccountId, fixture.AccountOwnerVersion,
            fixture.AccountSecurityVersion, tenant.Path, Convert.ToHexString(tenant.Key), tenant.TenantId);
        // The child entry runs during module initialization. Keep the signal synchronous so an I/O continuation
        // cannot wait for the module initializer that is waiting for this operation.
        File.WriteAllText(Path.Combine(directory, "committed.json"), JsonSerializer.Serialize(state));
        tenant.Authority = new StopAfterFinalizeStore(tenant.Authority, Path.Combine(directory, "ready"));
        await fixture.Coordinator.ExecuteAsync(fixture.Command(fixture.Tenants));
        throw new InvalidOperationException("The membership child returned without stopping after the tenant finalize.");
    }

    private sealed record MembershipChildState(string HomePath, string AccountId, long OwnerVersion, long SecurityVersion,
        string TenantPath, string TenantKeyHex, string TenantId);

    /// <summary>Lets the tenant finalize commit, then holds the process until the parent kills it.</summary>
    private sealed class StopAfterFinalizeStore(ITenantMembershipAuthorityStore inner, string readyPath)
        : DelegatingMembershipStore(inner)
    {
        public override async Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
        {
            await Inner.FinalizeAsync(correlationId, commandFingerprint, occurredAtUtc, cancellationToken);
            File.WriteAllText(readyPath, "after the tenant finalize commit, before the home receipt and envelope");
            Thread.Sleep(TimeSpan.FromMinutes(5));
            Environment.Exit(MembershipChildDeadlineExitCode);
            throw new UnreachableException();
        }
    }

    /// <summary>Grants leases on the given clock, so a test can advance time past the fixed-clock fixture.</summary>
    private sealed class ClockLeaseCoordinator(TimeProvider clock) : ILeaseCoordinator
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var lease = new Lease(Guid.NewGuid().ToString("N"), resourceId, "test-node", now, now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) => _held.Values.Any(item => item.ResourceId == resourceId);
        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records the durable home state at the instant each tenant finalize is about to run.</summary>
    private sealed class ObserveHomeAtFinalizeStore(
        ITenantMembershipAuthorityStore inner,
        InstallationFounderBootstrapServiceTests.IdentityContextFactory homeFactory,
        List<InstallationIdentityCoordinatorState> observed) : DelegatingMembershipStore(inner)
    {
        public override async Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
        {
            await using (var db = homeFactory.CreateDbContext())
                observed.Add((await db.Coordinators.AsNoTracking().SingleAsync(row => row.CorrelationId == correlationId,
                    cancellationToken)).State);
            return await Inner.FinalizeAsync(correlationId, commandFingerprint, occurredAtUtc, cancellationToken);
        }
    }
}

internal static class MembershipCoordinatorChildProcess
{
    [ModuleInitializer]
    internal static void RunWhenChild()
    {
        var directory = Environment.GetEnvironmentVariable(InstallationIdentityCoordinatorServiceTests.MembershipChildDirectoryVariable);
        if (string.IsNullOrEmpty(directory)) return;
        try { InstallationIdentityCoordinatorServiceTests.RunMembershipChildAsync(directory).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception);
            Environment.Exit(3);
        }
    }
}
