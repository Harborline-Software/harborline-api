using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Harborline.Api.Foundation.Assets.Common;
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
            var rows = roster.EnumerateAdmissions().Select(a => NodeRosterRecord.FromCrdtState(
                RosterRecordCrdtState.FromAdmission(a).AttestReceipt(founder, "founder", a.Admission.IssuedAt))).ToList();
            var firstRow = NodeRosterRecord.FromCrdtState(
                RosterRecordCrdtState.FromRevocation(first).AttestReceipt(founder, "founder", received));
            var secondRow = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(backdated)
                .AttestReceipt(founder, "founder", received.AddSeconds(random.Next(1, 60))));
            rows.AddRange([firstRow, secondRow]);
            string[] Fold(IEnumerable<NodeRosterRecord> arrivals)
            {
                var shuffled = arrivals.ToArray();
                var states = shuffled.Select(NodeRosterRecord.ToCrdtState).ToArray();
                Assert.All(states, state => Assert.True(RosterReceiveAttestationSigning.Verify(
                    state.RecordId, Guid.Parse(state.NonceGuid), state.ReceiveAttestationOrNull()!, verifier)));
                var rebuilt = MemberRoster.FromSyncedRecords(
                    states.Select(s => s.ToAdmissionOrNull()).OfType<MemberAdmissionRecord>(),
                    states.Select(s => s.ToRevocationOrNull()).OfType<MemberRevocationRecord>(),
                    verifier, NodeRosterRecord.OrderTimes(shuffled),
                    // "member" was admitted as an owner locally, so the grant store holds that for it; the
                    // replay reads the revoker's authority from there, not from the record.
                    new TestRosterAuthority(("member", PermissionCompositions.Owner)));
                Assert.True(rebuilt.Contains(firstParty), $"seed={seed}, sample={sample}");
                Assert.False(rebuilt.Contains(secondParty), $"seed={seed}, sample={sample}");
                return rebuilt.EnumerateAdmissions().Where(a => rebuilt.Contains(a.PartyId))
                    .Select(a => a.PartyId).Order(StringComparer.Ordinal).ToArray();
            }
            Assert.Equal(Fold(rows.OrderBy(_ => random.Next())), Fold(rows.OrderBy(_ => random.Next())));
        }
    }

    [Fact]
    public async Task OldShapeSignatureDoesNotVerifyAgainstCurrentCanonicalReceipt()
    {
        using var key = KeyPair.Generate();
        var signer = new Ed25519Signer(key);
        var nonce = Guid.NewGuid();
        var old = await signer.SignAsync(new { FormatVersion = 1, RecordId = "record" }, At, nonce);
        var current = new RosterReceiveAttestationRecord(RosterWireFormat.CurrentVersion, "founder",
            signer.IssuerId.ToBase64Url(), "record", At.ToString("O"));
        Assert.Equal(["FormatVersion", "NodePartyId", "NodePublicKey", "ReceivedAtIso", "RecordId"],
            typeof(RosterReceiveAttestationRecord).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.False(new Ed25519Verifier().Verify(new SignedOperation<RosterReceiveAttestationRecord>(
            current, signer.IssuerId, At, nonce, old.Signature)));
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

        var bounded = new HydrationRosterVerifier(inner);
        SignedOperation<string>? survivor = null;
        for (var i = 0; i < HydrationRosterVerifier.Capacity; i++)
        {
            var proof = valid with { Payload = $"bounded-{i}" };
            bounded.Verify(proof);
            if (i == 1) survivor = proof;
        }
        bounded.Verify(valid with { Payload = "one-over-capacity" });
        var retainedCalls = inner.Calls;
        bounded.Verify(survivor!);
        Assert.Equal(retainedCalls, inner.Calls);
    }

    [Fact]
    public async Task ConcurrentFailureAndRecoveryPublishersLeaveAConsistentSnapshot()
    {
        await using var projection = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(),
            Substitute.For<IDbContextFactory<NodeLocalRosterDbContext>>(), new Ed25519Verifier(),
            new Ed25519Signer(KeyPair.Generate()),
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
    public async Task ReadAtAndInMemoryFoldAgreeForRevocationAtReceiptWindowBoundary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roster-boundary-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        using var founderKey = KeyPair.Generate();
        using var memberKey = KeyPair.Generate();
        var founder = new Ed25519Signer(founderKey);
        var verifier = new Ed25519Verifier();
        var tenant = Guid.NewGuid();
        var roster = MemberRoster.Genesis(tenant, "founder", founder, verifier, At, Guid.NewGuid())
            .Admit("founder", founder, "member", memberKey.PrincipalId, PermissionCompositions.Member,
                verifier, At.AddSeconds(1), Guid.NewGuid());
        var received = At.AddHours(2);
        var boundary = received - NodeRosterRecord.ReceiveTimeWindow;
        var revocation = new MemberRevocationRecord(tenant.ToString("D"), "member",
            RosterSigning.SignRevocation(founder, tenant, "member", "founder", boundary, Guid.NewGuid()));
        var states = roster.EnumerateAdmissions()
            .Select(admission => RosterRecordCrdtState.FromAdmission(admission)
                .AttestReceipt(founder, "founder", admission.Admission.IssuedAt))
            .Append(RosterRecordCrdtState.FromRevocation(revocation)
                .AttestReceipt(founder, "founder", received))
            .ToArray();
        var rows = states.Select(NodeRosterRecord.FromCrdtState).ToArray();

        try
        {
            await using (var db = new NodeLocalRosterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.RosterRecords.AddRange(rows);
                await db.SaveChangesAsync();
            }

            var orderTime = NodeRosterRecord.OrderTimes(rows);
            var included = states.Where(state =>
                orderTime(state.SignatureB64Url, NodeRosterRecord.FromCrdtState(state).IssuedAtUtc) <= boundary);
            var folded = MemberRoster.FromSyncedRecords(
                included.Select(state => state.ToAdmissionOrNull()).OfType<MemberAdmissionRecord>(),
                included.Select(state => state.ToRevocationOrNull()).OfType<MemberRevocationRecord>(),
                verifier, orderTime);
            var read = await new VerifiedTenantRosterReader(new InlineFactory(options), verifier)
                .ReadAtAsync(new TenantId(tenant.ToString("D")), boundary, default);

            Assert.Equal(folded.Contains("member"), read.Contains("member"));
            Assert.False(read.Contains("member"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadAtRefusesNonCurrentWireFormatInsteadOfSkippingItsFutureReceipt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roster-version-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        using var founderKey = KeyPair.Generate();
        var founder = new Ed25519Signer(founderKey);
        var verifier = new Ed25519Verifier();
        var tenant = Guid.NewGuid();
        var roster = MemberRoster.Genesis(tenant, "founder", founder, verifier, At, Guid.NewGuid());
        var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState
            .FromAdmission(roster.EnumerateAdmissions().Single())
            .AttestReceipt(founder, "founder", At.AddDays(1)));
        row.WireFormatVersion = 0;

        try
        {
            await using (var db = new NodeLocalRosterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.RosterRecords.Add(row);
                await db.SaveChangesAsync();
            }

            var exception = await Assert.ThrowsAsync<VerifiedTenantRosterRefusedException>(() =>
                new VerifiedTenantRosterReader(new InlineFactory(options), verifier)
                    .ReadAtAsync(new TenantId(tenant.ToString("D")), At.AddDays(-1), default));
            Assert.Equal(VerifiedTenantRosterRefusal.WireVersionUnsupported, exception.Refusal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NumberedMigrationRetainsReceiptOnDiskAndQueriesAsOfReceiptCandidates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roster-receipt-{Guid.NewGuid():N}.db");
        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").LogTo(commands.Add).Options;
        try
        {
            await using (var db = new NodeLocalRosterDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Contains("20260908123000_RosterReceiveTime", await db.Database.GetAppliedMigrationsAsync());
                using var key = KeyPair.Generate();
                var root = MemberRoster.Genesis(Guid.NewGuid(), "founder", new Ed25519Signer(key),
                    new Ed25519Verifier(), At, Guid.NewGuid());
                var signer = new Ed25519Signer(key);
                var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(root.EnumerateAdmissions().Single())
                    .AttestReceipt(signer, "founder", At.AddDays(1).AddMilliseconds(123)));
                Assert.NotNull(row.ReceivedAtUtc);
                db.RosterRecords.Add(row);
                await db.SaveChangesAsync();
            }
            await using (var restarted = new NodeLocalRosterDbContext(options))
            {
                await restarted.Database.MigrateAsync();
                Assert.Contains("20260908150000_RosterReceiveAttestation", await restarted.Database.GetAppliedMigrationsAsync());
                var row = await restarted.RosterRecords.SingleAsync();
                Assert.Equal(At, row.IssuedAtUtc);
                Assert.Equal(At.AddDays(1).AddMilliseconds(123), row.ReceivedAtUtc);
                Assert.Equal(RosterWireFormat.CurrentVersion, row.WireFormatVersion);
                var state = NodeRosterRecord.ToCrdtState(row);
                Assert.True(RosterReceiveAttestationSigning.Verify(state.RecordId, Guid.Parse(state.NonceGuid),
                    state.ReceiveAttestationOrNull()!, new Ed25519Verifier()));
                Assert.Equal(row.ReceivedAtUtc, NodeRosterRecord.BoundedOrderTime(row.IssuedAtUtc, row.ReceivedAtUtc));
                row.WireFormatVersion = 0;
                Assert.Equal(row.IssuedAtUtc, NodeRosterRecord.OrderTimes([row])(row.SignatureB64Url, row.IssuedAtUtc));
                row.WireFormatVersion = RosterWireFormat.CurrentVersion;
                await new VerifiedTenantRosterReader(new InlineFactory(options), new Ed25519Verifier())
                    .ReadAtAsync(new TenantId(row.TeamId), At.AddDays(2), default);
                Assert.Contains(commands, command => command.Contains("WHERE", StringComparison.Ordinal)
                    && command.Contains("wire_format_version", StringComparison.Ordinal)
                    && command.Contains("issued_at", StringComparison.Ordinal)
                    && command.Contains("received_at", StringComparison.Ordinal));
            }
        }
        finally { File.Delete(path); }
    }

    private sealed class CountingVerifier : IOperationVerifier
    {
        public int Calls;
        public bool Verify<T>(SignedOperation<T> op) { Calls++; return new Ed25519Verifier().Verify(op); }
    }

    private sealed class InlineFactory(DbContextOptions<NodeLocalRosterDbContext> options)
        : IDbContextFactory<NodeLocalRosterDbContext>
    {
        public NodeLocalRosterDbContext CreateDbContext() => new(options);
    }
}
