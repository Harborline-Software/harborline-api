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

public sealed class RosterPartialAdoptionTests
{
    private static readonly Guid Tenant = Guid.Parse("29620000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(10);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static PermissionSet Floor => PermissionSet.Of(Permission.GrantPermissions,
        Permission.OrgTransferOwnership, Permission.MembersAdmit);

    [Fact]
    public async Task ValidChainRevocationConvergesDespiteBackdatedSecondGenesis()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PoisonAsync();
        await f.MergeAsync([RosterRecordCrdtState.FromRevocation(f.Removal("peer"))]);
        Assert.False(f.Live.Current.Contains("peer"));
        Assert.Equal("founder", f.Live.Current.GenesisPartyId);
        Assert.Null(f.Live.PartyIdForTransportKey(f.PeerTransport));
        Assert.Null(f.Live.DmPublicKeyOf("peer"));
        Assert.Null(f.Live.XWingPublicKeyOf("peer"));
        Assert.True(f.Live.Current.HasRootGrantHolder());
    }

    [Fact]
    public async Task DroppedChainCannotAdmitGrantTransferOrReplaceValidTransport()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PoisonAsync();
        Assert.Single(await f.RowsAsync());
        foreach (var party in new[] { "poison", "intruder" })
        {
            Assert.False(f.Live.Current.Contains(party));
            Assert.Null(f.Live.Current.PermissionsOf(party));
            Assert.Throws<RosterGuardException>(() => f.Live.Current.Grant(party, "peer", Floor));
            Assert.Throws<RosterGuardException>(() => f.Live.Current.Admit(party, f.Attacker,
                "injected", KeyPair.Generate().PrincipalId, Floor, Verifier, At, Guid.NewGuid()));
        }
        Assert.Equal("peer", f.Live.PartyIdForTransportKey(f.PeerTransport));
        Assert.Null(f.Live.PartyIdForTransportKey(f.PoisonTransport));
        Assert.False(f.Live.Current.Contains("injected"));
    }

    [Fact]
    public async Task DuplicateDropIsAuditedOnceAndClearsWhenCandidateDisappears()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PoisonAsync();
        await f.MergeAsync([f.Projection.Snapshot().Single(r => r.IsGenesis && r.PartyId == "poison")]);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Projection.ReconcileAsync(default)));
        var refused = Assert.Single(await f.RowsAsync());
        Assert.Equal("AuthorizationRefused", refused.EventType.Value);
        using (var body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Payload.Payload.Body)))
        {
            Assert.Equal("roster.genesis.duplicate", body.RootElement.GetProperty("code").GetString());
            Assert.True(body.RootElement.GetProperty("preDecision").GetBoolean());
        }
        // Exercise the existing converging removal operation, then restore only the valid chain.
        await f.Projection.SupersedeOwnTeamRecordsAsync(Tenant, default);
        await f.Projection.DrainPendingReconcilesAsync();
        foreach (var a in f.Valid.EnumerateAdmissions()) await f.PublishAsync(RosterRecordCrdtState.FromAdmission(a));
        var rows = await f.RowsAsync();
        Assert.Equal(2, rows.Count);
        var cleared = Assert.Single(rows, r => r.EventType.Value == "AuthorizationRefusalCleared");
        Assert.Equal(JsonSerializer.Serialize(refused.Payload.Payload.Body), JsonSerializer.Serialize(cleared.Payload.Payload.Body));
        Assert.All(rows, r => Assert.True(Verifier.Verify(r.Payload)));
        await f.Projection.ReconcileAsync(default);
        Assert.Equal(2, (await f.RowsAsync()).Count);
    }

    [Theory]
    [InlineData(Permission.GrantPermissions)]
    [InlineData(Permission.OrgTransferOwnership)]
    [InlineData(Permission.MembersAdmit)]
    public async Task PartialAdoptionCannotUseDroppedFloorHolder(string missing)
    {
        await using var f = await Fixture.CreateAsync(Floor.Without(missing));
        await f.PoisonAsync();
        await f.PublishAsync(RosterRecordCrdtState.FromRevocation(f.Removal("founder")));
        var refusal = Assert.Single(f.Live.Current.RefusedRevocations);
        Assert.Equal(MemberRoster.NoBrickingFloorCode, refusal.Code);
        Assert.True(f.Live.Current.Contains("founder"));
        Assert.True(f.Live.Current.HasRootGrantHolder());
        Assert.Equal(2, (await f.RowsAsync()).Count);
    }

    [Fact]
    public async Task RestartReadsDurableAnchorAndIgnoresNewShellGenesis()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PoisonAsync();
        await f.PublishAsync(RosterRecordCrdtState.FromRevocation(f.Removal("peer")));
        await f.Projection.DisposeAsync();
        await f.Provider.DisposeAsync();
        var shell = MemberRoster.Genesis(Tenant, "new-shell", f.Founder, Verifier, At.AddDays(1), Guid.NewGuid());
        await using var restarted = f.NewProvider(shell);
        var factory = restarted.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        var stored = await DurableGenesisIdentity.ReadAsync(factory, Tenant, f.Founder.IssuerId, Verifier, default);
        Assert.NotNull(stored);
        Assert.Equal("founder", stored.GenesisPartyId);
        Assert.False(stored.Contains("peer"));
        // A fresh projection also chooses the log, even if a caller supplies a newly minted shell roster.
        var live = restarted.GetRequiredService<NodeTeamRoster>();
        var projection = restarted.GetRequiredService<RosterCrdtProjection>();
        await projection.HydrateFromStoreAsync(default);
        await projection.DrainPendingReconcilesAsync();
        Assert.Equal("founder", live.Current.GenesisPartyId);
        Assert.False(live.Current.Contains("peer"));
        Assert.False(live.Current.Contains("poison"));
    }

    [Fact]
    public async Task DropAuditIsAwaited()
    {
        var trail = new DelayedTrail();
        await using var f = await Fixture.CreateAsync(trail: trail);
        await f.PoisonAsync(drain: false);
        var drain = f.Projection.DrainPendingReconcilesAsync();
        try
        {
            // Race the audit entry against reconcile completion: no polling or timing-derived sleep.
            Assert.Same(trail.Started.Task, await Task.WhenAny(trail.Started.Task, drain));
            Assert.False(drain.IsCompleted);
            Assert.Empty(await f.RowsAsync());
        }
        finally
        {
            trail.Release.TrySetResult();
            await drain;
        }
        Assert.Single(await f.RowsAsync());
    }

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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"roster-partial-{Guid.NewGuid():N}");
        private IAuditTrail _trail = null!;
        public Ed25519Signer Founder { get; } = new(KeyPair.Generate());
        public Ed25519Signer Attacker { get; } = new(KeyPair.Generate());
        public byte[] PeerTransport { get; } = Enumerable.Repeat((byte)1, 32).ToArray();
        public byte[] PoisonTransport { get; } = Enumerable.Repeat((byte)2, 32).ToArray();
        public ServiceProvider Provider { get; private set; } = null!;
        public MemberRoster Valid { get; private set; } = null!;
        public NodeTeamRoster Live { get; private set; } = null!;
        public RosterCrdtProjection Projection { get; private set; } = null!;
        public ServiceProvider NewProvider(MemberRoster roster)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite(
                $"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalRosterDbContext.MigrationsHistoryTableName)));
            services.AddSingleton(new NodeTeamRoster(roster));
            services.AddSingleton<IOperationSigner>(Founder);
            services.AddSingleton(_trail);
            services.AddAuthorizationRefusalAudit();
            services.AddNodeRoster();
            return services.BuildServiceProvider();
        }
        public static async Task<Fixture> CreateAsync(PermissionSet? peer = null, IAuditTrail? trail = null)
        {
            var f = new Fixture { _trail = trail ?? new InMemoryAuditTrail() };
            Directory.CreateDirectory(f._directory);
            f.Valid = MemberRoster.Genesis(Tenant, "founder", f.Founder, Verifier, At, Guid.NewGuid())
                .Admit("founder", f.Founder, "peer", KeyPair.Generate().PrincipalId,
                    peer ?? Floor, Verifier, At.AddMinutes(1), Guid.NewGuid(),
                    newDmPublicKey: PrincipalId.FromBytes(f.PeerTransport).ToBase64Url(),
                    newXWingPublicKey: Convert.ToBase64String(new byte[1216]).TrimEnd('='));
            f.Provider = f.NewProvider(f.Valid);
            var factory = f.Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
            f.Live = f.Provider.GetRequiredService<NodeTeamRoster>();
            f.Projection = f.Provider.GetRequiredService<RosterCrdtProjection>();
            foreach (var a in f.Valid.EnumerateAdmissions())
                await f.PublishAsync(RosterRecordCrdtState.FromAdmission(a with { TransportPublicKey = a.Admission.IsGenesis ? null : f.PeerTransport }));
            Assert.True(f.Live.Current.Contains("peer"));
            Assert.Equal("peer", f.Live.PartyIdForTransportKey(f.PeerTransport));
            Assert.NotNull(f.Live.DmPublicKeyOf("peer"));
            Assert.NotNull(f.Live.XWingPublicKeyOf("peer"));
            return f;
        }
        public MemberRevocationRecord Removal(string party) => new(Tenant.ToString("D"), party,
            RosterSigning.SignRevocation(Founder, Tenant, party, "founder", At.AddHours(1), Guid.NewGuid()));
        public async Task PublishAsync(RosterRecordCrdtState record)
        {
            await Projection.PublishLocalAsync(record, default);
            await Projection.DrainPendingReconcilesAsync();
        }
        public async Task MergeAsync(IEnumerable<RosterRecordCrdtState> records)
        {
            var factory = Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using var sender = new RosterCrdtProjection(new YDotNetCrdtEngine(), factory, Verifier,
                NullLogger<RosterCrdtProjection>.Instance);
            foreach (var record in records) await sender.PublishLocalAsync(record, default);
            await sender.DrainPendingReconcilesAsync();
            var delta = await sender.EncodeOutboundDeltaAsync(RosterCrdtProjection.DocumentId, ReadOnlyMemory<byte>.Empty, default);
            Assert.NotNull(delta);
            await Projection.ApplyInboundDeltaAsync(RosterCrdtProjection.DocumentId, 1, delta.Value, default);
            await Projection.DrainPendingReconcilesAsync();
        }
        public async Task PoisonAsync(bool drain = true)
        {
            // The candidate lies about its age; durable append precedence must still choose the real root.
            var poison = MemberRoster.Genesis(Tenant, "poison", Attacker, Verifier, At.AddDays(-1), Guid.NewGuid())
                .Admit("poison", Attacker, "intruder", KeyPair.Generate().PrincipalId,
                    PermissionCompositions.Owner, Verifier, At, Guid.NewGuid())
                .Admit("poison", Attacker, "peer", KeyPair.Generate().PrincipalId,
                    Floor, Verifier, At, Guid.NewGuid());
            foreach (var a in poison.EnumerateAdmissions())
                await Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(a with { TransportPublicKey = PoisonTransport }), default);
            if (drain) await Projection.DrainPendingReconcilesAsync();
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
            await Provider.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
