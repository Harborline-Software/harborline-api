using System;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MD-2 — the <see cref="HomeEpochRecord"/> bump-on-promotion path (the joint ADR 0113+0117 amendment;
/// ADR 0135 §D3). Proves the roster-signed bump is FORGE-PROOF, the G-5 multi-actor floor is enforced for
/// the recovery-failover case, and the monotonic advance rejects non-increasing / replayed bumps.
/// </summary>
/// <remarks>
/// These tests build a REAL <see cref="LocalNodeDbContext"/> over on-disk SQLite (the production EF stack)
/// with the production <see cref="HomeEpochEntityModule"/>, and the REAL foundation Ed25519 signer/verifier
/// (<see cref="KeyPair"/> → <see cref="Ed25519Signer"/> → <see cref="Ed25519Verifier"/>) — no test crypto
/// double. A forged bump fails the SAME Ed25519 check the trust roster uses for admissions.
/// </remarks>
public sealed class HomeEpochStoreTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    private static readonly string Tenant =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId).Value;

    // Two distinct in-roster admins (the proposer + a co-approver) and a rogue/unrelated key.
    private readonly KeyPair _adminA = KeyPair.Generate();
    private readonly KeyPair _adminB = KeyPair.Generate();
    private readonly KeyPair _rogue = KeyPair.Generate();

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-home-epoch-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "home-epoch.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        _adminA.Dispose();
        _adminB.Dispose();
        _rogue.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    private HomeEfHomeEpochStore Store() => new(_factory, new Ed25519Verifier());

    // ── happy paths ───────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "MD-2: a planned-handoff bump signed by one in-roster admin advances the home epoch")]
    public async Task PlannedHandoff_SingleAdmin_Advances()
    {
        var store = Store();
        Assert.Null(await store.GetCurrentEpochAsync(Tenant)); // genesis state — no home epoch yet

        var bump = SignedBump(_adminA, epoch: 1, previous: 0, home: "device-1", HomePromotionKind.PlannedHandoff);
        await store.AdvanceAsync(bump);

        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.NotNull(current);
        Assert.Equal(1, current!.EpochNumber);
        Assert.Equal("device-1", current.HomeDeviceId);
    }

    [Fact(DisplayName = "MD-2: a recovery-failover bump with TWO distinct co-approver signatures advances (G-5 floor satisfied)")]
    public async Task RecoveryFailover_MultiActor_Advances()
    {
        var store = Store();
        await store.AdvanceAsync(SignedBump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        // Old home (device-1) is lost; promote device-2 via recovery-failover — needs a 2nd distinct admin.
        var bump = SignedBump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, coApprover: _adminB);
        await store.AdvanceAsync(bump);

        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.Equal(2, current!.EpochNumber);
        Assert.Equal("device-2", current.HomeDeviceId);
    }

    // ── forge-proof ──────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "MD-2 forge-proof: a bump whose HomeDeviceId was tampered after signing is REJECTED (signature no longer verifies)")]
    public async Task TamperedHomeDevice_Rejected()
    {
        var store = Store();
        // Sign a bump aiming the home at device-1, then tamper the home to the attacker's device-X.
        var honest = SignedBump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff);
        var forged = new HomeEpochRecord
        {
            TenantId = honest.TenantId,
            EpochNumber = honest.EpochNumber,
            PreviousEpochNumber = honest.PreviousEpochNumber,
            HomeDeviceId = "device-X-attacker", // tampered — NOT what adminA signed
            PromotionKind = honest.PromotionKind,
            IssuedAt = honest.IssuedAt,
            Nonce = honest.Nonce,
            IssuerId = honest.IssuerId,
            Signature = honest.Signature, // the signature over device-1 — now stale
        };

        var ex = await Assert.ThrowsAsync<HomeEpochAdvanceRejectedException>(() => store.AdvanceAsync(forged));
        Assert.Contains("invalid proposer signature", ex.Message);
        Assert.Null(await store.GetCurrentEpochAsync(Tenant)); // nothing persisted
    }

    [Fact(DisplayName = "MD-2 forge-proof: a bump signed by a rogue (non-roster) key with a forged issuer claim is REJECTED")]
    public async Task RogueIssuerClaim_Rejected()
    {
        var store = Store();
        // The rogue signs, but stamps adminA's public key as the issuer (claiming to be adminA).
        var payload = HomeEpochSignaturePayload.For(Tenant, 1, 0, "device-rogue", HomePromotionKind.PlannedHandoff);
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();
        var op = await new Ed25519Signer(_rogue).SignAsync(payload, issuedAt, nonce);

        var forged = new HomeEpochRecord
        {
            TenantId = Tenant,
            EpochNumber = 1,
            PreviousEpochNumber = 0,
            HomeDeviceId = "device-rogue",
            PromotionKind = HomePromotionKind.PlannedHandoff,
            IssuedAt = issuedAt,
            Nonce = nonce,
            IssuerId = _adminA.PrincipalId.ToBase64Url(), // claims to be adminA…
            Signature = op.Signature.ToBase64Url(),        // …but signed by the rogue key
        };

        var ex = await Assert.ThrowsAsync<HomeEpochAdvanceRejectedException>(() => store.AdvanceAsync(forged));
        Assert.Contains("invalid proposer signature", ex.Message);
        Assert.Null(await store.GetCurrentEpochAsync(Tenant));
    }

    // ── G-5 multi-actor floor ────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "MD-2 G-5: a recovery-failover bump with NO co-approver is REJECTED (multi-actor floor)")]
    public async Task RecoveryFailover_NoCoApprover_Rejected()
    {
        var store = Store();
        await store.AdvanceAsync(SignedBump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        var soloFailover = SignedBump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover); // no co-approver
        var ex = await Assert.ThrowsAsync<HomeEpochAdvanceRejectedException>(() => store.AdvanceAsync(soloFailover));
        Assert.Contains("multi-actor floor", ex.Message);
        Assert.Equal(1, (await store.GetCurrentEpochAsync(Tenant))!.EpochNumber); // still epoch 1
    }

    [Fact(DisplayName = "MD-2 G-5: a recovery-failover bump whose co-approver is the SAME admin as the proposer is REJECTED (no self-approval)")]
    public async Task RecoveryFailover_SelfCoApproval_Rejected()
    {
        var store = Store();
        await store.AdvanceAsync(SignedBump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        // adminA signs BOTH the proposer and the co-approver slot — not two distinct admins.
        var selfApproved = SignedBump(_adminA, 2, 1, "device-2", HomePromotionKind.RecoveryFailover, coApprover: _adminA);
        var ex = await Assert.ThrowsAsync<HomeEpochAdvanceRejectedException>(() => store.AdvanceAsync(selfApproved));
        Assert.Contains("identical to the proposer", ex.Message);
        Assert.Equal(1, (await store.GetCurrentEpochAsync(Tenant))!.EpochNumber);
    }

    // ── monotonicity ─────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "MD-2 monotonic: a bump that re-uses or skips an epoch number is REJECTED (strict +1)")]
    public async Task NonMonotonicBump_Rejected()
    {
        var store = Store();
        await store.AdvanceAsync(SignedBump(_adminA, 1, 0, "device-1", HomePromotionKind.PlannedHandoff));

        // Skip-ahead (epoch 3 when current is 1) — rejected.
        var skip = SignedBump(_adminA, 3, 1, "device-2", HomePromotionKind.PlannedHandoff);
        var skipEx = await Assert.ThrowsAsync<HomeEpochAdvanceRejectedException>(() => store.AdvanceAsync(skip));
        Assert.Contains("not strictly monotonic", skipEx.Message);

        // Re-use epoch 1 — rejected.
        var reuse = SignedBump(_adminA, 1, 0, "device-2", HomePromotionKind.PlannedHandoff);
        await Assert.ThrowsAsync<HomeEpochAdvanceRejectedException>(() => store.AdvanceAsync(reuse));

        Assert.Equal(1, (await store.GetCurrentEpochAsync(Tenant))!.EpochNumber);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Produces a fully roster-signed <see cref="HomeEpochRecord"/> (proposer + optional distinct
    /// co-approver), each signature over the canonical <see cref="HomeEpochSignaturePayload"/>.</summary>
    private HomeEpochRecord SignedBump(
        KeyPair proposer, long epoch, long previous, string home, HomePromotionKind kind,
        KeyPair? coApprover = null)
    {
        var payload = HomeEpochSignaturePayload.For(Tenant, epoch, previous, home, kind);
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();

        var proposerSig = new Ed25519Signer(proposer).SignAsync(payload, issuedAt, nonce)
            .AsTask().GetAwaiter().GetResult();

        string? coId = null;
        string? coSig = null;
        if (coApprover is not null)
        {
            coId = coApprover.PrincipalId.ToBase64Url();
            coSig = new Ed25519Signer(coApprover).SignAsync(payload, issuedAt, nonce)
                .AsTask().GetAwaiter().GetResult().Signature.ToBase64Url();
        }

        return new HomeEpochRecord
        {
            TenantId = Tenant,
            EpochNumber = epoch,
            PreviousEpochNumber = previous,
            HomeDeviceId = home,
            PromotionKind = kind,
            IssuedAt = issuedAt,
            Nonce = nonce,
            IssuerId = proposer.PrincipalId.ToBase64Url(),
            Signature = proposerSig.Signature.ToBase64Url(),
            CoApproverIssuerId = coId,
            CoApproverSignature = coSig,
        };
    }
}
