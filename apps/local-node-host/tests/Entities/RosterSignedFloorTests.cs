using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Kernel.Crdt.Backends;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterSignedFloorTests
{
    private static readonly Guid Tenant = Guid.Parse("29120000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static PermissionSet Floor => PermissionSet.Of(Permission.GrantPermissions,
        Permission.OrgTransferOwnership, Permission.MembersAdmit);

    private static (MemberRoster Roster, Ed25519Signer Founder) Fixture(PermissionSet successor)
    {
        var founder = new Ed25519Signer(KeyPair.Generate());
        return (MemberRoster.Genesis(Tenant, "founder", founder, Verifier, At, Guid.NewGuid())
            .Admit("founder", founder, "successor", KeyPair.Generate().PrincipalId,
                successor, Verifier, At, Guid.NewGuid()), founder);
    }

    private static MemberRevocationRecord Removal(Ed25519Signer founder, string target = "founder") =>
        new(Tenant.ToString("D"), target, RosterSigning.SignRevocation(founder, Tenant, target,
            "founder", At.AddMinutes(1), Guid.NewGuid()));

    [Theory]
    [InlineData(Permission.GrantPermissions)]
    [InlineData(Permission.OrgTransferOwnership)]
    [InlineData(Permission.MembersAdmit)]
    public void ProjectedFloorAtomsCannotAuthorizeFounderRemoval(string missing)
    {
        var (roster, _) = Fixture(Floor.Without(missing));
        var projected = roster.Grant("founder", "successor", Floor);
        var before = projected.Members.OrderBy(m => m.PartyId).ToArray();
        var refusal = Assert.Throws<RosterGuardException>(() => projected.Revoke("founder", "founder"));
        Assert.Equal("roster.revocation.no_bricking_floor", refusal.Code);
        Assert.Equal(before, projected.Members.OrderBy(m => m.PartyId));
    }

    [Fact]
    public void NarrowedSignedSuccessorCannotAuthorizeFounderRemoval()
    {
        var (roster, _) = Fixture(Floor);
        var projected = roster.Grant("founder", "successor", PermissionSet.Empty);
        var refusal = Assert.Throws<RosterGuardException>(() => projected.Revoke("founder", "founder"));
        Assert.Equal(MemberRoster.NoBrickingFloorCode, refusal.Code);
        Assert.True(projected.Contains("founder"));
        Assert.True(projected.HasRootGrantHolder());
        Assert.True(projected.ValidatesToGenesis(Verifier));
    }

    [Theory]
    [InlineData(Permission.GrantPermissions)]
    [InlineData(Permission.OrgTransferOwnership)]
    [InlineData(Permission.MembersAdmit)]
    public void GrantCannotNarrowLastLiveFloorHolder(string missing)
    {
        var (roster, _) = Fixture(Floor);
        var projected = roster.Grant("founder", "successor", PermissionSet.Empty);
        var before = projected.Members.ToArray();
        var refusal = Assert.Throws<RosterGuardException>(() =>
            projected.Grant("founder", "founder", projected.PermissionsOf("founder")!.Without(missing)));
        Assert.Equal(MemberRoster.NoBrickingFloorCode, refusal.Code);
        Assert.Equal(before, projected.Members);
        Assert.True(projected.HasRootGrantHolder());
    }

    [Fact]
    public async Task RefusalAuditReportsEachRecordOnceAndClearsOnce()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await fixture.Projection.ReconcileAsync(CancellationToken.None);
        Assert.Single(await fixture.RowsAsync());
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            fixture.Projection.ReconcileAsync(CancellationToken.None)));
        Assert.Single(await fixture.RowsAsync());

        await fixture.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(Removal(fixture.Founder)), CancellationToken.None);
        await fixture.Projection.DrainPendingReconcilesAsync();
        var rows = await fixture.RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("AuthorizationRefused", row.EventType.Value));
        Assert.Equal(2, rows.Select(row => JsonSerializer.Serialize(row.Payload.Payload.Body)).Distinct().Count());

        var admitted = fixture.Roster.Admit("founder", fixture.Founder, "third", fixture.Roster.PublicKeyOf("founder")!.Value,
            Floor, Verifier, At.AddMinutes(2), Guid.NewGuid());
        await fixture.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(
            admitted.EnumerateAdmissions().Single(a => a.PartyId == "third")), CancellationToken.None);
        await fixture.Projection.DrainPendingReconcilesAsync();
        Assert.Empty(fixture.Live.Current.RefusedRevocations);
        Assert.False(fixture.Live.Current.Contains("founder"));
        rows = await fixture.RowsAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(row => row.EventType.Value == "AuthorizationRefusalCleared"));
        Assert.All(rows, row => Assert.True(Verifier.Verify(row.Payload)));
        await fixture.Projection.ReconcileAsync(CancellationToken.None);
        Assert.Equal(4, (await fixture.RowsAsync()).Count);
    }

    [Fact]
    public async Task DuplicateRefusalRecordsFromAnotherReplicaProduceOneAudit()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await fixture.MergeDuplicateRefusalAsync();
        Assert.Equal(2, fixture.Live.Current.RefusedRevocations.Count);
        Assert.Single(await fixture.RowsAsync());
        await fixture.Projection.ReconcileAsync(CancellationToken.None);
        Assert.Single(await fixture.RowsAsync());
    }

    [Fact]
    public async Task RefusalClearIsAuditedOnlyAfterTheRebuildIsAdopted()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        await fixture.Projection.ReconcileAsync(CancellationToken.None);
        var before = Assert.Single(await fixture.RowsAsync());
        var foreign = fixture.Roster.Admit("founder", fixture.Founder, "foreign", KeyPair.Generate().PrincipalId,
            Floor, Verifier, At.AddMinutes(2), Guid.NewGuid());
        await fixture.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(
            foreign.EnumerateAdmissions().Single(a => a.PartyId == "foreign")), CancellationToken.None);
        await fixture.Projection.DrainPendingReconcilesAsync();
        Assert.True(fixture.Live.Current.Contains("founder"));
        Assert.Equal(before, Assert.Single(await fixture.RowsAsync()));

        // Bind this node's key to a successor so the cleared rebuild passes the own-membership guard.
        var own = fixture.Roster.Admit("founder", fixture.Founder, "own", fixture.Founder.IssuerId,
            Floor, Verifier, At.AddMinutes(3), Guid.NewGuid());
        await fixture.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(
            own.EnumerateAdmissions().Single(a => a.PartyId == "own")), CancellationToken.None);
        await fixture.Projection.DrainPendingReconcilesAsync();
        Assert.False(fixture.Live.Current.Contains("founder"));
        var cleared = Assert.Single(await fixture.RowsAsync(), row => row.EventType.Value == "AuthorizationRefusalCleared");
        Assert.Equal(JsonSerializer.Serialize(before.Payload.Payload.Body), JsonSerializer.Serialize(cleared.Payload.Payload.Body));
        await fixture.Projection.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, (await fixture.RowsAsync()).Count);
    }

    [Fact]
    public async Task RefusalAuditIsAwaitedWithoutBlockingTheReconcileCaller()
    {
        var trail = new DelayedAuditTrail();
        await using var fixture = await AuditFixture.CreateAsync(trail);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Task.Run(() =>
        {
            var reconcile = fixture.Projection.ReconcileAsync(CancellationToken.None);
            returned.SetResult(reconcile);
            return reconcile;
        });
        try
        {
            await trail.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var reconcile = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reconcile.IsCompleted);
            Assert.Empty(await fixture.RowsAsync());
        }
        finally
        {
            trail.Release.TrySetResult();
            await caller.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Single(await fixture.RowsAsync());
    }

    private sealed class DelayedAuditTrail : IAuditTrail
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
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);
    }

    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly IAuditTrail _trail;
        public MemberRoster Roster { get; }
        public Ed25519Signer Founder { get; }
        public NodeTeamRoster Live { get; }
        public RosterCrdtProjection Projection { get; }

        private AuditFixture(SqliteConnection connection, ServiceProvider provider, IAuditTrail trail,
            MemberRoster roster, Ed25519Signer founder, NodeTeamRoster live, RosterCrdtProjection projection)
        {
            (_connection, _provider, _trail, Roster, Founder, Live, Projection) =
                (connection, provider, trail, roster, founder, live, projection);
        }

        public static async Task<AuditFixture> CreateAsync(IAuditTrail? trail = null)
        {
            var (roster, founder) = Fixture(Floor);
            trail ??= new InMemoryAuditTrail();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite(connection));
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
            var genesis = roster.EnumerateAdmissions().Single(a => a.Admission.IsGenesis);
            // Start with only genesis: two distinct refused records can target the same last holder.
            var live = new NodeTeamRoster(roster);
            AuthorizationRefusalAudit? audit = null;
            var projection = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(), factory, Verifier,
                NullLogger<RosterCrdtProjection>.Instance, nodeRoster: live, refusalAudit: () => audit);
            await projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(genesis), CancellationToken.None);
            await projection.PublishLocalAsync(RosterRecordCrdtState.FromRevocation(Removal(founder)), CancellationToken.None);
            await projection.DrainPendingReconcilesAsync();
            audit = new AuthorizationRefusalAudit(trail, founder, NullLogger<AuthorizationRefusalAudit>.Instance);
            return new AuditFixture(connection, provider, trail, roster, founder, live, projection);
        }

        public async Task MergeDuplicateRefusalAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using var sender = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(), factory, Verifier,
                NullLogger<RosterCrdtProjection>.Instance);
            await sender.PublishLocalAsync(Projection.Snapshot().Single(r => r.Kind == RosterRecordKind.Revocation),
                CancellationToken.None);
            await sender.DrainPendingReconcilesAsync();
            var delta = await sender.EncodeOutboundDeltaAsync(RosterCrdtProjection.DocumentId,
                ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            Assert.NotNull(delta);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta.Value, CancellationToken.None);
            await Projection.DrainPendingReconcilesAsync();
        }

        public async Task<List<AuditRecord>> RowsAsync()
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in _trail.QueryAsync(new AuditQuery(new TenantId(Tenant.ToString("D"))))) rows.Add(row);
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            await Projection.DisposeAsync();
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(Permission.GrantPermissions)]
    [InlineData(Permission.OrgTransferOwnership)]
    [InlineData(Permission.MembersAdmit)]
    public void CarriedFloorDoesNotCountAndBlockedRemovalReportsWithoutChangingState(string missing)
    {
        var (roster, founder) = Fixture(Floor.Without(missing));
        var records = roster.EnumerateAdmissions().ToArray();
        var root = records.Single(a => a.Admission.IsGenesis);
        var member = records.Single(a => !a.Admission.IsGenesis);
        var forged = member with { Permissions = Floor };
        var removal = Removal(founder);
        foreach (var admissions in new[] { new[] { root, forged }, new[] { forged, root },
                     new[] { root, member }, new[] { member, root } })
        {
            var before = MemberRoster.FromSyncedRecords(admissions, [], Verifier);
            var after = MemberRoster.FromSyncedRecords(admissions, [removal], Verifier);
            Assert.Equal(before.Members.OrderBy(m => m.PartyId), after.Members.OrderBy(m => m.PartyId));
            Assert.Equal(before.EnumerateAdmissions(), after.EnumerateAdmissions());
            var report = Assert.Single(after.RefusedRevocations);
            Assert.Equal("roster.revocation.no_bricking_floor", report.Code);
            Assert.Equal(removal, report.Revocation);
            Assert.True(after.Contains("founder"));
            Assert.Equal(admissions.Contains(member), after.Contains("successor"));
            Assert.True(after.ValidatesToGenesis(Verifier));
        }
    }

    [Fact]
    public void OrdinaryRemovalConvergesAndDoesNotLeaveAStaleRefusal()
    {
        var (roster, founder) = Fixture(Floor);
        var admissions = roster.EnumerateAdmissions().ToArray();
        var removal = Removal(founder);
        var pending = MemberRoster.FromSyncedRecords(admissions.Where(a => a.Admission.IsGenesis),
            [removal], Verifier);
        Assert.Single(pending.RefusedRevocations);
        foreach (var ordered in new[] { admissions, admissions.Reverse().ToArray() })
        {
            var complete = MemberRoster.FromSyncedRecords(ordered, [removal, removal], Verifier);
            Assert.False(complete.Contains("founder"));
            Assert.True(complete.Contains("successor"));
            Assert.Empty(complete.RefusedRevocations);
            Assert.Equal(Floor, complete.PermissionsOf("successor"));
            Assert.True(complete.ValidatesToGenesis(Verifier));
        }
    }

    [Fact]
    public async Task InboundRebuildCarriesTheFloorReportIntoTheExistingRefusalAudit()
    {
        var (roster, founder) = Fixture(Floor.Without(Permission.MembersAdmit));
        var trail = new InMemoryAuditTrail();
        var services = new ServiceCollection();
        services.AddLogging();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton(new NodeTeamRoster(roster));
        services.AddSingleton<IOperationSigner>(founder);
        services.AddSingleton<IAuditTrail>(trail);
        services.AddAuthorizationRefusalAudit();
        services.AddSingleton(TimeProvider.System);
        services.AddNodeRoster();
        await using var provider = services.BuildServiceProvider();
        var projection = provider.GetRequiredService<RosterCrdtProjection>();
        var records = roster.EnumerateAdmissions().Select(a => RosterRecordCrdtState.FromAdmission(a))
            .Append(RosterRecordCrdtState.FromRevocation(Removal(founder))).ToArray();
        var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        await using var sender = new RosterCrdtProjection(TimeProvider.System, new YDotNetCrdtEngine(), factory, Verifier,
            NullLogger<RosterCrdtProjection>.Instance);
        foreach (var record in records) await sender.PublishLocalAsync(record, CancellationToken.None);
        await sender.DrainPendingReconcilesAsync();
        var delta = await sender.EncodeOutboundDeltaAsync(RosterCrdtProjection.DocumentId,
            ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.NotNull(delta);
        await projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta.Value, CancellationToken.None);
        await projection.DrainPendingReconcilesAsync();
        var current = provider.GetRequiredService<NodeTeamRoster>().Current;
        Assert.Equal(roster.Members.Select(m => (m.PartyId, m.PublicKey, roster.PermissionsOf(m.PartyId))).OrderBy(m => m.PartyId),
            current.Members.Select(m => (m.PartyId, m.PublicKey, current.PermissionsOf(m.PartyId))).OrderBy(m => m.PartyId));
        var refusal = Assert.Single(current.RefusedRevocations);
        var rows = new List<AuditRecord>();
        await foreach (var row in trail.QueryAsync(new AuditQuery(new TenantId(Tenant.ToString("D")))))
            rows.Add(row);
        var audit = Assert.Single(rows);
        Assert.Equal(AuthorizationRefusalAudit.AuthorizationRefusedEventType, audit.EventType);
        Assert.True(Verifier.Verify(audit.Payload));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(audit.Payload.Payload.Body));
        Assert.Equal(refusal.Code, body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("preDecision").GetBoolean());
        Assert.Equal(JsonSerializer.Serialize(refusal), body.RootElement.GetProperty("diagnostic").GetString());
        Assert.Equal("founder", audit.Actor?.Value);
    }
}
