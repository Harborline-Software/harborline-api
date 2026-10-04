using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;

using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// T-1048: the process stops after a spatial-frame mint commits and before anything after the commit runs. A
/// restarted host over the same encrypted file must still owe, and then deliver exactly once, the mint's audit.
/// </summary>
[Collection(HomeEpochFenceStaticHookCollection.Name)]
public sealed class SpatialFrameMintAuditCrashTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant-spatial-crash");
    private static readonly RegistryEntityId Anchor = new("hull-001");
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuditEventType Minted = new("SpatialFrameDescriptorMinted");
    private static readonly AuditEventType Conflict = new("SpatialFrameDescriptorConflictDetected");

    private DurableAuditHarness _harness = null!;
    private NodePrincipalSigner _signer = null!;
    private SpatialFramePiiFieldSealer _sealer = null!;
    private readonly FixedClock _clock = new(FixedNow);

    public async Task InitializeAsync()
    {
        _harness = await DurableAuditHarness.CreateAsync();
        await using (var main = _harness.Store.CreateLocalNodeContext())
            await main.GetService<IMigrator>().MigrateAsync();
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        _signer = new NodePrincipalSigner(seed);
        var rootSeed = new byte[32];
        Random.Shared.NextBytes(rootSeed);
        _sealer = new SpatialFramePiiFieldSealer(
            new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed));
    }

    public async Task DisposeAsync()
    {
        _signer.Dispose();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task ProcessStopAfterMintCommit_RestartDeliversOneMintedAuditOnce()
    {
        await SeedHomeClaimAsync();

        await Assert.ThrowsAsync<SimulatedProcessStop>(() => Store(new StopOnAppend()).MintAsync(Request(0)));

        var minted = await RecoverAsync(Minted);
        Assert.Equal("1", minted.Payload.Payload.Body["frameEpoch"]?.ToString());
        Assert.Equal("hull-001", minted.Payload.Payload.Body["anchor"]?.ToString());
        Assert.Equal(FixedNow, minted.OccurredAt);
        await using var main = _harness.Store.CreateLocalNodeContext();
        Assert.Equal(1, await main.Set<SpatialFrameDescriptorRow>().CountAsync());
    }

    [Fact]
    public async Task ProcessStopAfterLayer2QuarantineCommit_RestartDeliversOneConflictAuditOnce()
    {
        await SeedHomeClaimAsync();
        await Store(new InMemoryRegistryAuditLog()).MintAsync(Request(0));

        await Assert.ThrowsAsync<SimulatedProcessStop>(() => Store(new StopOnAppend()).MintAsync(Request(0)));

        var conflict = await RecoverAsync(Conflict);
        Assert.Equal("1", conflict.Payload.Payload.Body["frameEpoch"]?.ToString());
        await using var main = _harness.Store.CreateLocalNodeContext();
        Assert.Equal(1, await main.Set<SpatialFrameQuarantineRow>().CountAsync());
    }

    [Fact]
    public async Task ProcessStopAfterLayer3QuarantineCommit_RestartDeliversOneConflictAuditOnce()
    {
        await SeedHomeClaimAsync();
        var existing = await Store(new InMemoryRegistryAuditLog()).MintAsync(Request(0));

        await Assert.ThrowsAsync<SimulatedProcessStop>(() => Port(new StopOnAppend()).MintAsync(
            new SpatialFrameMintCommand(Tenant, Anchor, "hull-datum"),
            (_, _) => ValueTask.FromResult(SpatialFrameMintStaging.Mint(
                existing with { Attestation = existing.Attestation with { Nonce = Guid.NewGuid() } }))));

        await RecoverAsync(Conflict);
        await using var main = _harness.Store.CreateLocalNodeContext();
        Assert.Equal(1, await main.Set<SpatialFrameQuarantineRow>().CountAsync());
    }

    [Fact]
    public async Task MintRefusedInsideTheFence_StagesNoAudit()
    {
        await Assert.ThrowsAsync<FrameEpochMintRefusedException>(
            () => Store(new InMemoryRegistryAuditLog()).MintAsync(Request(0)));

        await using var search = _harness.Store.CreateContext();
        Assert.Empty(await search.AuditOutbox.ToListAsync());
    }

    /// <summary>A restart, then two drains: exactly one owed entry, delivered once and marked delivered.</summary>
    private async Task<AuditRecord> RecoverAsync(AuditEventType eventType)
    {
        var restarted = _harness.Reopen();
        var first = await restarted.DeliveredAsync(Tenant, eventType);
        var second = await _harness.Reopen().DeliveredAsync(Tenant, eventType);

        var delivered = Assert.Single(first);
        Assert.Equal(delivered.AuditId, Assert.Single(second).AuditId);
        await using var search = _harness.Store.CreateContext();
        var owed = Assert.Single(await search.AuditOutbox.Where(row => row.EventType == eventType.Value).ToListAsync());
        Assert.Equal(delivered.AuditId.ToString("D"), owed.AuditId);
        Assert.NotNull(owed.PublishedAtUnixMs);
        return delivered;
    }

    private NodeEfSpatialFrameDescriptorPort Port(IRegistryAuditLog audit) =>
        new(_harness.Store.LocalNodeFactory, audit, _sealer, _signer, _clock);

    private FoundationBackedSpatialFrameDescriptorStore Store(IRegistryAuditLog audit) =>
        new(Port(audit), new NodeHomeClaimFrameEpochAuthority(_harness.Store.LocalNodeFactory, _signer),
            _signer.Signer, clock: _clock);

    private async Task SeedHomeClaimAsync()
    {
        await using var main = _harness.Store.CreateLocalNodeContext();
        main.Add(new HomeEpochRecord
        {
            TenantId = Tenant.Value,
            EpochNumber = 1,
            PreviousEpochNumber = 0,
            HomeDeviceId = _signer.NodePublicKey,
            PromotionKind = HomePromotionKind.PlannedHandoff,
            IssuedAt = FixedNow,
            Nonce = Guid.NewGuid(),
            IssuerId = _signer.NodePublicKey,
            Signature = "test-seeded",
        });
        await main.SaveChangesAsync();
    }

    private static SpatialFrameMintRequest Request(long previousEpoch) =>
        new(Tenant, Anchor, "hull-datum", previousEpoch,
            AxisConvention: "x-fwd-y-stbd-z-down",
            OriginDescription: "aft perpendicular at baseline",
            LengthUnit: "metre",
            Georeference: null);

    /// <summary>The process stops at the first thing that runs after the commit: the in-memory registry append.</summary>
    private sealed class StopOnAppend : IRegistryAuditLog
    {
        public RegistryAuditEvent Append(
            TenantId tenant, string subject, RegistryOp op, Instant at, string? actorRef = null, string? detail = null) =>
            throw new SimulatedProcessStop();

        public IReadOnlyList<RegistryAuditEvent> ForSubject(TenantId tenant, string subject) => [];

        public IReadOnlyList<RegistryAuditEvent> ForTenant(TenantId tenant) => [];

        public bool VerifyChain(TenantId tenant, string subject) => true;
    }

    private sealed class SimulatedProcessStop : Exception;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// T-1048: the mint stages its audit in <c>search_audit_outbox</c>, which the search context owns and migrates.
/// A fixture that builds the main context with <c>EnsureCreated</c> applies the search schema next to it.
/// </summary>
internal static class SpatialAuditOutboxSchema
{
    public static async Task ApplyAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Harborline.Api.LocalNodeHost.Data.Search.NodeLocalSearchDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsHistoryTable(
                Harborline.Api.LocalNodeHost.Data.Search.NodeLocalSearchDbContext.MigrationsHistoryTableName))
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var search = new Harborline.Api.LocalNodeHost.Data.Search.NodeLocalSearchDbContext(options);
        await search.Database.MigrateAsync();
    }
}
