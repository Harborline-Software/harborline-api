using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterPreInsertVerificationTests
{
    private static readonly Guid Tenant = Guid.Parse("29510000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(10);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSignatureNeverReachesDurableStore(bool revoke)
    {
        await using var f = await Fixture.CreateAsync();
        var record = revoke ? f.Revocation(f.Founder, "founder", "member")
            : f.Admission(f.Founder, "founder", "new");
        record = record with { SignatureB64Url = new string('A', 86) };
        await f.MergeAsync([record]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == record.RecordId);
        var rows = await f.AuditsAsync();
        var row = Assert.Single(rows);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(row.Payload.Payload.Body));
        Assert.Equal("roster.record.signature_invalid", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("preDecision").GetBoolean());
        Assert.Contains(record.RecordId, body.RootElement.GetProperty("diagnostic").GetString());
        Assert.True(Verifier.Verify(row.Payload));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Projection.ReconcileAsync(default)));
        await f.MergeAsync([record]);
        Assert.Single(await f.AuditsAsync());
    }

    [Fact]
    public async Task RestartUsesDurableChainAndRefusedRecordsAreAbsentFromHydration()
    {
        await using var f = await Fixture.CreateAsync();
        var valid = f.Admission(f.Founder, "founder", "valid");
        var bad = f.Admission(f.Founder, "founder", "bad") with { SignatureB64Url = new string('A', 86) };
        await f.MergeAsync([bad, valid]);
        await f.RestartAsync();
        // No hydration or live membership seed: the next inbound check must read durable evidence.
        var afterRestart = f.Admission(f.Founder, "founder", "after-restart");
        await f.MergeAsync([afterRestart]);
        var stored = await f.StoredAsync();
        Assert.Contains(stored, r => r.RecordId == valid.RecordId);
        Assert.Contains(stored, r => r.RecordId == afterRestart.RecordId);
        Assert.DoesNotContain(stored, r => r.RecordId == bad.RecordId);
        await f.Projection.HydrateFromStoreAsync(default);
        await f.Projection.DrainPendingReconcilesAsync();
        Assert.DoesNotContain(f.Projection.Snapshot(), r => r.RecordId == bad.RecordId);
        var reader = f.Provider.GetRequiredService<IVerifiedTenantRosterReader>();
        var rebuilt = await reader.ReadAsync(new TenantId(Tenant.ToString("D")), default);
        Assert.True(rebuilt.Contains("valid"));
        Assert.True(rebuilt.Contains("after-restart"));
        Assert.False(rebuilt.Contains("bad"));
    }

    [Fact]
    public async Task DurableGenesisRefusesBackdatedSecondRoot()
    {
        await using var f = await Fixture.CreateAsync();
        var root = MemberRoster.Genesis(Tenant, "intruder", f.Member, Verifier, At.AddDays(-1), Guid.NewGuid());
        var candidate = RosterRecordCrdtState.FromAdmission(root.EnumerateAdmissions().Single());
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        Assert.Contains("roster.genesis.duplicate", JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedSignerCannotAddRecordsLaterInTheChain(bool revoke)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        await f.MergeAsync([f.Revocation(f.Founder, "founder", "member")]);
        var candidate = revoke ? f.Revocation(f.Member, "member", "founder", At.AddHours(2))
            : f.Admission(f.Member, "member", "late", At.AddHours(2));
        await f.RestartAsync();
        await f.MergeAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == candidate.RecordId);
        Assert.Contains("roster.record.chain_ineligible", JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task ReverseOrderedValidChainIsStoredAndBadRecordAuditIsAwaited()
    {
        var trail = new DelayedTrail();
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner, trail);
        var child = f.Admission(f.Member, "member", "child");
        await f.MergeAsync([child]);
        Assert.Contains(await f.StoredAsync(), r => r.RecordId == child.RecordId);
        var bad = f.Admission(f.Founder, "founder", "bad") with { SignatureB64Url = new string('A', 86) };
        var merge = f.MergeAsync([bad]);
        try
        {
            Assert.Same(trail.Started.Task, await Task.WhenAny(trail.Started.Task, merge));
            Assert.False(merge.IsCompleted);
            Assert.Empty(await f.AuditsAsync());
        }
        finally { trail.Release.TrySetResult(); }
        await merge;
        Assert.Single(await f.AuditsAsync());
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == bad.RecordId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveOrderSurvivesRestartAndLateBackdatingCannotReplaceEarlierRemoval(bool earlierLegitimate)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var first = earlierLegitimate
            ? f.Revocation(f.Member, "member", "founder", At.AddMinutes(2))
            : f.Revocation(f.Founder, "founder", "member", At.AddHours(1));
        first = first.AttestReceipt(f.Founder, "founder", At.AddHours(2));
        await f.MergeRawAsync([first]);
        DateTimeOffset? receipt;
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var rows = await db.RosterRecords.AsNoTracking().ToListAsync();
            Assert.All(rows, row => Assert.NotNull(row.ReceivedAtUtc));
            Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.ReceiveAttestationSignatureB64Url)));
            receipt = rows.Single(row => row.Id == first.RecordId).ReceivedAtUtc;
        }
        await f.RestartAsync();
        await f.MergeRawAsync([first]);
        await using (var db = await f.Factory.CreateDbContextAsync())
            Assert.Equal(receipt, (await db.RosterRecords.SingleAsync(row => row.Id == first.RecordId)).ReceivedAtUtc);
        var late = earlierLegitimate
            ? f.Revocation(f.Founder, "founder", "member", At.AddMinutes(1))
            : f.Revocation(f.Member, "member", "founder", At.AddMinutes(2));
        late = late.AttestReceipt(f.Founder, "founder", At.AddHours(2).AddSeconds(1));
        await f.MergeRawAsync([late]);
        Assert.DoesNotContain(await f.StoredAsync(), r => r.RecordId == late.RecordId);
        var reader = f.Provider.GetRequiredService<IVerifiedTenantRosterReader>();
        var rebuilt = await reader.ReadAsync(new TenantId(Tenant.ToString("D")), default);
        Assert.Equal(earlierLegitimate, rebuilt.Contains("member"));
        Assert.Equal(!earlierLegitimate, rebuilt.Contains("founder"));
        Assert.Contains("roster.record.chain_ineligible", JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-record")]
    [InlineData("altered-time")]
    public async Task ForgedReceiveAttestationIsRefusedBeforeDurableInsert(string mutation)
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", $"candidate-{mutation}", At.AddHours(2))
            .AttestReceipt(f.Founder, "founder", At.AddHours(2));
        candidate = mutation switch
        {
            "wrong-key" => candidate with { ReceivedByPublicKey = f.Member.IssuerId.ToBase64Url() },
            "wrong-record" => CopyReceipt(candidate,
                f.Admission(f.Founder, "founder", "other-record", At.AddHours(2))
                    .AttestReceipt(f.Founder, "founder", At.AddHours(2))),
            _ => candidate with { ReceivedAtIso = At.AddHours(3).ToString("O") },
        };
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), row => row.RecordId == candidate.RecordId);
        Assert.Contains("roster.record.receive_attestation_invalid",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task FutureOrderTimeIsRefusedAndReceiptWindowEdgeIsAccepted()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var received = At.AddHours(2);
        var forgedFuture = f.Admission(f.Founder, "founder", "forged-future",
                received + NodeRosterRecord.ReceiveTimeWindow + TimeSpan.FromMilliseconds(1))
            .AttestReceipt(f.Founder, "founder", received);
        var atWindowEdge = f.Admission(f.Founder, "founder", "window-edge",
                received + NodeRosterRecord.ReceiveTimeWindow)
            .AttestReceipt(f.Founder, "founder", received);

        await f.MergeRawAsync([forgedFuture, atWindowEdge]);

        var stored = await f.StoredAsync();
        Assert.DoesNotContain(stored, row => row.RecordId == forgedFuture.RecordId);
        Assert.Contains(stored, row => row.RecordId == atWindowEdge.RecordId);
        Assert.Contains("roster.record.order_time_future",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task OldWireShapeIsRefusedAtTheVersionBoundary()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "old-peer", At.AddHours(2)) with
        {
            WireFormatVersion = 3,
            UnmappedWireFields = PermissionField(),
        };
        candidate = JsonSerializer.Deserialize<RosterRecordCrdtState>(JsonSerializer.Serialize(candidate))!;
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), row => row.RecordId == candidate.RecordId);
        Assert.Contains("roster_wire_format_unsupported",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    [Fact]
    public async Task CurrentWireShapeWithPermissionFieldIsRefusedAsMalformed()
    {
        await using var f = await Fixture.CreateAsync(PermissionCompositions.Owner);
        var candidate = f.Admission(f.Founder, "founder", "stray-permissions", At.AddHours(2))
            .AttestReceipt(f.Founder, "founder", At.AddHours(2)) with
        {
            UnmappedWireFields = PermissionField(),
        };
        var json = JsonSerializer.Serialize(candidate);
        Assert.Contains("\"Permissions\"", json, StringComparison.Ordinal);
        candidate = JsonSerializer.Deserialize<RosterRecordCrdtState>(json)!;
        await f.MergeRawAsync([candidate]);
        Assert.DoesNotContain(await f.StoredAsync(), row => row.RecordId == candidate.RecordId);
        Assert.Contains("roster.record.malformed",
            JsonSerializer.Serialize(Assert.Single(await f.AuditsAsync()).Payload.Payload.Body));
    }

    private static Dictionary<string, JsonElement> PermissionField() => new(StringComparer.Ordinal)
    {
        ["Permissions"] = JsonSerializer.Deserialize<JsonElement>("[\"records:read\"]"),
    };

    private static RosterRecordCrdtState CopyReceipt(
        RosterRecordCrdtState target, RosterRecordCrdtState source) => target with
    {
        WireFormatVersion = source.WireFormatVersion,
        ReceivedAtIso = source.ReceivedAtIso,
        ReceivedByPartyId = source.ReceivedByPartyId,
        ReceivedByPublicKey = source.ReceivedByPublicKey,
        ReceiveAttestationSignatureB64Url = source.ReceiveAttestationSignatureB64Url,
    };

    private sealed class DelayedTrail : IAuditTrail
    {
        private readonly InMemoryAuditTrail _inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await _inner.AppendAsync(record, ct);
        }
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) => _inner.QueryAsync(query, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"roster-preinsert-{Guid.NewGuid():N}");
        private IAuditTrail _trail = null!;
        public Ed25519Signer Founder { get; } = new(KeyPair.Generate());
        public Ed25519Signer Member { get; } = new(KeyPair.Generate());
        public ServiceProvider Provider { get; private set; } = null!;
        public RosterCrdtProjection Projection => Provider.GetRequiredService<RosterCrdtProjection>();
        public IDbContextFactory<NodeLocalRosterDbContext> Factory => Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        // What the grant store holds for "member" - the replicated chain gates read authority from here now.
        private PermissionSet _memberAuthority = PermissionSet.Empty;

        private ServiceProvider NewProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False"));
            services.AddSingleton<IOperationSigner>(Founder);
            services.AddSingleton(_trail);
            services.AddAuthorizationRefusalAudit();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IRosterAuthority>(new TestRosterAuthority(("member", _memberAuthority)));
            services.AddNodeRoster();
            return services.BuildServiceProvider();
        }
        public static async Task<Fixture> CreateAsync(PermissionSet? permissions = null, IAuditTrail? trail = null)
        {
            var f = new Fixture { _trail = trail ?? new InMemoryAuditTrail(),
                _memberAuthority = permissions ?? PermissionSet.Empty };
            Directory.CreateDirectory(f._directory);
            f.Provider = f.NewProvider();
            await using (var db = await f.Factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
            var roster = MemberRoster.Genesis(Tenant, "founder", f.Founder, Verifier, At, Guid.NewGuid())
                .Admit("founder", f.Founder, "member", f.Member.IssuerId, permissions ?? PermissionSet.Empty,
                    Verifier, At.AddMinutes(1), Guid.NewGuid());
            // Both records enter over the real inbound route, in reverse chain order, into an empty receiver.
            await f.MergeAsync(roster.EnumerateAdmissions().Reverse().Select(a => RosterRecordCrdtState.FromAdmission(a)));
            return f;
        }
        public RosterRecordCrdtState Admission(Ed25519Signer signer, string party, string target, DateTimeOffset? at = null)
        {
            var key = KeyPair.Generate().PrincipalId;
            var signed = RosterSigning.SignAdmission(signer, Tenant, target, key, party, false,
                at ?? At.AddHours(1), Guid.NewGuid());
            return RosterRecordCrdtState.FromAdmission(
                new MemberAdmissionRecord(Tenant.ToString("D"), target, key, signed));
        }
        public RosterRecordCrdtState Revocation(Ed25519Signer signer, string party, string target, DateTimeOffset? at = null) =>
            RosterRecordCrdtState.FromRevocation(new MemberRevocationRecord(Tenant.ToString("D"), target,
                RosterSigning.SignRevocation(signer, Tenant, target, party, at ?? At.AddHours(1), Guid.NewGuid())));
        public async Task MergeAsync(IEnumerable<RosterRecordCrdtState> records)
        {
            // A sender may persist arbitrary input; it never shares the receiving database.
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_directory, "sender.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
            await using var sender = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(), factory,
                Verifier, Founder, NullLogger<RosterCrdtProjection>.Instance, attestationPartyId: "founder");
            foreach (var record in records) await sender.PublishLocalAsync(record, default);
            await sender.DrainPendingReconcilesAsync();
            var delta = await sender.EncodeOutboundDeltaAsync(RosterCrdtProjection.DocumentId, ReadOnlyMemory<byte>.Empty, default);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta!.Value, default);
        }
        public async Task MergeRawAsync(IEnumerable<RosterRecordCrdtState> records)
        {
            await using var raw = new Harborline.Api.Kernel.Crdt.CrdtProjection<RosterCrdtSchema>(
                new YDotNetCrdtEngine(), new RosterCrdtSchema(_ => Task.CompletedTask));
            raw.Mutate(schema => schema.PushMany(records));
            var delta = raw.EncodeDelta(ReadOnlyMemory<byte>.Empty);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta, default);
        }
        public async Task<List<RosterRecordCrdtState>> StoredAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            return (await db.RosterRecords.AsNoTracking().ToListAsync()).Select(NodeRosterRecord.ToCrdtState).ToList();
        }
        public async Task<List<AuditRecord>> AuditsAsync()
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in _trail.QueryAsync(new AuditQuery(new TenantId(Tenant.ToString("D"))))) rows.Add(row);
            return rows;
        }
        public async Task RestartAsync()
        {
            await Provider.DisposeAsync();
            Provider = NewProvider();
        }
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
