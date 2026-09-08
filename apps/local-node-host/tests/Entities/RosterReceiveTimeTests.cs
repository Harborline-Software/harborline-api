using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterReceiveTimeTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20);

    [Fact]
    public void ReceiveWindowHasStrictLowerBoundaryAndPreservesLegacyAndInWindowTimes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), NodeRosterRecord.ReceiveTimeWindow);
        Assert.Equal(At, NodeRosterRecord.BoundedOrderTime(At.AddSeconds(-30).AddMilliseconds(-1), At));
        Assert.Equal(At.AddSeconds(-30), NodeRosterRecord.BoundedOrderTime(At.AddSeconds(-30), At));
        Assert.Equal(At.AddSeconds(-10), NodeRosterRecord.BoundedOrderTime(At.AddSeconds(-10), At));
        Assert.Equal(At.AddSeconds(10), NodeRosterRecord.BoundedOrderTime(At.AddSeconds(10), At));
        Assert.Equal(At.AddDays(-1), NodeRosterRecord.BoundedOrderTime(At.AddDays(-1), null));
    }

    [Fact]
    public void SameDurableReceiptsConvergeAcross256SeededRecordPermutations()
    {
        const int seed = 295_003;
        var random = new Random(seed);
        using var founderKey = KeyPair.Generate();
        using var memberKey = KeyPair.Generate();
        var founder = new Ed25519Signer(founderKey);
        var member = new Ed25519Signer(memberKey);
        var verifier = new Ed25519Verifier();
        Guid Nonce() { var bytes = new byte[16]; random.NextBytes(bytes); return new Guid(bytes); }
        for (var sample = 0; sample < 256; sample++)
        {
            var tenant = Nonce();
            var roster = MemberRoster.Genesis(tenant, "founder", founder, verifier, At, Nonce())
                .Admit("founder", founder, "member", member.IssuerId, PermissionCompositions.Owner,
                    verifier, At.AddSeconds(1), Nonce());
            for (var i = 0; i < random.Next(1, 6); i++)
                roster = roster.Admit("founder", founder, $"guest-{i}", member.IssuerId, PermissionSet.Empty,
                    verifier, At.AddSeconds(2 + i), Nonce());
            var memberWins = sample % 2 == 0;
            var firstSigner = memberWins ? member : founder;
            var secondSigner = memberWins ? founder : member;
            var firstParty = memberWins ? "member" : "founder";
            var secondParty = memberWins ? "founder" : "member";
            var received = At.AddMinutes(2).AddSeconds(random.Next(60));
            var first = new MemberRevocationRecord(tenant.ToString("D"), secondParty,
                RosterSigning.SignRevocation(firstSigner, tenant, secondParty, firstParty,
                    received.AddSeconds(-random.Next(0, 30)), Nonce()));
            var backdated = new MemberRevocationRecord(tenant.ToString("D"), firstParty,
                RosterSigning.SignRevocation(secondSigner, tenant, firstParty, secondParty,
                    At.AddDays(-random.Next(1, 30)), Nonce()));
            var rows = roster.EnumerateAdmissions().Select(a => NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(a))).ToList();
            foreach (var row in rows) row.ReceivedAtUtc = row.IssuedAtUtc;
            var firstRow = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(first));
            firstRow.ReceivedAtUtc = received;
            var secondRow = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(backdated));
            secondRow.ReceivedAtUtc = received.AddSeconds(random.Next(1, 60));
            rows.AddRange([firstRow, secondRow]);
            string[] Fold(IEnumerable<NodeRosterRecord> arrivals)
            {
                var shuffled = arrivals.ToArray();
                var states = shuffled.Select(NodeRosterRecord.ToCrdtState).ToArray();
                var rebuilt = MemberRoster.FromSyncedRecords(
                    states.Select(s => s.ToAdmissionOrNull()).OfType<MemberAdmissionRecord>(),
                    states.Select(s => s.ToRevocationOrNull()).OfType<MemberRevocationRecord>(),
                    verifier, NodeRosterRecord.OrderTimes(shuffled));
                Assert.True(rebuilt.Contains(firstParty), $"seed={seed}, sample={sample}");
                Assert.False(rebuilt.Contains(secondParty), $"seed={seed}, sample={sample}");
                return rebuilt.EnumerateAdmissions().Where(a => rebuilt.Contains(a.PartyId))
                    .Select(a => a.PartyId).Order(StringComparer.Ordinal).ToArray();
            }
            Assert.Equal(Fold(rows.OrderBy(_ => random.Next())), Fold(rows.OrderBy(_ => random.Next())));
        }
    }

    [Fact]
    public async Task VerifierCacheEvictsPositiveAndNegativeProofsWithoutHydration()
    {
        using var key = KeyPair.Generate();
        var signer = new Ed25519Signer(key);
        var inner = new CountingVerifier();
        var cache = new HydrationRosterVerifier(inner);
        var valid = await signer.SignAsync("original", At, Guid.NewGuid());
        var invalid = valid with { Payload = "invalid" };
        Assert.True(cache.Verify(valid));
        Assert.False(cache.Verify(invalid));
        cache.Verify(valid);
        cache.Verify(invalid);
        Assert.Equal(2, inner.Calls);
        for (var i = 0; i < HydrationRosterVerifier.Capacity; i++) cache.Verify(valid with { Payload = $"churn-{i}" });
        var before = inner.Calls;
        Assert.True(cache.Verify(valid));
        Assert.False(cache.Verify(invalid));
        Assert.Equal(before + 2, inner.Calls);
        var proofs = (System.Collections.IDictionary)typeof(HydrationRosterVerifier)
            .GetField("_verified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        Assert.InRange(proofs.Count, 1, HydrationRosterVerifier.Capacity);
    }

    [Fact]
    public async Task ConcurrentFailureAndRecoveryPublishersLeaveAConsistentSnapshot()
    {
        await using var projection = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(),
            Substitute.For<IDbContextFactory<NodeLocalRosterDbContext>>(), new Ed25519Verifier(),
            NullLogger<RosterCrdtProjection>.Instance);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Task Failure(int i) => (Task)typeof(RosterCrdtProjection).GetMethod("ReportRebuildFailureAsync", flags)!
            .Invoke(projection, [new InvalidOperationException($"fault-{i}"), $"roster.rebuild.test-{i}", CancellationToken.None])!;
        Task Recovery() => (Task)typeof(RosterCrdtProjection).GetMethod("ClearRebuildFailureAsync", flags)!
            .Invoke(projection, [CancellationToken.None])!;
        await Failure(-1);
        Assert.Equal(projection.RebuildFailure, Assert.Single(projection.RefusalReports));
        for (var i = 0; i < 256; i++)
        {
            await Task.WhenAll(Task.Run(() => Failure(i)), Task.Run(Recovery), Task.Run(() => Failure(i + 1)));
            Assert.Equal(projection.RebuildFailure is { } failure ? [failure] : [], projection.RefusalReports);
        }
        // A failure copies the reports while recovery publishes the existing array. A large
        // snapshot exposes a late failure publisher overwriting a completed recovery publication.
        var report = projection.RebuildFailure;
        if (report is null) { await Failure(-2); report = projection.RebuildFailure; }
        var reports = Enumerable.Repeat(report!, 100_000).ToArray();
        typeof(RosterCrdtProjection).GetField("_refusalReports", flags)!.SetValue(projection, reports);
        await Recovery();
        for (var i = 0; i < 64; i++)
        {
            var publishing = Task.Run(() => Failure(i));
            SpinWait.SpinUntil(() => projection.RebuildFailure is not null || publishing.IsCompleted);
            await Recovery();
            await publishing;
            Assert.Null(projection.RebuildFailure);
            Assert.Equal(reports.Length, projection.RefusalReports.Count);
        }
        typeof(RosterCrdtProjection).GetField("_refusalReports", flags)!.SetValue(projection, Array.Empty<Harborline.Api.Foundation.Authorization.AuthorizationRefusal>());
        var retained = projection.RefusalReports;
        await Recovery();
        Assert.Empty(projection.RefusalReports);
        Assert.NotSame(retained, projection.RefusalReports);
    }

    [Fact]
    public async Task NumberedMigrationRetainsReceiptOnDiskAndLeavesLegacyReceiptUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roster-receipt-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new NodeLocalRosterDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Contains("20260908123000_RosterReceiveTime", await db.Database.GetAppliedMigrationsAsync());
                using var key = KeyPair.Generate();
                var root = MemberRoster.Genesis(Guid.NewGuid(), "founder", new Ed25519Signer(key),
                    new Ed25519Verifier(), At, Guid.NewGuid());
                var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(root.EnumerateAdmissions().Single()));
                Assert.Null(row.ReceivedAtUtc);
                row.ReceivedAtUtc = At.AddDays(1).AddMilliseconds(123);
                db.RosterRecords.Add(row);
                await db.SaveChangesAsync();
            }
            await using (var restarted = new NodeLocalRosterDbContext(options))
            {
                await restarted.Database.MigrateAsync();
                var row = await restarted.RosterRecords.SingleAsync();
                Assert.Equal(At, row.IssuedAtUtc);
                Assert.Equal(At.AddDays(1).AddMilliseconds(123), row.ReceivedAtUtc);
                Assert.Equal(row.ReceivedAtUtc, NodeRosterRecord.BoundedOrderTime(row.IssuedAtUtc, row.ReceivedAtUtc));
            }
        }
        finally { File.Delete(path); }
    }

    private sealed class CountingVerifier : IOperationVerifier
    {
        public int Calls;
        public bool Verify<T>(SignedOperation<T> op) { Calls++; return new Ed25519Verifier().Verify(op); }
    }
}
