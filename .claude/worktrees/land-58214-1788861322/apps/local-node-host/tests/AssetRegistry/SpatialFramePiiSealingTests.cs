using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// CP-4 governed-field sealing conformance (ADR 0101 Rev 3.2 Wave 5 precondition 3; ADR 0168
/// D2-A8): the two governed columns — <c>OriginDescription</c> + <c>GeoreferenceJson</c> — hold the
/// tenant-DEK <c>EncryptedField</c> envelope at rest on BOTH the descriptor and quarantine rows;
/// reads decrypt back to cleartext; contentHash + the mint signature still verify from the
/// decrypted row (the signed preimage is NEVER sealed — the pinned trap); and a DEK-less port
/// fails CLOSED on both the write and the read path.
/// </summary>
[Collection(HomeEpochFenceStaticHookCollection.Name)]
public sealed class SpatialFramePiiSealingTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant-spatial-sealing");
    private static readonly RegistryEntityId Anchor = new("hull-007");

    /// <summary>The authorized read posture (the route's spatial:read gate stands in for it here) —
    /// unseals the governed cells and owes one Audit@Read row per unsealed row.</summary>
    private static readonly SpatialFrameReadContext Privileged =
        SpatialFrameReadContext.PrivilegedUnseal("test-principal");
    private const string Origin = "aft perpendicular at baseline";

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
        _dir = Path.Combine(Path.GetTempPath(), "harborline-spatial-sealing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "sealing-test.db")};Pooling=False";

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

        var rootSeed = new byte[32];
        Random.Shared.NextBytes(rootSeed);
        _audit = new InMemoryRegistryAuditLog();
        _sealer = new SpatialFramePiiFieldSealer(new RootSeedTenantKeyProvider(rootSeed));
        _port = new NodeEfSpatialFrameDescriptorPort(_factory, _audit, _sealer, _signer, clock: TimeProvider.System);
        _store = new FoundationBackedSpatialFrameDescriptorStore(
            _port, new NodeHomeClaimFrameEpochAuthority(_factory, _signer), _signer.Signer, clock: TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _signer.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private async Task SeedHomeClaimAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Add(new HomeEpochRecord
        {
            TenantId = Tenant.Value,
            EpochNumber = 1,
            PreviousEpochNumber = 0,
            HomeDeviceId = _signer.NodePublicKey,
            PromotionKind = HomePromotionKind.PlannedHandoff,
            IssuedAt = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid(),
            IssuerId = _signer.NodePublicKey,
            Signature = "test-seeded",
        });
        await ctx.SaveChangesAsync();
    }

    private static SpatialFrameGeoreference Geo() => new(
        ObservedAt: "2026-08-05T12:34:56Z",
        GeodeticCrs: "EPSG:4979",
        OriginPosition: new[] { 51.500123, -0.250456, 12.75 },
        Orientation: null,
        PoseBasis: null);

    private static SpatialFrameMintRequest Request(long previousEpoch = 0) =>
        new(Tenant, Anchor, "hull-datum", previousEpoch,
            AxisConvention: "x-fwd-y-stbd-z-down",
            OriginDescription: Origin,
            LengthUnit: "metre",
            Georeference: Geo());

    /// <summary>Asserts a raw column value is a well-formed EncryptedField envelope, not cleartext.</summary>
    private static void AssertSealed(string stored, string cleartext)
    {
        Assert.DoesNotContain(cleartext, stored, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(stored);
        Assert.True(doc.RootElement.TryGetProperty("ct", out _), "envelope JSON must carry \"ct\"");
        Assert.True(doc.RootElement.TryGetProperty("nonce", out _), "envelope JSON must carry \"nonce\"");
        Assert.Equal(2, doc.RootElement.GetProperty("kv").GetInt32());
    }

    [Fact(DisplayName = "Seal-at-rest: the raw descriptor columns hold the envelope, never the prose; contentHash stays cleartext")]
    public async Task Mint_RawDescriptorColumns_AreSealed()
    {
        await SeedHomeClaimAsync();
        var minted = await _store.MintAsync(Request());

        await using var ctx = await _factory.CreateDbContextAsync();
        var raw = Assert.Single(await ctx.Set<SpatialFrameDescriptorRow>().AsNoTracking().ToListAsync());

        AssertSealed(raw.OriginDescription, Origin);
        Assert.NotNull(raw.GeoreferenceJson);
        AssertSealed(raw.GeoreferenceJson!, "EPSG:4979");

        // The signed preimage is NEVER sealed (pinned trap): cleartext lowercase-hex SHA-256.
        Assert.Equal(minted.Attestation.ContentHash, raw.ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", raw.ContentHash);
        // Identity triple + axisConvention stay cleartext for keying/display (0168 D2-A8).
        Assert.Equal("hull-datum", raw.FrameCode);
        Assert.Equal("x-fwd-y-stbd-z-down", raw.AxisConvention);
    }

    [Fact(DisplayName = "Decrypt-at-read round-trip: the store returns cleartext, and contentHash + signature verify from the decrypted row")]
    public async Task Read_RoundTrips_AndReverifies()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request());

        var reloaded = Assert.Single(await _store.ListAsync(Tenant, Anchor, "hull-datum", Privileged));
        Assert.Equal(Origin, reloaded.OriginDescription);
        Assert.Equal("EPSG:4979", reloaded.Georeference!.GeodeticCrs);

        // The row-alone re-verification invariant: recompute the hash over the DECRYPTED fields.
        var recomputed = SpatialFrameContentHashing.Compute(
            reloaded.AxisConvention, reloaded.OriginDescription!, reloaded.LengthUnit, reloaded.Georeference);
        Assert.Equal(reloaded.Attestation.ContentHash, recomputed);
    }

    [Fact(DisplayName = "Quarantine row sealed too: raw losing content is enveloped; the store read decrypts it")]
    public async Task Quarantine_RawColumns_AreSealed()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request(previousEpoch: 0));
        await Assert.ThrowsAsync<SpatialFrameEpochMintRejectedException>(
            () => _store.MintAsync(Request(previousEpoch: 0))); // stale belief → layer-2 quarantine

        await using var ctx = await _factory.CreateDbContextAsync();
        var raw = Assert.Single(await ctx.Set<SpatialFrameQuarantineRow>().AsNoTracking().ToListAsync());
        AssertSealed(raw.OriginDescription, Origin);
        Assert.NotNull(raw.GeoreferenceJson);
        AssertSealed(raw.GeoreferenceJson!, "EPSG:4979");

        var quarantined = Assert.Single(await _store.ListQuarantinedAsync(Tenant, Anchor, "hull-datum", Privileged));
        Assert.Equal(Origin, quarantined.OriginDescription);
    }

    [Fact(DisplayName = "DEK-absent write path fails CLOSED: the mint throws and NOTHING persists (no cleartext fallback)")]
    public async Task DekAbsent_Write_FailsClosed()
    {
        await SeedHomeClaimAsync();
        var keylessPort = new NodeEfSpatialFrameDescriptorPort(
            _factory, _audit, new SpatialFramePiiFieldSealer(new ThrowingKeyProvider()), _signer, clock: TimeProvider.System);
        var keylessStore = new FoundationBackedSpatialFrameDescriptorStore(
            keylessPort, new NodeHomeClaimFrameEpochAuthority(_factory, _signer), _signer.Signer, clock: TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => keylessStore.MintAsync(Request()));

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Set<SpatialFrameDescriptorRow>().CountAsync());
        Assert.Equal(0, await ctx.Set<SpatialFrameQuarantineRow>().CountAsync());
    }

    [Fact(DisplayName = "DEK-absent read path fails CLOSED: sealed rows are unreadable, never returned as raw column bytes")]
    public async Task DekAbsent_Read_FailsClosed()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request());

        var keylessPort = new NodeEfSpatialFrameDescriptorPort(
            _factory, _audit, new SpatialFramePiiFieldSealer(new ThrowingKeyProvider()), _signer, clock: TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => keylessPort.ListAsync(Tenant, Anchor, "hull-datum", Privileged));
    }

    [Fact(DisplayName = "Tamper/garbage in a governed column fails CLOSED on read — raw bytes are never surfaced as cleartext")]
    public async Task GarbageColumn_Read_FailsClosed()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request());

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE spatial_frame_descriptors SET OriginDescription = 'not-an-envelope'");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _port.ListAsync(Tenant, Anchor, "hull-datum", Privileged));
    }

    // ── Store-level Redact@Read + Audit@Read (card 3778; CIC ruling 2026-08-06) ────────────

    [Fact(DisplayName = "Redact@Read: a redacted read withholds BOTH governed cells (null) and appends NO unseal-audit row")]
    public async Task RedactedRead_WithholdsGovernedCells_AndDoesNotAudit()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request());
        var before = UnsealEvents().Count;

        var redacted = Assert.Single(
            await _store.ListAsync(Tenant, Anchor, "hull-datum", SpatialFrameReadContext.Redacted));

        Assert.Null(redacted.OriginDescription);
        Assert.Null(redacted.Georeference);
        // F2: the cleartext contentHash + signature are confirmation oracles over the withheld
        // cells (guess -> hash -> verify), so the redacted read withholds them too. Read-boundary
        // only — the stored row keeps both (asserted sealed-at-rest above in this suite).
        Assert.Null(redacted.Attestation.ContentHash);
        Assert.Null(redacted.Attestation.Signature);
        // Cleartext identity/keying columns still surface.
        Assert.Equal("hull-datum", redacted.FrameCode);
        Assert.Equal(_signer.NodePublicKey, redacted.Attestation.Issuer);
        // Audit rows are bounded to ACTUAL unsealing — a redacted read owes none.
        Assert.Equal(before, UnsealEvents().Count);

        var redactedFound = await _store.FindAsync(
            Tenant, Anchor, "hull-datum", 1, SpatialFrameReadContext.Redacted);
        Assert.Null(redactedFound!.OriginDescription);
    }

    [Fact(DisplayName = "Audit@Read: one identity-triple audit row per ACTUAL unsealing, Op=PiiUnsealed, PII never in the row")]
    public async Task PrivilegedRead_AppendsOneTripleOnlyAuditRow_PerUnsealedRow()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request());
        await _store.MintAsync(Request(previousEpoch: 1)); // second epoch — N=2
        var before = UnsealEvents().Count;

        // F5a: N=2 rows unsealed -> exactly TWO audit rows, one per row with its OWN epoch in the
        // triple (a per-call aggregate row would fail this).
        var unsealed = await _store.ListAsync(Tenant, Anchor, "hull-datum", Privileged);
        Assert.Equal(2, unsealed.Count);
        Assert.All(unsealed, d => Assert.Equal(Origin, d.OriginDescription));

        var rows = UnsealEvents().Skip(before).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(RegistryOp.SpatialFrameDescriptorPiiUnsealed, r.Op);
            Assert.Equal("test-principal", r.ActorRef);
            Assert.DoesNotContain(Origin, r.Detail!, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            new[]
            {
                $"anchor={Anchor.Value};frameCode=hull-datum;frameEpoch=1",
                $"anchor={Anchor.Value};frameCode=hull-datum;frameEpoch=2",
            },
            rows.Select(r => r.Detail).OrderBy(d => d).ToArray());

        // The quarantine family rides the same seam: a privileged quarantine read audits too,
        // keyed on the ATTEMPTED epoch.
        await Assert.ThrowsAsync<SpatialFrameEpochMintRejectedException>(
            () => _store.MintAsync(Request(previousEpoch: 0)));
        var mid = UnsealEvents().Count;
        Assert.Single(await _store.ListQuarantinedAsync(Tenant, Anchor, "hull-datum", Privileged));
        Assert.Single(UnsealEvents().Skip(mid));
    }

    [Fact(DisplayName = "Audit failure on a privileged read is FATAL: the read throws and the PII is not surfaced; a redacted read still works")]
    public async Task PrivilegedRead_WhenAuditAppendFails_WithholdsTheRead()
    {
        await SeedHomeClaimAsync();
        await _store.MintAsync(Request());

        var auditlessPort = new NodeEfSpatialFrameDescriptorPort(
            _factory, new ThrowingAuditLog(), _sealer, _signer, clock: TimeProvider.System);

        // Surfacing PII IS the grant — a grant that cannot be audited is not surfaced.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auditlessPort.ListAsync(Tenant, Anchor, "hull-datum", Privileged));

        // The redacted posture owes no audit row, so it survives a dead audit substrate.
        var redacted = Assert.Single(
            await auditlessPort.ListAsync(Tenant, Anchor, "hull-datum", SpatialFrameReadContext.Redacted));
        Assert.Null(redacted.OriginDescription);
    }

    private IReadOnlyList<RegistryAuditEvent> UnsealEvents() =>
        _audit.ForSubject(Tenant, $"spatial-frame:{Anchor.Value}:hull-datum")
            .Where(e => e.Op == RegistryOp.SpatialFrameDescriptorPiiUnsealed)
            .ToList();

    /// <summary>A dead audit substrate — every append fails.</summary>
    private sealed class ThrowingAuditLog : IRegistryAuditLog
    {
        public RegistryAuditEvent Append(
            TenantId tenant, string subject, RegistryOp op,
            Harborline.Api.Foundation.Assets.Common.Instant at,
            string? actorRef = null, string? detail = null)
            => throw new InvalidOperationException("audit substrate unavailable (test)");

        public IReadOnlyList<RegistryAuditEvent> ForSubject(TenantId tenant, string subject)
            => Array.Empty<RegistryAuditEvent>();

        public IReadOnlyList<RegistryAuditEvent> ForTenant(TenantId tenant)
            => Array.Empty<RegistryAuditEvent>();

        public bool VerifyChain(TenantId tenant, string subject) => true;
    }

    /// <summary>DEK-absent substrate: every derivation fails (e.g. no root seed provisioned).</summary>
    private sealed class ThrowingKeyProvider : ITenantKeyProvider
    {
        public Task<ReadOnlyMemory<byte>> DeriveKeyAsync(TenantId tenant, string purpose, CancellationToken ct)
            => throw new InvalidOperationException("no tenant DEK available (test: DEK-absent substrate)");

        public Task<ReadOnlyMemory<byte>> DeriveSubjectKeyAsync(
            TenantId tenant,
            Harborline.Api.Foundation.Recovery.Erasure.SubjectId subject,
            string purpose,
            Harborline.Api.Foundation.Recovery.Erasure.ISubjectErasureRegistry erasure,
            CancellationToken ct)
            => throw new InvalidOperationException("no tenant DEK available (test: DEK-absent substrate)");
    }
}
