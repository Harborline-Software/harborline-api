using Harborline.Api.Blocks.AccessGrant;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Admission;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #3167 — hard-gate coverage for <see cref="PairingTokenGatedAdmitter"/> (ruling 1150Z R1.3/R4-a + the
/// PR-3166 verdict findings folded onto the card). Exercised against the DURABLE token + binding stores (the
/// production wiring), not the in-memory doubles. Gates proven here:
/// <list type="bullet">
///   <item>R4-a — proof-of-possession is verified BEFORE redeem; a PoP failure (incl. a substituted X-Wing key)
///     refuses with the token UNBURNED.</item>
///   <item>R3 / F2-a — the X-Wing key is a HARD, tested precondition: absent or wrong-length refuses (unburned),
///     so the wiring cannot silently omit it.</item>
///   <item>R1.3 — the DISTINCT admitter signer is UNREACHABLE without a successful redemption (a spy signer never
///     signs on any refusal path).</item>
///   <item>R6/C — a durable-store Redeem throw AND a CryptographicException are CONTAINED to an opaque refusal
///     (never an escaping throw / 500).</item>
///   <item>F3 — an induced Admit-guard refusal (already-member) leaves the DURABLE token REDEEMABLE (not burned).</item>
///   <item>Happy path — a valid pairing enrollment admits, binds the token id + mint-session evidence + X-Wing into
///     the signed admission, and returns the A->B bootstrap.</item>
/// </list>
/// </summary>
public sealed class PairingTokenGatedAdmitterTests : IAsyncLifetime
{
    private const string TenantId = "7e57aaaa-0000-0000-0000-000000000001";
    private const string PrincipalId = "principal-1";
    private const string JoinerPartyId = "party-1";
    private const string GrantId = "grant-1";
    private const string FounderPartyId = "founder";
    private static readonly Guid Team = Guid.Parse("7e57bbbb-0000-0000-0000-000000000002");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    private static readonly Harborline.Api.Kernel.Security.Crypto.Ed25519Signer TransportSigner = new();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(TransportSigner);

