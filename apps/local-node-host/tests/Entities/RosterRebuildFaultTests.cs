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
        Assert.Equal(malformed ? "{" : "[]", peer.PermissionsJson);
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
            services.AddSingleton<IAuditTrail>(_trail);
            services.AddAuthorizationRefusalAudit();
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
                    .Select(a => NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(a))));
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
            peer.PermissionsJson = malformed ? "{" : "[]";
            if (!malformed) peer.SignatureB64Url = "AA";
            await db.SaveChangesAsync();
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
