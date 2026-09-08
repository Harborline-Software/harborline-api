using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;


namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;


/// <summary>
/// Wave-5 durable-mint conformance against real SQLite (ADR 0101 Rev 3.2; ADR 0168 OQ-1 ruling):
/// fail-closed refusal without a positively-asserted home claim, the fenced signed mint, the
/// audit obligation on BOTH D2-A6 paths (a mint producing no audit event fails these asserts),
/// quarantine-survives-rollback on BOTH paths with the losing content retrievable, and restart
/// durability (the tip derives from the store, not process memory).
/// </summary>
[Collection(HomeEpochFenceStaticHookCollection.Name)]
public sealed class SpatialFrameDescriptorMintTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant-spatial-host");
    private static readonly RegistryEntityId Anchor = new("hull-001");

    /// <summary>The authorized read posture (the route's spatial:read gate stands in for it here) —
    /// unseals the governed cells and owes one Audit@Read row per unsealed row.</summary>
    private static readonly SpatialFrameReadContext Privileged =
        SpatialFrameReadContext.PrivilegedUnseal("test-principal");

    private string _dir = null!;
    private ServiceProvider _provider = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodePrincipalSigner _signer = null!;
    private IRegistryAuditLog _audit = null!;
    private SpatialFramePiiFieldSealer _sealer = null!;
    private NodeEfSpatialFrameDescriptorPort _port = null!;
    private FoundationBackedSpatialFrameDescriptorStore _store = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-spatial-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "spatial-test.db")};Pooling=False";

        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        _signer = new NodePrincipalSigner(seed);

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, SpatialFrameEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _audit = new InMemoryRegistryAuditLog();
        var rootSeed = new byte[32];
        Random.Shared.NextBytes(rootSeed);
        _sealer = new SpatialFramePiiFieldSealer(
            new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed));
        _port = new NodeEfSpatialFrameDescriptorPort(_factory, _audit, _sealer, _signer, clock: TimeProvider.System);
        _store = new FoundationBackedSpatialFrameDescriptorStore(
            _port,
            new NodeHomeClaimFrameEpochAuthority(_factory, _signer),
            _signer.Signer, clock: TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _signer.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private async Task SeedHomeClaimAsync(string? homeDeviceId = null, long epoch = 1)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Add(new HomeEpochRecord
        {
            TenantId = Tenant.Value,
            EpochNumber = epoch,
            PreviousEpochNumber = epoch - 1,
            HomeDeviceId = homeDeviceId ?? _signer.NodePublicKey,
            PromotionKind = HomePromotionKind.PlannedHandoff,
            IssuedAt = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid(),
            IssuerId = _signer.NodePublicKey,
            Signature = "test-seeded",
        });
        await ctx.SaveChangesAsync();
    }

    private static SpatialFrameMintRequest Request(long previousEpoch = 0) =>
        new(Tenant, Anchor, "hull-datum", previousEpoch,
            AxisConvention: "x-fwd-y-stbd-z-down",
            OriginDescription: "aft perpendicular at baseline",
            LengthUnit: "metre",
            Georeference: null);

    [Fact(DisplayName = "D2-A4(a): with NO HomeEpochRecord the mint REFUSES and nothing persists")]
    public async Task Mint_WithoutHomeClaim_Refuses()
    {
        await Assert.ThrowsAsync<FrameEpochMintRefusedException>(() => _store.MintAsync(Request()));

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Set<SpatialFrameDescriptorRow>().CountAsync());
        Assert.Equal(0, await ctx.Set<SpatialFrameQuarantineRow>().CountAsync());
        Assert.Empty(_audit.ForTenant(Tenant));
    }

    [Fact(DisplayName = "D2-A4(a): a home claim held by ANOTHER device REFUSES the mint")]
    public async Task Mint_HomeClaimOnOtherDevice_Refuses()
    {
        await SeedHomeClaimAsync(homeDeviceId: "some-other-device");
        await Assert.ThrowsAsync<FrameEpochMintRefusedException>(() => _store.MintAsync(Request()));
    }

    [Fact(DisplayName = "D2-A4(b): the enforcing check is the in-transaction re-read — a pre-flight-granting authority alone cannot mint")]
    public async Task Mint_EnforcedInTransaction_EvenWhenPreflightGrants()
    {
        // Wire the store with an always-granting pre-flight authority; the port's in-transaction
        // re-read must still refuse (no home record exists).
        var permissive = new FoundationBackedSpatialFrameDescriptorStore(
            _port, new AlwaysGrantingAuthority(), _signer.Signer, clock: TimeProvider.System);
        await Assert.ThrowsAsync<FrameEpochMintRefusedException>(() => permissive.MintAsync(Request()));
    }

    [Fact(DisplayName = "Signed mint: epoch series 1,2; grantingHomeEpoch bound; audit event per mint with triple-only detail")]
    public async Task Mint_WithHomeClaim_MintsSignedSeries_AndAudits()
    {
        await SeedHomeClaimAsync(epoch: 5);

        var first = await _store.MintAsync(Request(previousEpoch: 0));
        var second = await _store.MintAsync(Request(previousEpoch: 1));

        Assert.Equal(1, first.FrameEpoch);
        Assert.Equal(2, second.FrameEpoch);
        Assert.Equal(5, first.Attestation.GrantingHomeEpoch);
        Assert.Equal(_signer.NodePublicKey, first.Attestation.HomeDeviceId);
        Assert.Equal(_signer.NodePublicKey, first.Attestation.Issuer);

        // Round-trips from the durable rows. A REDACTED read suffices for series shape — and
        // must withhold the governed cells AND the hash/signature oracles over them (F2).
        var series = await _store.ListAsync(Tenant, Anchor, "hull-datum", SpatialFrameReadContext.Redacted);
        Assert.Equal(2, series.Count);
        Assert.All(series, d =>
        {
            Assert.Null(d.OriginDescription);
            Assert.Null(d.Attestation.Signature);
            Assert.Null(d.Attestation.ContentHash);
        });

        // [A11] — a mint producing no audit event fails here: exactly one Minted event per mint,
        // Detail = identity triple ONLY (no originDescription, no ordinates).
        var events = _audit.ForTenant(Tenant)
            .Where(e => e.Op == RegistryOp.SpatialFrameDescriptorMinted).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.StartsWith("anchor=hull-001;frameCode=hull-datum;frameEpoch=", e.Detail);
            Assert.DoesNotContain("aft perpendicular", e.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact(DisplayName = "D2-A6 layer-2: a stale mint commits ONLY the quarantine, audits the conflict, and the losing content is retrievable")]
    public async Task Mint_Layer2Conflict_QuarantinesAndAudits()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request(previousEpoch: 0)); // tip = 1

        var rejected = await Assert.ThrowsAsync<SpatialFrameEpochMintRejectedException>(
            () => _store.MintAsync(Request(previousEpoch: 0))); // stale belief

        Assert.Equal(SpatialFrameQuarantineReason.EpochConflict, rejected.Reason);

        // No second descriptor persisted; the quarantine row committed and survives.
        // (Redacted — this assertion needs the series COUNT, not the governed cells.)
        var series = await _store.ListAsync(Tenant, Anchor, "hull-datum", SpatialFrameReadContext.Redacted);
        Assert.Single(series);
        var quarantined = Assert.Single(await _store.ListQuarantinedAsync(Tenant, Anchor, "hull-datum", Privileged));
        Assert.Equal(rejected.QuarantineId, quarantined.Id);
        Assert.Equal("aft perpendicular at baseline", quarantined.OriginDescription); // retrievable
        Assert.Equal(1, quarantined.TipEpochAtDetection);

        var conflictEvents = _audit.ForTenant(Tenant)
            .Where(e => e.Op == RegistryOp.SpatialFrameDescriptorConflictDetected).ToList();
        var conflictEvent = Assert.Single(conflictEvents);
        Assert.Equal("anchor=hull-001;frameCode=hull-datum;frameEpoch=1", conflictEvent.Detail);
    }

    [Fact(DisplayName = "D2-A6 layer-3: the PK backstop rolls the mint back, quarantines in a SECOND committed transaction, and audits")]
    public async Task Mint_Layer3PkBackstop_QuarantineSurvivesRollback()
    {
        await SeedHomeClaimAsync();
        var existing = await _store.MintAsync(Request(previousEpoch: 0)); // epoch 1 exists

        // Drive the port directly with a defective callback that stages a duplicate epoch —
        // simulating the store defect the composite PK is the deterministic backstop for.
        var rejected = await Assert.ThrowsAsync<SpatialFrameEpochMintRejectedException>(
            () => _port.MintAsync(
                new SpatialFrameMintCommand(Tenant, Anchor, "hull-datum"),
                (context, _) => ValueTask.FromResult(SpatialFrameMintStaging.Mint(
                    existing with
                    {
                        OriginDescription = "defective duplicate mint",
                        Attestation = existing.Attestation with { Nonce = Guid.NewGuid() },
                    }))));

        Assert.Equal(SpatialFrameQuarantineReason.StorageConflict, rejected.Reason);

        // The rolled-back descriptor did not persist; the quarantine committed separately.
        var series = await _store.ListAsync(Tenant, Anchor, "hull-datum", Privileged);
        Assert.Single(series);
        Assert.Equal("aft perpendicular at baseline", series[0].OriginDescription);
        var quarantined = (await _store.ListQuarantinedAsync(Tenant, Anchor, "hull-datum", Privileged))
            .Single(q => q.Reason == SpatialFrameQuarantineReason.StorageConflict);
        Assert.Equal("defective duplicate mint", quarantined.OriginDescription); // retrievable

        Assert.Contains(_audit.ForTenant(Tenant),
            e => e.Op == RegistryOp.SpatialFrameDescriptorConflictDetected);
    }

    [Fact(DisplayName = "Layer-3 catch is UNIQUE-violation-only: a non-unique DbUpdateException rethrows and does NOT quarantine")]
    public async Task Mint_NonUniqueDbUpdateException_DoesNotQuarantine()
    {
        await SeedHomeClaimAsync();
        var template = await _store.MintAsync(Request(previousEpoch: 0));

        // Stage a row violating NOT NULL (AxisConvention) at a FRESH epoch — a DbUpdateException
        // that is NOT the composite-PK backstop. It must rethrow untouched: a false quarantine
        // plus an irreversible Op.Reject journal row would misclassify an environmental failure.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => _port.MintAsync(
                new SpatialFrameMintCommand(Tenant, Anchor, "hull-datum"),
                (context, _) => ValueTask.FromResult(SpatialFrameMintStaging.Mint(
                    template with
                    {
                        FrameEpoch = context.TipEpoch + 1,
                        AxisConvention = null!,
                    }))));

        Assert.Empty(await _store.ListQuarantinedAsync(
            Tenant, Anchor, "hull-datum", SpatialFrameReadContext.Redacted));
        Assert.DoesNotContain(_audit.ForTenant(Tenant),
            e => e.Op == RegistryOp.SpatialFrameDescriptorConflictDetected);
    }

    [Fact(DisplayName = "Row-alone re-verification: contentHash recomputes and the Ed25519 signature verifies from the DB round-tripped row")]
    public async Task Mint_WithGeoreference_RowAloneReverifies()
    {
        await SeedHomeClaimAsync(epoch: 7);
        var geo = new SpatialFrameGeoreference(
            ObservedAt: "2026-08-05T12:34:56Z",
            GeodeticCrs: "EPSG:4979",
            OriginPosition: new[] { 51.500123, -0.250456, 12.75 },
            Orientation: new[] { 0.0, 0.7071067811865476, 0.0, 0.7071067811865476 },
            PoseBasis: "enu");
        await _store.MintAsync(Request(previousEpoch: 0) with { Georeference = geo });

        // Reload from the durable rows — everything below uses ONLY the round-tripped record.
        var reloaded = Assert.Single(await _store.ListAsync(Tenant, Anchor, "hull-datum", Privileged));

        // (a) The stored contentHash recomputes from the reloaded defining fields.
        var recomputed = SpatialFrameContentHashing.Compute(
            reloaded.AxisConvention, reloaded.OriginDescription!, reloaded.LengthUnit, reloaded.Georeference);
        Assert.Equal(reloaded.Attestation.ContentHash, recomputed);

        // (b) The Ed25519 signature verifies over the payload reconstructed from the row alone.
        var payload = new SpatialFrameMintSignaturePayload(
            TenantId: reloaded.TenantId.Value,
            Anchor: reloaded.Anchor.Value,
            FrameCode: reloaded.FrameCode,
            FrameEpoch: reloaded.FrameEpoch,
            PreviousEpoch: reloaded.Attestation.PreviousEpoch,
            HomeDeviceId: reloaded.Attestation.HomeDeviceId,
            GrantingHomeEpoch: reloaded.Attestation.GrantingHomeEpoch,
            ContentHash: reloaded.Attestation.ContentHash!);
        var signable = CanonicalJson.SerializeSignable(
            payload,
            PrincipalId.FromBase64Url(reloaded.Attestation.Issuer),
            reloaded.Attestation.IssuedAt,
            reloaded.Attestation.Nonce);
        Assert.True(KeyPair.VerifyRaw(
            PrincipalId.FromBase64Url(reloaded.Attestation.Issuer).AsSpan(),
            signable,
            Signature.FromBase64Url(reloaded.Attestation.Signature!).AsSpan()));

        // The georeference itself round-trips losslessly (the hash equality already implies it).
        Assert.NotNull(reloaded.Georeference);
        Assert.Equal(geo.ObservedAt, reloaded.Georeference!.ObservedAt);
        Assert.Equal(geo.GeodeticCrs, reloaded.Georeference.GeodeticCrs);
        Assert.Equal(geo.OriginPosition, reloaded.Georeference.OriginPosition);
        Assert.Equal(geo.Orientation, reloaded.Georeference.Orientation);
        Assert.Equal(geo.PoseBasis, reloaded.Georeference.PoseBasis);
        Assert.Equal(7, reloaded.Attestation.GrantingHomeEpoch);
    }

    [Fact(DisplayName = "Durability: the epoch tip derives from the store, not process memory (restart-safe)")]
    public async Task Mint_TipSurvivesNewPortInstance()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request(previousEpoch: 0));

        // A fresh port/store (simulated restart) continues the series instead of reissuing epoch 1.
        var freshPort = new NodeEfSpatialFrameDescriptorPort(_factory, _audit, _sealer, _signer, clock: TimeProvider.System);
        var freshStore = new FoundationBackedSpatialFrameDescriptorStore(
            freshPort, new NodeHomeClaimFrameEpochAuthority(_factory, _signer), _signer.Signer, clock: TimeProvider.System);
        var next = await freshStore.MintAsync(Request(previousEpoch: 1));
        Assert.Equal(2, next.FrameEpoch);
    }

    private sealed class AlwaysGrantingAuthority : IFrameEpochAuthority
    {
        public Task AssertMintAuthorityAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
