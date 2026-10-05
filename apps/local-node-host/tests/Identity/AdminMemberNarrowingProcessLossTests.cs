using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed partial class AdminTeamAccessAuthorityTests
{
    internal const string NarrowingChildDirectoryVariable = "HARBORLINE_T1048H_NARROWING_CHILD_DIRECTORY";
    private const int NarrowingChildDeadlineExitCode = 4;

    [Fact]
    public async Task KilledMemberNarrowingProcess_StartupDrainDeliversAllThreeCommittedAuditsExactlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-t1048h-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "committed.json");
        var readyPath = Path.Combine(directory, "ready");
        NarrowingChildState? state = null;
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(AdminTeamAccessAuthorityTests).Assembly.Location);
        start.Environment[NarrowingChildDirectoryVariable] = directory;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The narrowing child did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!File.Exists(readyPath))
            {
                if (child.HasExited) Assert.Fail("The narrowing child exited before delivery: " + await stdout + await stderr);
                Assert.True(DateTime.UtcNow < deadline, "The narrowing child did not reach post-commit delivery within 120s.");
                await Task.Delay(50);
            }
            state = JsonSerializer.Deserialize<NarrowingChildState>(await File.ReadAllTextAsync(manifestPath))!;
            var factory = ContextFactory<NodeLocalSearchDbContext>.Create(state.DatabasePaths[2],
                options => new NodeLocalSearchDbContext(options), NodeLocalSearchDbContext.MigrationsHistoryTableName);

            // Prove the transaction is committed while the actual narrowing process is still alive and no delivery ran.
            await using (var db = factory.CreateDbContext())
            {
                var original = await db.Grants.AsNoTracking().SingleAsync(row => row.GrantId == state.OriginalGrantId);
                Assert.NotNull(original.RevokedAtUnixMs);
                Assert.Single(await db.Grants.AsNoTracking().Where(row => row.SubjectId == "principal-narrowed" && row.RevokedAtUnixMs == null).ToListAsync());
                Assert.Equal((state.SubjectEpochBefore ?? 0) + 1, (await db.GrantAuthorizationEpochs.AsNoTracking()
                    .SingleAsync(row => row.TenantId == TenantId && row.PrincipalId == "principal-narrowed")).AuthorizationEpoch);
                var pending = await db.AuditOutbox.AsNoTracking().Where(row => row.PublishedAtUnixMs == null).ToListAsync();
                Assert.Equal(new[] { "AuthorizationAdmissionGrantConferred", "CapabilityDelegated", "CapabilityRevoked" },
                    pending.Select(row => row.EventType).Order(StringComparer.Ordinal));
                Assert.Equal(3, pending.Select(row => row.AuditId).Distinct().Count());
            }

            child.Kill(entireProcessTree: true);
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync(exit.Token);
            Assert.NotEqual(NarrowingChildDeadlineExitCode, child.ExitCode);

            var first = await RecoverNarrowingAtStartupAsync(factory, state);
            Assert.Equal(new[] { "AuthorizationAdmissionGrantConferred", "CapabilityDelegated", "CapabilityRevoked" },
                first.Select(record => record.EventType.Value).Order(StringComparer.Ordinal));
            Assert.All(first, record =>
            {
                Assert.Equal(Now, record.OccurredAt);
                Assert.Equal(new ActorId("principal-admin"), record.Actor);
                Assert.NotNull(record.AuthoritySnapshot);
            });
            string successor;
            await using (var db = factory.CreateDbContext())
                successor = (await db.Grants.AsNoTracking().SingleAsync(row => row.SubjectId == "principal-narrowed" && row.RevokedAtUnixMs == null)).GrantId;
            var legs = first.Where(record => record.EventType.Value != "AuthorizationAdmissionGrantConferred").ToList();
            Assert.Equal(2, legs.Count);
            Assert.All(legs, record =>
            {
                Assert.Equal(state.OriginalGrantId, record.Target!.Value.RecordId);
                Assert.Equal("member-narrowed", record.Payload.Payload.Body["reason"]?.ToString());
                Assert.Equal(successor, record.Payload.Payload.Body["successor_grant_id"]?.ToString());
            });
            Assert.Single(legs.Select(record => record.Payload.Payload.Body["correlation_id"]?.ToString()).Distinct());

            // Another fresh startup over the same file cannot add a second copy of any audit leg.
            var second = await RecoverNarrowingAtStartupAsync(factory, state);
            Assert.Equal(first.Select(record => record.AuditId).Order(), second.Select(record => record.AuditId).Order());
            await using var final = factory.CreateDbContext();
            Assert.Equal(0, await final.AuditOutbox.CountAsync(row => row.PublishedAtUnixMs == null));
            Assert.Equal(4, await final.AuditOutbox.CountAsync()); // setup conferral + exactly three narrowing records
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await child.WaitForExitAsync(exit.Token);
            }
            if (state is null && File.Exists(manifestPath))
                state = JsonSerializer.Deserialize<NarrowingChildState>(await File.ReadAllTextAsync(manifestPath));
            if (state is not null)
                foreach (var database in state.DatabasePaths)
                {
                    var full = Path.GetFullPath(database);
                    Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full));
                    Assert.StartsWith("admin-team-", Path.GetFileName(full));
                    File.Delete(full);
                    File.Delete(full + "-wal");
                    File.Delete(full + "-shm");
                }
            File.Delete(readyPath);
            File.Delete(manifestPath);
            Directory.Delete(directory);
        }
    }

    private static async Task<List<AuditRecord>> RecoverNarrowingAtStartupAsync(
        ContextFactory<NodeLocalSearchDbContext> factory, NarrowingChildState state)
    {
        var trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(factory));
        using var outbox = new NodeAuditOutbox(factory, trail, trail, new Ed25519Signer(KeyPair.Generate()),
            TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
        using var daemon = new NodeAuditOutboxDrainDaemon(outbox, TimeProvider.System, NullLogger<NodeAuditOutboxDrainDaemon>.Instance);
        await daemon.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                await using var db = factory.CreateDbContext();
                if (!await db.AuditOutbox.AnyAsync(row => row.PublishedAtUnixMs == null)) break;
                Assert.True(DateTime.UtcNow < deadline, "The fresh startup did not drain the narrowing audit within 60s.");
                await Task.Delay(50);
            }
        }
        finally { await daemon.StopAsync(CancellationToken.None); }
        var records = new List<AuditRecord>();
        await foreach (var record in trail.QueryAsync(new AuditQuery(new TenantId(TenantId))))
            if (!state.SetupAuditIds.Contains(record.AuditId)) records.Add(record);
        return records;
    }

    internal static async Task RunMemberNarrowingChildAsync(string directory)
    {
        await using var fixture = await Fixture.CreateAsync(PermissionCompositions.Admin,
            memberRoleSet: NarrowedMemberSet, grantAudit: new StopBeforeNarrowingDelivery(Path.Combine(directory, "ready")));
        var original = await ConferNarrowableGrantAsync(fixture);
        var setup = await RestartAndDrainTwiceAsync(fixture);
        long? epoch;
        await using (var db = fixture.GrantFactory.CreateDbContext())
            epoch = (await db.GrantAuthorizationEpochs.AsNoTracking()
                .Where(row => row.TenantId == TenantId && row.PrincipalId == "principal-narrowed")
                .Select(row => (long?)row.AuthorizationEpoch).SingleOrDefaultAsync());
        var state = new NarrowingChildState(fixture.DatabasePaths.ToArray(), original, epoch, setup.Select(record => record.AuditId).ToArray());
        // The child entry runs during module initialization. Keep this owned signal synchronous so an I/O
        // continuation cannot wait for the module initializer that is waiting for the child operation.
        File.WriteAllText(Path.Combine(directory, "committed.json"), JsonSerializer.Serialize(state));
        await fixture.Authority.NarrowMemberGrantAsync(fixture.Handle, TenantId, original, [TeamRolePermissions.MembersManage],
            new AuthorizationWriteContext(new ActorId("principal-admin"), new TenantId(TenantId), Now));
        throw new InvalidOperationException("The narrowing child returned without pausing before delivery.");
    }

    private sealed record NarrowingChildState(string[] DatabasePaths, string OriginalGrantId, long? SubjectEpochBefore, Guid[] SetupAuditIds);

    private sealed class StopBeforeNarrowingDelivery(string readyPath) : IAuthorizedAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) =>
            throw new InvalidOperationException("Narrowing must deliver through the admitted decision.");
        public ValueTask AppendAuthorizedAsync(AuditRecord record, AuthorizationDecision decision,
            CancellationToken ct = default, SeparationOfDutyDecision? approval = null)
        {
            File.WriteAllText(readyPath, "after narrowing commit, before audit delivery");
            Thread.Sleep(TimeSpan.FromMinutes(5));
            Environment.Exit(NarrowingChildDeadlineExitCode);
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

internal static class MemberNarrowingChildProcess
{
    [ModuleInitializer]
    internal static void RunWhenChild()
    {
        var directory = Environment.GetEnvironmentVariable(AdminTeamAccessAuthorityTests.NarrowingChildDirectoryVariable);
        if (string.IsNullOrEmpty(directory)) return;
        try { AdminTeamAccessAuthorityTests.RunMemberNarrowingChildAsync(directory).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception);
            Environment.Exit(3);
        }
    }
}
