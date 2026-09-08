using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Card #3702 — the GENESIS home-epoch write (ADR 0101 Rev 3.2 precondition 2 / ADR 0168 D2-A4(a)).
/// Proves the install-time genesis <see cref="HomeEpochRecord"/> is written on first enrollment with
/// the dedicated <see cref="HomePromotionKind.Genesis"/> kind, is idempotent across re-runs and
/// restarts, survives a two-writer race on both loser shapes, is signed by the node principal
/// signer, and that the ORIGIN GATE refuses to self-genesis on a joiner (founder-key mismatch).
/// </summary>
/// <remarks>
/// Same substrate discipline as <see cref="HomeEpochStoreTests"/>: a REAL
/// <see cref="LocalNodeDbContext"/> over on-disk SQLite with the production
/// <see cref="HomeEpochEntityModule"/> and the REAL foundation Ed25519 signer/verifier — no crypto
/// doubles, and the genesis goes through the store's full fail-closed <c>AdvanceAsync</c> path.
/// </remarks>
public sealed class HomeEpochGenesisTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodePrincipalSigner _nodeSigner = null!;

    private static readonly string Tenant =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId).Value;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-home-epoch-genesis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "home-epoch-genesis.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();

        _nodeSigner = new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32));
    }

    public Task DisposeAsync()
    {
        _nodeSigner.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    private HomeEfHomeEpochStore Store() => new(_factory, new Ed25519Verifier());

    /// <summary>A roster whose genesis founder is signed by <paramref name="founderSigner"/> — the
    /// installing-device shape when it is the node's own signer, the joiner shape when it is not.</summary>
    private static NodeTeamRoster RosterFoundedBy(IOperationSigner founderSigner) =>
        new(MemberRoster.StableGenesis(
            NodeTestActiveTeam.TestTeamId.Value,
            "party-founder",
            founderSigner,
            new Ed25519Verifier()));

    private HostedHomeEpochGenesisService Service(IHomeEpochStore store, NodeTeamRoster roster) =>
        new(store, _nodeSigner, roster, NodeTestActiveTeam.Accessor,
            TimeProvider.System,
            NullLogger<HostedHomeEpochGenesisService>.Instance);

    // ── genesis written on first enrollment ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "3702: first enrollment writes the signed genesis home epoch (epoch 1, previous 0, kind Genesis, home = this node)")]
    public async Task FirstEnrollment_WritesGenesis()
    {
        var store = Store();
        Assert.Null(await store.GetCurrentEpochAsync(Tenant)); // pre-genesis: only an absence

        var wrote = await HomeEpochGenesis.EnsureWrittenAsync(
            store, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey);

        Assert.True(wrote);
        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.NotNull(current);
        Assert.Equal(1L, current!.EpochNumber);
        Assert.Equal(0L, current.PreviousEpochNumber);
        Assert.Equal(_nodeSigner.NodePublicKey, current.HomeDeviceId);
        Assert.Equal(HomePromotionKind.Genesis, current.PromotionKind); // the ADDITIVE genesis kind, not a transfer
        Assert.Equal(_nodeSigner.NodePublicKey, current.IssuerId); // issued by the node identity
        Assert.Null(current.CoApproverIssuerId);                    // single-admin genesis — no G-5 case
        Assert.Null(current.CoApproverSignature);
    }

    [Fact(DisplayName = "3702: the stored genesis row's signature round-trips — reconstruction from the stored fields re-verifies with real Ed25519")]
    public async Task Genesis_Signature_ReVerifies_FromStoredFields()
    {
        var store = Store();
        await HomeEpochGenesis.EnsureWrittenAsync(store, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey);
        var row = await store.GetCurrentEpochAsync(Tenant);

        // Rebuild the canonical payload from the row's STORED fields (via the production payload
        // helper) and verify — proving storage round-trip fidelity of every signed field.
        var payload = HomeEpochSignaturePayload.For(
            row!.TenantId, row.EpochNumber, row.PreviousEpochNumber, row.HomeDeviceId, row.PromotionKind);
        var op = new SignedOperation<HomeEpochSignaturePayload>(
            Payload: payload,
            IssuerId: PrincipalId.FromBase64Url(row.IssuerId),
            IssuedAt: row.IssuedAt,
            Nonce: row.Nonce,
            Signature: Signature.FromBase64Url(row.Signature));
        Assert.True(new Ed25519Verifier().Verify(op));
        Assert.Equal(HomeEpochSignaturePayload.TypeDiscriminator, payload.PayloadType); // 'home-epoch/v1' is in the signed bytes
    }

    [Fact(DisplayName = "3702 domain separator: the same fields under a DIFFERENT payload type do not verify (no cross-protocol replay)")]
    public async Task Genesis_Signature_DoesNotVerify_UnderDifferentPayloadType()
    {
        var store = Store();
        await HomeEpochGenesis.EnsureWrittenAsync(store, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey);
        var row = await store.GetCurrentEpochAsync(Tenant);

        // Identical fields, foreign discriminator — a signature minted for another payload type
        // (or a pre-separator payload shape) must never verify as a home-epoch bump.
        var foreign = HomeEpochSignaturePayload.For(
                row!.TenantId, row.EpochNumber, row.PreviousEpochNumber, row.HomeDeviceId, row.PromotionKind)
            with { PayloadType = "some-other-protocol/v1" };
        var op = new SignedOperation<HomeEpochSignaturePayload>(
            Payload: foreign,
            IssuerId: PrincipalId.FromBase64Url(row.IssuerId),
            IssuedAt: row.IssuedAt,
            Nonce: row.Nonce,
            Signature: Signature.FromBase64Url(row.Signature));
        Assert.False(new Ed25519Verifier().Verify(op));
    }

    // ── idempotency ─────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "3702 idempotent: re-running enrollment does not mint a second genesis (epoch 1 exists -> no-op)")]
    public async Task ReRun_DoesNotMintSecondGenesis()
    {
        var store = Store();
        Assert.True(await HomeEpochGenesis.EnsureWrittenAsync(
            store, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey));
        var first = await store.GetCurrentEpochAsync(Tenant);

        // Re-run — even naming a DIFFERENT device: the existing recorded fact wins.
        Assert.False(await HomeEpochGenesis.EnsureWrittenAsync(
            store, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey));
        Assert.False(await HomeEpochGenesis.EnsureWrittenAsync(
            store, _nodeSigner.Signer, Tenant, "some-other-device"));

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await ctx.Set<HomeEpochRecord>().CountAsync(r => r.TenantId == Tenant));

        var after = await store.GetCurrentEpochAsync(Tenant);
        Assert.Equal(first!.Nonce, after!.Nonce); // the ORIGINAL genesis row, untouched
        Assert.Equal(first.Signature, after.Signature);
    }

    // ── two-writer race: both loser shapes are absorbed, absence still rethrows ─────────────────────

    [Fact(DisplayName = "3702 race: a competitor landing epoch 1 between read and append is absorbed when the store's monotonic reject fires")]
    public async Task Race_StoreReject_Absorbed()
    {
        // The competitor's genesis lands through the REAL store between our read (which saw
        // absence) and our append, so AdvanceAsync re-reads tip=1 and throws the store's own
        // HomeEpochAdvanceRejectedException — the writer must absorb it (an epoch now exists).
        var store = Store();
        var racing = new RacingStore(store, preStage: () => HomeEpochGenesis.EnsureWrittenAsync(
            store, _nodeSigner.Signer, Tenant, "competitor-device"));

        Assert.False(await HomeEpochGenesis.EnsureWrittenAsync(
            racing, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey));

        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.Equal("competitor-device", current!.HomeDeviceId); // the competitor's row won
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await ctx.Set<HomeEpochRecord>().CountAsync(r => r.TenantId == Tenant));
    }

    [Fact(DisplayName = "3702 race: the composite-PK loser surfaces as DbUpdateException (not the store's exception) and is absorbed when an epoch is then visible")]
    public async Task Race_PkBackstop_DbUpdateException_Absorbed()
    {
        // Pre-stage the competitor's epoch-1 row, then run a writer whose FIRST read raced (saw
        // absence) and whose append trips the composite-PK backstop — which EF raises as
        // DbUpdateException, NOT HomeEpochAdvanceRejectedException (precedent:
        // WebSessionSchemaConstraintTests). The post-throw re-read sees the competitor's row.
        var store = Store();
        await HomeEpochGenesis.EnsureWrittenAsync(store, _nodeSigner.Signer, Tenant, "competitor-device");

        var pkLoser = new PkRaceLoserStore(store, () => new DbUpdateException("UNIQUE constraint failed"));
        Assert.False(await HomeEpochGenesis.EnsureWrittenAsync(
            pkLoser, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey));

        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.Equal("competitor-device", current!.HomeDeviceId); // untouched
    }

    [Fact(DisplayName = "3702 race guard: a DbUpdateException with NO epoch visible afterwards is a REAL failure and rethrows")]
    public async Task Race_DbUpdateException_WithNoEpochVisible_Rethrows()
    {
        var store = Store(); // tenant has NO rows — the re-read stays null, so the fault must surface
        var broken = new PkRaceLoserStore(store, () => new DbUpdateException("disk I/O error"));
        await Assert.ThrowsAsync<DbUpdateException>(() => HomeEpochGenesis.EnsureWrittenAsync(
            broken, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey));
    }

    // ── restart persistence ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "3702 restart: the genesis survives a restart (fresh store over the same db) and stays a no-op")]
    public async Task Restart_GenesisPersists_AndStaysNoOp()
    {
        await HomeEpochGenesis.EnsureWrittenAsync(
            Store(), _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey);

        // "Restart": a brand-new store instance over the same on-disk database.
        var restarted = Store();
        var current = await restarted.GetCurrentEpochAsync(Tenant);
        Assert.NotNull(current);
        Assert.Equal(1L, current!.EpochNumber);
        Assert.Equal(_nodeSigner.NodePublicKey, current.HomeDeviceId);

        Assert.False(await HomeEpochGenesis.EnsureWrittenAsync(
            restarted, _nodeSigner.Signer, Tenant, _nodeSigner.NodePublicKey));
    }

    // ── the hosted backstop wrapper + the origin gate ───────────────────────────────────────────────

    [Fact(DisplayName = "3702 host wiring: the hosted backstop writes the genesis for the ACTIVE tenant when this node IS the roster founder, idempotent across starts")]
    public async Task HostedBackstop_WritesForActiveTenant_IdempotentAcrossStarts()
    {
        var store = Store();
        var service = Service(store, RosterFoundedBy(_nodeSigner.Signer)); // founder = this node

        await service.StartAsync(CancellationToken.None);
        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.NotNull(current);
        Assert.Equal(1L, current!.EpochNumber);
        Assert.Equal(_nodeSigner.NodePublicKey, current.HomeDeviceId);

        // Second start (host restart) — no second genesis.
        await service.StartAsync(CancellationToken.None);
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await ctx.Set<HomeEpochRecord>().CountAsync(r => r.TenantId == Tenant));
    }

    [Fact(DisplayName = "3702 origin gate: a JOINER (roster founder key != this node's principal) is REFUSED — no self-genesis for a tenant homed elsewhere")]
    public async Task OriginGate_FounderKeyMismatch_RefusesWrite()
    {
        using var inviter = KeyPair.Generate();
        var store = Store();
        // The adopted roster's chain root is the INVITER's key, not this node's — the joiner posture.
        var service = Service(store, RosterFoundedBy(new Ed25519Signer(inviter)));

        await service.StartAsync(CancellationToken.None);

        Assert.Null(await store.GetCurrentEpochAsync(Tenant)); // refused: nothing written
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Set<HomeEpochRecord>().CountAsync(r => r.TenantId == Tenant));
    }

    // ── test doubles for the race shapes ────────────────────────────────────────────────────────────

    /// <summary>Delegates to the real store but lands a competitor's genesis BETWEEN the writer's
    /// read (which saw absence) and its append — the exact two-writer race window.</summary>
    private sealed class RacingStore : IHomeEpochStore
    {
        private readonly IHomeEpochStore _inner;
        private readonly Func<Task> _preStage;

        public RacingStore(IHomeEpochStore inner, Func<Task> preStage)
        {
            _inner = inner;
            _preStage = preStage;
        }

        public Task<HomeEpochRecord?> GetCurrentEpochAsync(string tenantId, CancellationToken ct = default) =>
            _inner.GetCurrentEpochAsync(tenantId, ct);

        public async Task AdvanceAsync(HomeEpochRecord proposed, CancellationToken ct = default)
        {
            await _preStage(); // the competitor lands first
            await _inner.AdvanceAsync(proposed, ct); // now the strictly-monotonic reject fires
        }
    }

    /// <summary>Simulates the composite-PK race loser: the FIRST read reports absence (the raced
    /// pre-append snapshot), the append throws the caller-chosen exception (a DbUpdateException for
    /// the PK shape), and every LATER read delegates to the real store — so the writer's post-throw
    /// re-read sees whatever actually landed.</summary>
    private sealed class PkRaceLoserStore : IHomeEpochStore
    {
        private readonly IHomeEpochStore _inner;
        private readonly Func<Exception> _exception;
        private bool _firstReadDone;

        public PkRaceLoserStore(IHomeEpochStore inner, Func<Exception> exception)
        {
            _inner = inner;
            _exception = exception;
        }

        public Task<HomeEpochRecord?> GetCurrentEpochAsync(string tenantId, CancellationToken ct = default)
        {
            if (!_firstReadDone)
            {
                _firstReadDone = true; // the raced snapshot: absence
                return Task.FromResult<HomeEpochRecord?>(null);
            }
            return _inner.GetCurrentEpochAsync(tenantId, ct);
        }

        public Task AdvanceAsync(HomeEpochRecord proposed, CancellationToken ct = default) =>
            throw _exception();
    }
}
