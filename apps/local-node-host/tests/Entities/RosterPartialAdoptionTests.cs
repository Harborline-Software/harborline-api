using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Harborline.Api.Kernel.Runtime.Teams;
using NSubstitute;
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
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
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
    public async Task DuplicateGenesisDoesNotSilenceUnrelatedOrphan()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PoisonAsync();
        var outsider = new Ed25519Signer(KeyPair.Generate());
        var unrelated = MemberRoster.Genesis(Tenant, "missing-root", outsider, Verifier, At, Guid.NewGuid())
            .Admit("missing-root", outsider, "forged-member", KeyPair.Generate().PrincipalId,
                Floor, Verifier, At, Guid.NewGuid());
        await f.PublishAsync(RosterRecordCrdtState.FromAdmission(
            unrelated.EnumerateAdmissions().Single(a => !a.Admission.IsGenesis)));
        var factory = f.Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurableGenesisIdentity.ReadAsync(factory, Tenant, f.Founder.IssuerId, Verifier, default));
        Assert.Equal(VerifiedTenantRosterRefusal.Orphan,
            Assert.IsType<VerifiedTenantRosterRefusedException>(error.InnerException).Refusal);
    }

    [Fact]
    public async Task BootRefusesEarlierHostileGenesisEvenWhenItAdmitsDerivedPrincipal()
    {
        await using var f = await Fixture.CreateAsync();
        var hostile = MemberRoster.Genesis(Tenant, "hostile", f.Attacker, Verifier, At, Guid.NewGuid())
            .Admit("hostile", f.Attacker, "copied-public-key", f.Founder.IssuerId,
                Floor, Verifier, At, Guid.NewGuid());
        var factory = f.Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RosterRecords.RemoveRange(await db.RosterRecords.ToListAsync());
            await db.SaveChangesAsync();
            // Deliberately invert durable append order; the attacker's chain names our PUBLIC key.
            foreach (var admission in hostile.EnumerateAdmissions().Concat(f.Valid.EnumerateAdmissions()))
            {
                db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(admission)));
                await db.SaveChangesAsync();
            }
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurableGenesisIdentity.ReadAsync(factory, Tenant, f.Founder.IssuerId, Verifier, default));
        Assert.Contains(DurableGenesisIdentity.InvalidLogCode, error.Message);
        Assert.Equal(VerifiedTenantRosterRefusal.MultipleGenesis,
            Assert.IsType<VerifiedTenantRosterRefusedException>(error.InnerException).Refusal);
    }

    [Fact]
    public async Task MalformedDuplicateIsAuditedOnceAndFoldCompletes()
    {
        await using var f = await Fixture.CreateAsync();
        var poison = MemberRoster.Genesis(Tenant, "poison", f.Attacker, Verifier, At, Guid.NewGuid());
        var candidate = RosterRecordCrdtState.FromAdmission(poison.EnumerateAdmissions().Single())
            with { IssuedAtIso = "not-a-date" };
        var factory = f.Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var party in new[] { "founder", "peer" })
            {
                var tip = await db.AdministratorAuthority.OrderByDescending(r => r.Sequence).FirstOrDefaultAsync();
                var record = new AdministratorAuthorityRecord
                {
                    Sequence = (tip?.Sequence ?? 0) + 1, TeamId = Tenant.ToString("D"), PartyId = party,
                    Event = AdministratorAuthorityEvent.Established, Provenance = AdministratorProvenance.Recovery,
                    MemberPublicKey = "cHVibGljLWtleQ", AdmissionSignature = "c2lnbmF0dXJl",
                    AdmittedByPublicKey = "cHVibGljLWtleQ", AdmittedByPartyId = party,
                    OccurredAtUtc = At, Reason = "test-seed", PreviousHash = tip?.Hash ?? AdministratorAuthorityRecord.ZeroHash,
                    Hash = string.Empty,
                };
                record.Hash = AdministratorAuthorityRecord.ComputeHash(record);
                db.AdministratorAuthority.Add(record);
                await db.SaveChangesAsync();
            }
        }
        await f.MergeAsync([candidate, RosterRecordCrdtState.FromRevocation(f.Removal("peer"))]);
        await f.Projection.ReconcileAsync(default);
        Assert.False(f.Live.Current.Contains("peer"));
        await using var check = await factory.CreateDbContextAsync();
        var removal = Assert.Single(await check.AdministratorAuthority.Where(r => r.PartyId == "peer"
            && r.Event != AdministratorAuthorityEvent.Established).ToListAsync());
        Assert.Equal(RosterCrdtProjection.RosterRevocationRemovalReason, removal.Reason);
        var row = Assert.Single(await f.RowsAsync());
        Assert.Equal("AuthorizationRefused", row.EventType.Value);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(row.Payload.Payload.Body));
        Assert.Equal("roster.genesis.duplicate", body.RootElement.GetProperty("code").GetString());
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenesisDiagnosticsAlsoReportDroppedCandidatesAlongsideAdoptedChain(bool foreign)
    {
        await using var f = await Fixture.CreateAsync();
        var tenant = foreign ? Guid.Parse("29630000-0000-0000-0000-000000000002") : Tenant;
        var candidate = MemberRoster.Genesis(tenant, "other-founder", f.Attacker, Verifier, At, Guid.NewGuid());
        await f.MergeAsync([RosterRecordCrdtState.FromAdmission(candidate.EnumerateAdmissions().Single())]);
        var report = Assert.Single(f.Projection.RefusalReports);
        Assert.Equal(foreign ? "roster.genesis.foreign_tenant" : "roster.genesis.duplicate", report.Code);
        if (!foreign) Assert.Contains("injection attempt", report.Remediation);
        Assert.True(f.Live.Current.Contains("peer"));
        Assert.False(f.Live.Current.Contains("other-founder"));
        await f.Projection.ReconcileAsync(default);
        Assert.Single(await f.RowsAsync(tenant));
        Assert.Single(f.Logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenesisDiagnosticsReportOnceAcrossCyclesAndClearWithoutAdoption(bool foreign)
    {
        await using var f = await Fixture.CreateAsync();
        var tenant = foreign ? Guid.Parse("29630000-0000-0000-0000-000000000002") : Tenant;
        var candidate = MemberRoster.Genesis(tenant, "different-party", f.Attacker, Verifier, At, Guid.NewGuid());
        // Leave only the candidate in the sync document: no adoptable chain, while the live roster stays intact.
        await f.Projection.SupersedeOwnTeamRecordsAsync(Tenant, default);
        await f.Projection.DrainPendingReconcilesAsync();
        await f.PublishAsync(RosterRecordCrdtState.FromAdmission(candidate.EnumerateAdmissions().Single()));
        var code = foreign ? "roster.genesis.foreign_tenant" : "roster.genesis.duplicate";
        var report = Assert.Single(f.Projection.RefusalReports);
        Assert.Equal(code, report.Code);
        Assert.Contains(foreign ? "intended tenant" : "Remove the duplicate candidate", report.Remediation);
        var accessor = Substitute.For<IActiveTeamAccessor>();
        var health = new LocalNodeHealthCheck(accessor, f.Projection);
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new HealthCheckContext())).Status);
        await using var active = new TeamContext(new TeamId(Tenant), "Test tenant",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        accessor.Active.Returns(active);
        var first = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, first.Status);
        Assert.Equal($"Roster refused: {code}. {report.Remediation}", first.Description);
        var entry = new HealthReportEntry(first.Status, first.Description, TimeSpan.Zero, null, first.Data);
        var aggregate = new HealthReport(new Dictionary<string, HealthReportEntry>
            { ["local-node"] = entry, ["local-node-readiness"] = entry }, TimeSpan.Zero);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var writer = typeof(LocalNodeHealthProbeEndpointRouteBuilderExtensions).GetMethod("WriteAggregateResponseAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)writer.Invoke(null, [http, aggregate])!;
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        Assert.Equal($"Degraded{Environment.NewLine}{first.Description}", await reader.ReadToEndAsync());
        var warnings = f.Logger.Warnings.ToArray();
        Assert.Single(warnings);
        Assert.Contains(code, warnings[0]);
        Assert.Contains(report.Remediation, warnings[0]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Projection.ReconcileAsync(default)));
        Assert.Equal(warnings, f.Logger.Warnings.ToArray());
        Assert.Single(f.Projection.RefusalReports);
        Assert.Equal(first.Description, (await health.CheckHealthAsync(new HealthCheckContext())).Description);
        var row = Assert.Single(await f.RowsAsync(tenant));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(row.Payload.Payload.Body));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(report.Remediation, body.RootElement.GetProperty("remedy").GetString());
        Assert.True(body.RootElement.GetProperty("preDecision").GetBoolean());
        Assert.Equal("founder", f.Live.Current.GenesisPartyId);
        // Empty snapshot must clear even though it cannot adopt a replacement roster.
        await f.Projection.SupersedeOwnTeamRecordsAsync(tenant, default);
        await f.Projection.DrainPendingReconcilesAsync();
        Assert.Empty(f.Projection.RefusalReports);
        Assert.DoesNotContain("Roster refused", (await health.CheckHealthAsync(new HealthCheckContext())).Description!);
        Assert.Single(await f.RowsAsync(tenant), r => r.EventType.Value == "AuthorizationRefusalCleared");
        await f.Projection.ReconcileAsync(default);
        Assert.Equal(2, (await f.RowsAsync(tenant)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenesisDiagnosticsReconstructAfterRestart(bool foreign)
    {
        await using var f = await Fixture.CreateAsync();
        var tenant = foreign ? Guid.Parse("29630000-0000-0000-0000-000000000002") : Tenant;
        await f.Projection.SupersedeOwnTeamRecordsAsync(Tenant, default);
        await f.Projection.DrainPendingReconcilesAsync();
        var candidate = MemberRoster.Genesis(tenant, "other-founder", f.Attacker, Verifier, At, Guid.NewGuid());
        await f.PublishAsync(RosterRecordCrdtState.FromAdmission(candidate.EnumerateAdmissions().Single()));
        var before = Assert.Single(f.Projection.RefusalReports);
        await f.Projection.DisposeAsync();
        await f.Provider.DisposeAsync();
        await using var restarted = f.NewProvider(f.Valid);
        var projection = restarted.GetRequiredService<RosterCrdtProjection>();
        await projection.HydrateFromStoreAsync(default);
        await projection.DrainPendingReconcilesAsync();
        Assert.Equal(before, Assert.Single(projection.RefusalReports));
        var warnings = f.Logger.Warnings.Count;
        var rows = (await f.RowsAsync(tenant)).Count;
        for (var i = 0; i < 8; i++) await projection.ReconcileAsync(default);
        Assert.Equal(warnings, f.Logger.Warnings.Count);
        Assert.Equal(rows, (await f.RowsAsync(tenant)).Count);
        var accessor = Substitute.For<IActiveTeamAccessor>();
        var health = new LocalNodeHealthCheck(accessor, projection);
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new HealthCheckContext())).Status);
        await using var active = new TeamContext(new TeamId(Tenant), "Test tenant",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        accessor.Active.Returns(active);
        var result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal($"Roster refused: {before.Code}. {before.Remediation}", result.Description);
        await projection.SupersedeOwnTeamRecordsAsync(tenant, default);
        await projection.DrainPendingReconcilesAsync();
        Assert.Empty(projection.RefusalReports);
    }

    [Fact]
    public async Task LocallyMintedStaleGenesisRaisesNoRefusalDuringHydrationOrSupersession()
    {
        await using var f = await Fixture.CreateAsync();
        var formerTenant = Guid.Parse("29630000-0000-0000-0000-000000000002");
        var stale = MemberRoster.Genesis(formerTenant, "founder", f.Founder, Verifier, At, Guid.NewGuid());
        var candidate = RosterRecordCrdtState.FromAdmission(stale.EnumerateAdmissions().Single());
        var factory = f.Provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(candidate));
            await db.SaveChangesAsync();
        }
        await f.Projection.DisposeAsync();
        await f.Provider.DisposeAsync();
        await using var restarted = f.NewProvider(f.Valid);
        var projection = restarted.GetRequiredService<RosterCrdtProjection>();
        await projection.HydrateFromStoreAsync(default);
        await projection.DrainPendingReconcilesAsync();
        Assert.Contains(projection.Snapshot(), r => r.RecordId == candidate.RecordId);
        Assert.Empty(projection.RefusalReports);
        Assert.Empty(await f.RowsAsync(formerTenant));
        Assert.Empty(f.Logger.Warnings);
        Assert.True(restarted.GetRequiredService<NodeTeamRoster>().Current.Contains("peer"));
        Assert.Equal(1, await projection.ReconcileLocallyMintedGenesisAsync(default));
        await projection.DrainPendingReconcilesAsync();
        Assert.DoesNotContain(projection.Snapshot(), r => r.RecordId == candidate.RecordId);
        Assert.Empty(projection.RefusalReports);
        Assert.Empty(await f.RowsAsync(formerTenant));
        Assert.Empty(await f.RowsAsync());
    }

    private sealed class RecordingLogger : ILogger<RosterCrdtProjection>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning) Warnings.Enqueue(formatter(state, exception));
        }
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
        public RecordingLogger Logger { get; } = new();
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
            services.AddSingleton<ILogger<RosterCrdtProjection>>(Logger);
            services.AddDbContextFactory<NodeLocalRosterDbContext>(o => o.UseSqlite(
                $"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalRosterDbContext.MigrationsHistoryTableName)));
            services.AddSingleton(new NodeTeamRoster(roster));
            services.AddSingleton<IOperationSigner>(Founder);
            services.AddSingleton(_trail);
            services.AddAuthorizationRefusalAudit();
            services.AddSingleton(sp => new NodeAdministratorAuthority(
                sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>(),
                TimeProvider.System, TestAuthorization.AllowGate()));
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
        public async Task<List<AuditRecord>> RowsAsync(Guid? tenant = null)
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in _trail.QueryAsync(new AuditQuery(new TenantId((tenant ?? Tenant).ToString("D"))))) rows.Add(row);
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