    private readonly System.Collections.Generic.List<string> _dirs = new();
    private readonly System.Collections.Generic.List<IAsyncDisposable> _async = new();
    private readonly System.Collections.Generic.List<ServiceProvider> _providers = new();
    private readonly System.Collections.Generic.List<SearchTestStore> _searchStores = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var a in _async) await a.DisposeAsync();
        foreach (var s in _searchStores) await s.DisposeAsync();
        foreach (var p in _providers) await p.DisposeAsync();
        foreach (var d in _dirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    // ── R4-a: PoP verified BEFORE redeem; a substituted X-Wing key fails PoP and the token stays unburned. ──

    [Fact(DisplayName = "pairing: a PoP failure (X-Wing substituted after signing) refuses BEFORE redeem — token unburned (R4-a)")]
    public async Task PoP_Failure_Refuses_Before_Redeem_Token_Unburned()
    {
        var h = await BuildAsync();
        var (tokenId, valid) = h.MintBindAndSign();

        // Substitute a DIFFERENT (well-formed) X-Wing key AFTER signing — the joiner's principal signature no longer
        // covers the transcript (R4-a binds the X-Wing bytes), so VerifyRequest fails.
        var tampered = valid with { JoiningXWingPublicKey = XWingKeyB64(99) };
        var outcome = await h.Admitter.AdmitAsync(tampered, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.False(h.Roster.Current.Contains(JoinerPartyId)); // nothing signed.

        // The token was NOT burned (PoP ran before redeem) — the ORIGINAL valid request now admits.
        var ok = await h.Admitter.AdmitAsync(valid, CancellationToken.None);
        Assert.True(ok.Accepted);
        Assert.True(h.Roster.Current.Contains(JoinerPartyId));
    }

    // ── R3 / F2-a: the X-Wing key is a HARD precondition — absent or wrong-length refuses (unburned). ──

    [Fact(DisplayName = "pairing: an ABSENT X-Wing key refuses (mandatory), token unburned (R3/F2-a)")]
    public async Task Absent_XWing_Refuses_Token_Unburned()
    {
        var h = await BuildAsync();
        // A validly-SIGNED request with NO X-Wing key (empty is a valid signed binding — VerifyRequest passes).
        var (tokenId, req) = h.MintBindAndSign(xwing: string.Empty);
        var outcome = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.False(h.Roster.Current.Contains(JoinerPartyId));
        Assert.False(await h.TokenIsBurnedAsync(tokenId)); // unburned — refused before redeem.
    }

    [Fact(DisplayName = "pairing: a WRONG-LENGTH X-Wing key refuses (not 1216 bytes), token unburned (R3)")]
    public async Task Malformed_XWing_Refuses_Token_Unburned()
    {
        var h = await BuildAsync();
        // A valid 32-byte (DM-shaped) base64url in the X-Wing slot — signed, so VerifyRequest passes, but it is not
        // the 1216-byte X-Wing length, so the gated admitter refuses.
        var (tokenId, req) = h.MintBindAndSign(xwing: DmKeyB64(5));
        var outcome = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.False(await h.TokenIsBurnedAsync(tokenId));
    }

    // ── R1.3: the DISTINCT signer is UNREACHABLE without a successful redemption. ──

    [Fact(DisplayName = "pairing: the admitter signer NEVER signs without a successful redeem (R1.3 structural)")]
    public async Task Signer_Unreachable_Without_A_Successful_Redemption()
    {
        // The genesis roster is signed by founderInner; the gated admitter's signer is a SPY that WRAPS founderInner
        // (so its IssuerId matches the founder roster binding — the unreached Admit key-check would pass), yet the
        // genesis signing does NOT touch the spy. So spy.SignCount starts at 0 and only a real redeem+sign moves it.
        var founderInner = NewIdentity(FounderPartyId).Signer;
        var spy = new CountingSigner(founderInner);
        var h = await BuildAsync(pairingSigner: spy, founderSigner: founderInner);

        // (a) A PoP-VALID request whose token was never MINTED/BOUND → the bridge refuses at binding lookup, well
        //     before AdmitOverInvite → the spy never signs.
        var unbound = h.SignForUnboundToken("never-bound-token");
        var r1 = await h.Admitter.AdmitAsync(unbound, CancellationToken.None);
        Assert.False(r1.Accepted);
        Assert.Equal(0, spy.SignCount);

        // (b) A PoP-VALID, BOUND request whose token is already REDEEMED (redeem fails) → AdmitOverInvite rejects
        //     before signing → the spy never signs.
        var (tokenId, req) = h.MintBindAndSign();
        Assert.True(h.TokenStore.Redeem(tokenId, Now).Accepted); // burn it out-of-band.
        var r2 = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.False(r2.Accepted);
        Assert.Equal(0, spy.SignCount);

        // (c) A VALID, BOUND, unredeemed request → NOW the signer signs exactly once (the redemption receipt path).
        //     The party is still party-1 (unadmitted — part (b)'s token was burned out-of-band before its admit).
        var (t2, req2) = h.MintBindAndSign(partySeed: 2);
        var r3 = await h.Admitter.AdmitAsync(req2, CancellationToken.None);
        Assert.True(r3.Accepted);
        Assert.Equal(1, spy.SignCount);
    }

    // ── R6/C: exception containment — a durable Redeem throw / CryptographicException → opaque refusal, no throw. ──

    [Fact(DisplayName = "pairing: a durable token-store Redeem THROW is contained to an opaque refusal (R6/C — never a 500)")]
    public async Task Durable_Redeem_Throw_Is_Contained()
    {
        var h = await BuildAsync(tokenStore: new ThrowingTokenStore());
        var (tokenId, req) = h.MintBindAndSign();
        // Must NOT throw — the gated admitter catches the durable fault and returns a fail-closed refusal.
        var outcome = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.False(h.Roster.Current.Contains(JoinerPartyId));
    }

    [Fact(DisplayName = "pairing: a CryptographicException during signing is contained to an opaque refusal (R6/C)")]
    public async Task Signing_CryptographicException_Is_Contained()
    {
        var founder = NewIdentity(FounderPartyId);
        // A signer whose key MATCHES the founder roster binding (so Admit's key-check passes and signing is reached)
        // but whose SignAsync throws CryptographicException.
        var throwing = new CryptoThrowingSigner(founder.Signer.IssuerId);
        var h = await BuildAsync(pairingSigner: throwing, founderSigner: founder.Signer);
        var (tokenId, req) = h.MintBindAndSign();
        var outcome = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.False(h.Roster.Current.Contains(JoinerPartyId));
    }

    // ── F3: an induced Admit-guard refusal (already-member) leaves the DURABLE token REDEEMABLE. ──

    [Fact(DisplayName = "pairing: an already-member pre-check refusal leaves the durable token REDEEMABLE (F3 — not burned)")]
    public async Task Already_Member_PreCheck_Leaves_Durable_Token_Redeemable()
    {
        var h = await BuildAsync();

        // Admit party-1 the first time (token T1).
        var (t1, first) = h.MintBindAndSign();
        Assert.True((await h.Admitter.AdmitAsync(first, CancellationToken.None)).Accepted);
        Assert.True(h.Roster.Current.Contains(JoinerPartyId));

        // Mint a SECOND durable token T2 bound to the SAME (now-member) party, and present it. The bridge's F3
        // pre-check (roster.Contains) refuses BEFORE AdmitOverInvite — so T2 is NOT redeemed.
        var (t2, second) = h.MintBindAndSign();
        var refused = await h.Admitter.AdmitAsync(second, CancellationToken.None);
        Assert.False(refused.Accepted);

        // THE PROOF: T2's durable token row is still UNBURNED (Redeemed == false) — no griefing on the member's own
        // pairing token via a can-never-succeed request.
        Assert.False(await h.TokenIsBurnedAsync(t2));
    }

    // ── Happy path: a valid pairing enrollment admits + binds token id / mint-session evidence / X-Wing. ──

    [Fact(DisplayName = "pairing: a valid enrollment admits, binds provenance + X-Wing into the signed admission, returns bootstrap")]
    public async Task Valid_Enrollment_Admits_And_Binds_Provenance()
    {
        var h = await BuildAsync();
        var (tokenId, req) = h.MintBindAndSign();

        var outcome = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.True(outcome.Accepted);
        Assert.NotNull(outcome.Response);
        // The A->B bootstrap carries the joiner's admission + the admitter's transport key (so B trusts A).
        Assert.Contains(outcome.Response!.Admissions, a => a.PartyId == JoinerPartyId);

        // The signed admission is genesis-rooted + binds the token id + mint-session evidence (R1.2) + the X-Wing key.
        var newRoster = h.Roster.Current;
        Assert.True(newRoster.Contains(JoinerPartyId));
        Assert.True(newRoster.ValidatesToGenesis(Verifier));
        Assert.NotNull(newRoster.XWingPublicKeyOf(JoinerPartyId));

        // The durable token is now consumed (a replay refuses).
        Assert.True(await h.TokenIsBurnedAsync(tokenId));
    }

    // ── F1: the token-id binding is DECOUPLED from the (soft) session-evidence field. ──

    [Fact(DisplayName = "pairing: the token id is bound into the signed admission even when session evidence is EMPTY (verdict F1 decoupled)")]
    public async Task Token_Id_Bound_Unconditionally_Even_With_Empty_Session_Evidence()
    {
        var h = await BuildAsync();
        // Bind the pairing token with EMPTY session evidence (the pathological case F1 hardens against). The
        // structural "admitted by token T" provenance must still bind — it must NOT ride on the soft audit field.
        var (tokenId, req) = h.MintBindAndSign(sessionEvidence: string.Empty);

        var outcome = await h.Admitter.AdmitAsync(req, CancellationToken.None);
        Assert.True(outcome.Accepted);

        var admission = h.Roster.Current.EnumerateAdmissions()
            .First(a => a.PartyId == JoinerPartyId).Admission;
        // Token id bound UNCONDITIONALLY on the pairing path; the (empty) session-evidence field is independent.
        Assert.Equal(tokenId, admission.AdmittedViaTokenId);
        Assert.Equal(string.Empty, admission.MintingSessionEvidence);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required PairingTokenGatedAdmitter Admitter { get; init; }
        public required NodeTeamRoster Roster { get; init; }
        public required AdmissionCoordinator Coordinator { get; init; }
        public required IAdmissionTokenStore TokenStore { get; init; }
        public required IWebPairingInviteBindingStore Bindings { get; init; }
        public required TeamTrustAnchor Anchor { get; init; }
        public required IDbContextFactory<NodeLocalAdmissionDbContext> AdmissionFactory { get; init; }

        // Mint a durable pairing token bound to (party), and build a signed enrollment request presenting it.
        public (string TokenId, EnrollmentRequest Request) MintBindAndSign(
            string xwing = "__default__", byte partySeed = 1, string joinerParty = JoinerPartyId,
            string sessionEvidence = "sess-corr")
        {
            var joiner = NewIdentity(joinerParty);
            var token = Coordinator.CreateInvite(Anchor);
            Bindings.Bind(new WebPairingInviteBinding(
                token.TokenId, Membership(), joinerParty, token.Anchor, sessionEvidence));
            var xwingKey = xwing == "__default__" ? XWingKeyB64(partySeed) : xwing;
            var req = WireEnrollment.BuildRequest(
                token.TokenId, joinerParty, FreshTransportKey(), joiner.Signer, Now, Guid.NewGuid(),
                joiningDmPublicKey: FreshKey(partySeed), joiningXWingPublicKey: xwingKey);
            return (token.TokenId, req);
        }

        // A valid, well-signed request presenting a token id that has NO pairing binding (for the unreachable test).
        public EnrollmentRequest SignForUnboundToken(string tokenId)
        {
            var joiner = NewIdentity(JoinerPartyId);
            return WireEnrollment.BuildRequest(
                tokenId, JoinerPartyId, FreshTransportKey(), joiner.Signer, Now, Guid.NewGuid(),
                joiningDmPublicKey: FreshKey(1), joiningXWingPublicKey: XWingKeyB64(1));
        }

        public async Task<bool> TokenIsBurnedAsync(string tokenId)
        {
            await using var ctx = await AdmissionFactory.CreateDbContextAsync();
            var row = await ctx.AdmissionTokens.FindAsync(tokenId);
            return row is not null && row.Redeemed;
        }
    }

    private async Task<Harness> BuildAsync(
        IOperationSigner? pairingSigner = null,
        IOperationSigner? founderSigner = null,
        IAdmissionTokenStore? tokenStore = null)
    {
        // Grant store (SearchTestStore) with ONE live grant matching the membership pins.
        var search = await SearchTestStore.CreateAsync();
        _searchStores.Add(search);
        await using (var ctx = search.CreateContext())
        {
            ctx.Grants.Add(new GrantRow
            {
                GrantId = GrantId,
                TenantId = TenantId,
                SubjectId = PrincipalId,
                RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
                RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
                ScopeValue = "/",
                Residency = 0,
                ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                GrantedBy = "issuer-1",
                GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                GranterKind = (int)GranterKind.Person,
                Source = (int)GrantSourceKind.Manual,
                Approver = "issuer-1",
                LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                RevokedAtUnixMs = null,
                OwnerVersion = 4,
            });
            ctx.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
            {
                TenantId = TenantId, PrincipalId = PrincipalId, AuthorizationEpoch = 7,
            });
            await ctx.SaveChangesAsync();
        }

        // Durable admission db (token store + pairing-binding store).
        var admissionFactory = NewAdmissionFactory();
        await using (var ctx = await admissionFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        var durableTokenStore = tokenStore ?? new DurableAdmissionTokenStore(admissionFactory);
        var bindings = new DurableWebPairingInviteBindingStore(admissionFactory);

        // Roster db + CRDT projection.
        var rosterFactory = NewRosterFactory(out var crdtSp);
        _providers.Add(crdtSp);
        await using (var ctx = await rosterFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var founder = new IdentityFor(founderSigner ?? NewIdentity(FounderPartyId).Signer);
        var genesis = MemberRoster.Genesis(Team, FounderPartyId, founder.Signer, Verifier, Now, Guid.NewGuid());
        var roster = new NodeTeamRoster(genesis);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            crdtSp.GetRequiredService<ICrdtEngine>(), rosterFactory, Verifier,
            NullLogger<RosterCrdtProjection>.Instance, roster);
        _async.Add(projection);

        var coordinator = new AdmissionCoordinator(Verifier, durableTokenStore, new FixedTimeProvider(Now));
        var bridge = new WebAdmittedMemberAtlasBridge(
            new FixedPartyReader(JoinerPartyId), search.Factory, coordinator, bindings, new FixedTimeProvider(Now),
            new FixedAuthorizationClosure());

        var teamContexts = BuildTeamContexts();
        var gated = new PairingTokenGatedAdmitter(
            bridge, roster, pairingSigner ?? founder.Signer, FounderPartyId, Verifier, projection,
            NullEnrollmentCompensatingControlRecorder.Instance, bindings, teamContexts, diag: null);

        return new Harness
        {
            Admitter = gated,
            Roster = roster,
            Coordinator = coordinator,
            TokenStore = durableTokenStore,
            Bindings = bindings,
            Anchor = TeamTrustAnchor.FromRoster(genesis),
            AdmissionFactory = admissionFactory,
        };
    }

    private ITeamContextFactory BuildTeamContexts()
    {
        var teamCollection = new ServiceCollection();
        teamCollection.AddSingleton<INodeIdentityProvider>(new InMemoryNodeIdentityProvider(DeriveTeamScopedIdentity(Team)));
        var teamServices = teamCollection.BuildServiceProvider();
        _providers.Add(teamServices);
        var teamContext = new TeamContext(new TeamId(Team), "Test Team", teamServices, TimeProvider.System);
        return new FixedTeamContextFactory(teamContext);
    }

    private IDbContextFactory<NodeLocalAdmissionDbContext> NewAdmissionFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-pairing-gated-adm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var services = new ServiceCollection();
        services.AddDbContextFactory<NodeLocalAdmissionDbContext>(
            opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "admission.db")};Pooling=False"));
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        return sp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
    }

    private IDbContextFactory<NodeLocalRosterDbContext> NewRosterFactory(out ServiceProvider sp)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-pairing-gated-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(
            opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "roster.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
    }

    private static NodeIdentity DeriveTeamScopedIdentity(Guid teamId)
    {
        var rootSeed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
        var (rootPub, rootPriv) = TransportSigner.GenerateFromSeed(rootSeed);
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var root = new NodeIdentity(nodeId, rootPub, rootPriv);
        return TeamScopedNodeIdentity.Derive(root, teamId.ToString("D"), SubkeyDerivation);
    }

    private static TenantMembershipSnapshot Membership() => new(
        MembershipId: "membership-1", AccountId: "account-1", TenantId: TenantId,
        CanonicalPrincipalId: PrincipalId, GrantId: GrantId, GrantOwnerVersion: 4, AuthorizationEpoch: 7,
        Status: TenantMembershipStatus.Active, OwnerVersion: 3);

    private sealed record IdentityWithKey(string PartyId, KeyPair Key, IOperationSigner Signer);

    private static IdentityWithKey NewIdentity(string partyId)
    {
        var kp = KeyPair.Generate();
        return new IdentityWithKey(partyId, kp, new Ed25519Signer(kp));
    }

    // A thin holder so a caller-supplied founder signer (spy/throwing) drives the genesis roster too.
    private sealed class IdentityFor
    {
        public IdentityFor(IOperationSigner signer) => Signer = signer;
        public IOperationSigner Signer { get; }
    }

    private static byte[] FreshTransportKey()
    {
        var k = new byte[Harborline.Api.Foundation.Crypto.PrincipalId.LengthInBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(k);
        return k;
    }

    private static byte[] FreshKey(byte seed)
    {
        var bytes = new byte[Harborline.Api.Foundation.Crypto.PrincipalId.LengthInBytes];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    private static string DmKeyB64(byte seed) =>
        Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(FreshKey(seed)).ToBase64Url();

    private static string XWingKeyB64(byte seed)
    {
        var bytes = new byte[1216];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class FixedPartyReader(string partyId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(
                new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(partyId)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedTeamContextFactory(TeamContext context) : ITeamContextFactory
    {
        public IReadOnlyCollection<TeamContext> Active { get; } = [context];
        public Task<TeamContext> GetOrCreateAsync(TeamId teamId, string displayName, CancellationToken ct) =>
            Task.FromResult(context);
        public Task RemoveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingSigner(IOperationSigner inner) : IOperationSigner
    {
        public int SignCount;
        public Harborline.Api.Foundation.Crypto.PrincipalId IssuerId => inner.IssuerId;
        public async ValueTask<SignedOperation<T>> SignAsync<T>(
            T payload, DateTimeOffset issuedAt, Guid nonce, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SignCount);
            return await inner.SignAsync(payload, issuedAt, nonce, ct).ConfigureAwait(false);
        }
    }

    private sealed class CryptoThrowingSigner(Harborline.Api.Foundation.Crypto.PrincipalId issuer) : IOperationSigner
    {
        public Harborline.Api.Foundation.Crypto.PrincipalId IssuerId => issuer;
        public ValueTask<SignedOperation<T>> SignAsync<T>(
            T payload, DateTimeOffset issuedAt, Guid nonce, CancellationToken ct = default) =>
            throw new System.Security.Cryptography.CryptographicException("induced signing failure");
    }

    private sealed class ThrowingTokenStore : IAdmissionTokenStore
    {
        public void Issue(AdmissionToken token) { /* mint succeeds; the redeem below throws */ }
        public RedeemResult Redeem(string tokenId, DateTimeOffset now) =>
            throw new InvalidOperationException("induced durable redeem failure");
    }
}
