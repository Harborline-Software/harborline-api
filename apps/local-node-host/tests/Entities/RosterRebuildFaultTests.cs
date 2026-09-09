using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterRebuildFaultTests
{
    private static readonly Guid Tenant = Guid.Parse("29520000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(10);

    [Fact]
    public async Task HydrationReturnsOnlyAfterAdoptionAndRefusalAudit()
    {
        await using var f = await Fixture.CreateAsync();
        await f.StoreFloorRemovalAsync();
        await f.RestartAsync();
        f.AuditGate.Hold = true;
        var hydration = f.Projection.HydrateFromStoreAsync(default);
        try
        {
            await f.AuditGate.Started.Task;
            Assert.False(hydration.IsCompleted);
        }
        finally
        {
            f.AuditGate.Release.TrySetResult();
            await f.Projection.DrainPendingReconcilesAsync();
        }
        Assert.Equal(3, await hydration);
        Assert.Single(f.Live.Current.RefusedRevocations);
        Assert.Equal(MemberRoster.NoBrickingFloorCode, Assert.Single(f.Projection.RefusalReports).Code);
        Assert.Single(await f.AuditsAsync());
    }

    [Fact]
    public async Task HydrationVerifiesEachEnvelopeOnceAndFoldsReuseOnlyIdenticalEvidence()
    {
        await using var f = await Fixture.CreateAsync();
        await f.RestartAsync();
        f.Verifier.Calls = 0;
        await f.Projection.HydrateFromStoreAsync(default);
        await f.Projection.DrainPendingReconcilesAsync();
        Assert.Equal(4, f.Verifier.Calls);
        await f.Projection.ReconcileAsync(default);
        Assert.Equal(4, f.Verifier.Calls);
        await f.Projection.HydrateFromStoreAsync(default);
        await f.Projection.DrainPendingReconcilesAsync();
        Assert.Equal(8, f.Verifier.Calls);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var peer = await db.RosterRecords.SingleAsync(r => r.PartyId == "peer");
            peer.SignedPermissionsJson = "[\"members:revoke\"]"; // Same id AND signature, different signed payload.
            await db.SaveChangesAsync();
        }
        await f.Projection.ReconcileAsync(default);
        Assert.Equal("roster.rebuild.durable_verification_failed", f.Projection.RebuildFailure?.Code);
        var afterCorruption = f.Verifier.Calls;
        await f.Projection.ReconcileAsync(default);
        Assert.Equal(afterCorruption, f.Verifier.Calls);
    }

    [Fact]
    public async Task NonCurrentWireFormatRefusesHydrationWithoutSigningOrReplication()
    {
        await using var f = await Fixture.CreateAsync();
        var forgedReceipt = At.AddYears(1);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var peer = await db.RosterRecords.SingleAsync(r => r.PartyId == "peer");
            peer.WireFormatVersion = 0;
            peer.ReceivedAtUtc = forgedReceipt;
            peer.ReceivedByPartyId = string.Empty;
            peer.ReceivedByPublicKey = string.Empty;
            peer.ReceiveAttestationSignatureB64Url = string.Empty;
            await db.SaveChangesAsync();
        }

        await f.RestartAsync();
        Assert.Equal(0, await f.Projection.HydrateFromStoreAsync(default));
        Assert.Empty(f.Projection.Snapshot());
        await f.AssertRefusalAsync("roster.rebuild.durable_verification_failed", "wire format version");

        await using var reopened = await f.Factory.CreateDbContextAsync();
        var refused = await reopened.RosterRecords.SingleAsync(r => r.PartyId == "peer");
        Assert.Equal(0, refused.WireFormatVersion);
        Assert.Equal(forgedReceipt, refused.ReceivedAtUtc);
        Assert.Empty(refused.ReceiveAttestationSignatureB64Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalSnapshotsAreImmutableAndAllocateNothingOnRead(bool failed)
    {
        await using var f = await Fixture.CreateAsync();
        if (failed)
        {
            f.Fault.Read = true;
            await f.Projection.ReconcileAsync(default);
        }
        else
        {
            await f.StoreFloorRemovalAsync();
            await f.RestartAsync();
            await f.Projection.HydrateFromStoreAsync(default);
        }
        var snapshot = f.Projection.RefusalReports;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = f.Projection.RefusalReports;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Same(snapshot, f.Projection.RefusalReports);
        f.Fault.Read = false;
        if (!failed) await f.PublishSuccessorAsync();
        await f.Projection.ReconcileAsync(default);
        Assert.Single(snapshot);
        Assert.Empty(f.Projection.RefusalReports);
    }

    [Fact]
    public async Task FloorDropHasReasonAuditTraceAndDegradedHealthUntilCleared()
    {
        await using var f = await Fixture.CreateAsync();
        await f.StoreFloorRemovalAsync();
        await f.RestartAsync();
        await f.Projection.HydrateFromStoreAsync(default);
        var refusal = Assert.Single(f.Projection.RefusalReports);
        Assert.Equal(MemberRoster.NoBrickingFloorCode, refusal.Code);
        Assert.True(f.Live.Current.Contains("founder"));
        var health = await f.HealthAsync();
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains(refusal.Detail, health.Description!);
        var row = Assert.Single(await f.AuditsAsync());
        Assert.True(new Ed25519Verifier().Verify(row.Payload));
        var reader = new AuthorizationTraceReader(f.Trail, Authorization.TestAuthorization.AllowGate());
        var read = await reader.ReadAsync(new TenantId(Tenant.ToString("D")), new ActorId("auditor"), row.AuditId, At);
        Assert.Equal("PreDecisionRefusal", read.Availability.ToString());
        var json = JsonSerializer.SerializeToElement(read);
        Assert.Equal(refusal.Code, json.GetProperty("Refusal").GetProperty("Code").GetString());
        Assert.Equal(refusal.Detail, json.GetProperty("Refusal").GetProperty("Detail").GetString());
        Assert.Empty(read.Steps); // A floor guard is pre-decision; never fabricate a gate trace.
        Assert.Null(read.Counterfactual);
        Assert.DoesNotContain("diagnostic", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await f.Projection.ReconcileAsync(default);
        Assert.Equal(HealthStatus.Degraded, (await f.HealthAsync()).Status);
        Assert.Single(await f.AuditsAsync());
        await f.PublishSuccessorAsync();
        Assert.Empty(f.Projection.RefusalReports);
        Assert.Equal(HealthStatus.Healthy, (await f.HealthAsync()).Status);
        Assert.Single(await f.AuditsAsync(), r => r.EventType.Value == "AuthorizationRefusalCleared");
    }

    private sealed class CountingVerifier : IOperationVerifier
    {
        private readonly Ed25519Verifier _inner = new();
        public int Calls;
        public bool Verify<T>(SignedOperation<T> op)
        {
            Interlocked.Increment(ref Calls);
            return _inner.Verify(op);
        }
    }

    private sealed class AuditGate(IAuditTrail inner) : IAuditTrail
    {
        public bool Hold;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            if (Hold)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            await inner.AppendAsync(record, ct);
        }
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            inner.QueryAsync(query, ct);
    }

    [Fact]
    public async Task InsertFaultRefusesWithReasonAndAuditThenRecovers()
    {
        await using var f = await Fixture.CreateAsync();
        f.Fault.Insert = true;
        await f.MergeRemovalAsync();
        await f.AssertRefusalAsync("roster.insert.failed", "injected insert failure");
        Assert.True(f.Live.Current.Contains("peer"));
        await using (var db = await f.Factory.CreateDbContextAsync()) Assert.Equal(2, await db.RosterRecords.CountAsync());
        f.Fault.Insert = false;
        await f.Projection.ReconcileAsync(default);
        Assert.False(f.Live.Current.Contains("peer"));
        Assert.Empty(f.Projection.RefusalReports);
        Assert.Equal(HealthStatus.Healthy, (await f.HealthAsync()).Status);
        Assert.Single(await f.AuditsAsync(), r => r.EventType.Value == "AuthorizationRefusalCleared");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebuildStorageFaultRefusesWithReasonAndAudit(bool afterInsert)
    {
        await using var f = await Fixture.CreateAsync();
        f.Fault.ReadAfterInsert = afterInsert;
        f.Fault.Read = !afterInsert;
        if (afterInsert) await f.MergeRemovalAsync();
        else await f.Projection.ReconcileAsync(default);
        await f.AssertRefusalAsync("roster.rebuild.failed", "injected rebuild read failure");
        Assert.True(f.Live.Current.Contains("peer"));
        await f.Projection.ReconcileAsync(default);
        Assert.Single(await f.AuditsAsync());
        f.Fault.Read = false;
        f.Fault.ReadAfterInsert = false;
        await f.Projection.ReconcileAsync(default);
        Assert.Empty(f.Projection.RefusalReports);
        if (afterInsert) Assert.False(f.Live.Current.Contains("peer"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableEvidenceFaultRefusesTheRebuildWithoutRefusingOrChangingRows(bool malformed)
    {
        await using var f = await Fixture.CreateAsync();
        await f.CorruptAsync(malformed);
        await f.Projection.ReconcileAsync(default);
        await f.AssertRefusalAsync("roster.rebuild.durable_verification_failed", malformed ? "JSON" : "invalid signature");
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.RosterRecords.CountAsync());
        var peer = await db.RosterRecords.SingleAsync(r => r.PartyId == "peer");
        Assert.Equal(malformed ? "{" : "[]", peer.SignedPermissionsJson);
        Assert.DoesNotContain(f.Projection.RefusalReports, r => r.Code.StartsWith("roster.record.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootRebuildFaultReconstructsAfterRestartAndDoesNotSeedStaleTrust()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CorruptAsync(true);
        for (var boot = 0; boot < 2; boot++)
        {
            await f.RestartAsync();
            var service = ActivatorUtilities.CreateInstance<RosterSyncBootstrapHostedService>(f.Provider);
            await service.StartAsync(default);
            await f.AssertRefusalAsync("roster.rebuild.durable_verification_failed", "JSON", boot + 1);
            Assert.Equal(0, f.Projection.Count);
            await using var db = await f.Factory.CreateDbContextAsync();
            Assert.Equal(2, await db.RosterRecords.CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPropagatesWithoutFaultReportOrAudit(bool hydration)
    {
        await using var f = await Fixture.CreateAsync();
        f.Fault.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hydration
            ? f.Projection.HydrateFromStoreAsync(default) : f.Projection.ReconcileAsync(default));
        Assert.Empty(f.Projection.RefusalReports);
        Assert.Empty(await f.AuditsAsync());
        f.Fault.Cancel = false;
    }

    private sealed class Faults : DbCommandInterceptor
    {
        public bool Insert { get; set; }
        public bool Read { get; set; }
        public bool ReadAfterInsert { get; set; }
        private bool _inserted;
        public bool Cancel { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Cancel) throw new OperationCanceledException("injected cancellation");
            if (Insert && command.CommandText.StartsWith("INSERT", StringComparison.Ordinal))
                throw new InvalidOperationException("injected insert failure");
            if (ReadAfterInsert && command.CommandText.StartsWith("INSERT", StringComparison.Ordinal)) _inserted = true;
            if ((Read || (ReadAfterInsert && _inserted)) && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
                throw new InvalidOperationException("injected rebuild read failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"roster-rebuild-{Guid.NewGuid():N}");
        private readonly InMemoryAuditTrail _trail = new();
        private readonly Ed25519Signer _founder = new(KeyPair.Generate());
        private MemberRoster _roster = null!;
        public Faults Fault { get; } = new();
        public CountingVerifier Verifier { get; } = new();
        public IAuditTrail Trail => _trail;
        public AuditGate AuditGate { get; private set; } = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public RosterCrdtProjection Projection => Provider.GetRequiredService<RosterCrdtProjection>();
        public NodeTeamRoster Live => Provider.GetRequiredService<NodeTeamRoster>();
        public IDbContextFactory<NodeLocalRosterDbContext> Factory => Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        private ServiceProvider NewProvider(string name, bool faults = true)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o =>
            {
                o.UseSqlite($"Data Source={Path.Combine(_directory, name)};Pooling=False");
                if (faults) o.AddInterceptors(Fault);
            });
            services.AddSingleton(new NodeTeamRoster(_roster));
            services.AddSingleton<IOperationSigner>(_founder);
            services.AddSingleton<IOperationVerifier>(Verifier);
            AuditGate = new AuditGate(_trail);
            services.AddSingleton<IAuditTrail>(AuditGate);
            services.AddAuthorizationRefusalAudit();
            services.AddSingleton(TimeProvider.System);
            services.AddNodeRoster();
            return services.BuildServiceProvider();
        }
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f._directory);
            f._roster = MemberRoster.Genesis(Tenant, "founder", f._founder, new Ed25519Verifier(), At, Guid.NewGuid())
                .Admit("founder", f._founder, "peer", KeyPair.Generate().PrincipalId,
                    PermissionSet.Empty, new Ed25519Verifier(), At.AddMinutes(1), Guid.NewGuid());
            f.Provider = f.NewProvider("roster.db");
            await using (var db = await f.Factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
            foreach (var admission in f._roster.EnumerateAdmissions())
                await f.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(admission), default);
            await f.Projection.DrainPendingReconcilesAsync();
            Assert.Empty(f.Projection.RefusalReports);
            return f;
        }
        public async Task MergeRemovalAsync()
        {
            await using var sender = NewProvider("sender.db", false);
            await using (var db = await sender.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.RosterRecords.AddRange(_roster.EnumerateAdmissions()
                    .Select(a => NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(a)
                        .AttestReceipt(_founder, "founder", a.Admission.IssuedAt))));
                await db.SaveChangesAsync();
            }
            var projection = sender.GetRequiredService<RosterCrdtProjection>();
            await projection.PublishLocalAsync(RosterRecordCrdtState.FromRevocation(new MemberRevocationRecord(
                Tenant.ToString("D"), "peer", RosterSigning.SignRevocation(_founder, Tenant, "peer", "founder", At.AddHours(1), Guid.NewGuid()))), default);
            await projection.DrainPendingReconcilesAsync();
            var delta = await projection.EncodeOutboundDeltaAsync(RosterCrdtProjection.DocumentId, ReadOnlyMemory<byte>.Empty, default);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta!.Value, default);
        }
        public async Task CorruptAsync(bool malformed)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var peer = await db.RosterRecords.SingleAsync(r => r.PartyId == "peer");
            peer.SignedPermissionsJson = malformed ? "{" : "[]";
            if (!malformed) peer.SignatureB64Url = "AA";
            await db.SaveChangesAsync();
        }
        public async Task StoreFloorRemovalAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            var removal = new MemberRevocationRecord(Tenant.ToString("D"), "founder",
                RosterSigning.SignRevocation(
                    _founder, Tenant, "founder", "founder", At.AddHours(1), Guid.NewGuid()));
            db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(removal)
                .AttestReceipt(_founder, "founder", removal.Signed.IssuedAt)));
            await db.SaveChangesAsync();
        }
        public async Task PublishSuccessorAsync()
        {
            var successor = _roster.Admit("founder", _founder, "successor", _founder.IssuerId,
                _roster.PermissionsOf("founder")!, new Ed25519Verifier(), At.AddMinutes(2), Guid.NewGuid());
            await Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(
                successor.EnumerateAdmissions().Single(a => a.PartyId == "successor")), default);
            await Projection.DrainPendingReconcilesAsync();
        }
        public async Task<HealthCheckResult> HealthAsync(bool active = true)
        {
            var accessor = Substitute.For<IActiveTeamAccessor>();
            await using var context = new TeamContext(new TeamId(Tenant), "Test tenant",
                new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
            if (active) accessor.Active.Returns(context);
            return await new LocalNodeHealthCheck(accessor, Projection).CheckHealthAsync(new HealthCheckContext());
        }
        public async Task AssertRefusalAsync(string code, string reason, int count = 1)
        {
            var report = Assert.Single(Projection.RefusalReports);
            Assert.Equal(code, report.Code);
            Assert.Contains(reason, report.Diagnostic, StringComparison.OrdinalIgnoreCase);
            var health = await HealthAsync();
            Assert.Equal(HealthStatus.Unhealthy, health.Status);
            Assert.Contains(code, health.Description!);
            Assert.Contains(report.Detail, health.Description!);
            var noTenant = await HealthAsync(false);
            Assert.Equal(HealthStatus.Unhealthy, noTenant.Status);
            Assert.StartsWith("No active team", noTenant.Description);
            var rows = await AuditsAsync();
            Assert.Equal(count, rows.Count);
            var row = rows.Last();
            Assert.Equal("AuthorizationRefused", row.EventType.Value);
            Assert.Null(row.Target);
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(row.Payload.Payload.Body));
            Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
            Assert.Equal(report.Diagnostic, body.RootElement.GetProperty("diagnostic").GetString());
            Assert.True(body.RootElement.GetProperty("preDecision").GetBoolean());
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
            Provider = NewProvider("roster.db");
        }
        public async ValueTask DisposeAsync()
        {
            Fault.Insert = Fault.Read = Fault.ReadAfterInsert = Fault.Cancel = false;
            await Provider.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
