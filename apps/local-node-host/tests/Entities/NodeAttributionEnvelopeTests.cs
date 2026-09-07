using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MTW-2 2612-C — the node-signed ATTRIBUTION ENVELOPE at the durable-mutation seam (per the 2612-B
/// key-model ruling, Admiral 2026-07-18). Proves the JE-posted audit row's signed payload now carries
/// the acting member's attribution (Party + membership + session correlation + pinned authority
/// versions) plus the SIGNED party→node-key binding, that the envelope's <b>attestation integrity</b>
/// verifies under the node key, and that any tamper of an attribution field is caught — <b>tamper
/// evidence</b>. These tests deliberately assert ATTESTATION INTEGRITY + TAMPER-EVIDENCE and NEVER
/// "impersonation-resistance": the node key attests, and a node can assert any member — true per-member
/// non-repudiation is the deferred passkey/roster-key future (board finding F9).
/// </summary>
[Trait("PlanCard", "MTW-2-2840")]
public sealed class NodeAttributionEnvelopeTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodePrincipalSigner _signer = null!;
    private IOperationVerifier _verifier = null!;
    private NodeAuditSignatureVerificationContext _verification = null!;

    private static readonly TenantId LocalTenantId = new("local");

    private static byte[] FixedSeed()
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(0xB0 + i);
        }
        return seed;
    }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-attribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "attribution-test.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _signer = new NodePrincipalSigner(FixedSeed());
        _verifier = new Ed25519Verifier();
        _verification = new NodeAuditSignatureVerificationContext(_signer.Signer.IssuerId, _verifier);
    }

    public async Task DisposeAsync()
    {
        _signer.Dispose();
        await Task.CompletedTask;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private NodeAuditWriteEnlister SigningEnlister() =>
        new(_signer.Signer);

    private static AuthorizationWriteContext Authority(string party) =>
        new(new ActorId(party), LocalTenantId, DateTimeOffset.UtcNow);

    [Fact(DisplayName = "Attestation integrity: a bound member's JE-posted audit row carries the node-signed attribution envelope (member Party + membership + session + authority + signed key-binding) and the signature VERIFIES")]
    public async Task MemberSession_AttributionEnvelope_IsSigned_AndVerifies()
    {
        var store = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPosted("JE-ATTR-1", 100m),
            Authority("party:alice"));

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value).ToListAsync());

        // The Actor is the resolved member Party — NOT the static operator constant.
        Assert.Equal("party:alice", row.Actor);

        // The signed payload carries the exact authorization decision used by the write.
        var attribution = ParseAttribution(row.Payload);
        Assert.Equal(NodeAuditWriteEnlister.CarriedDecisionAttributionSchema,
            attribution.GetProperty("schema").GetString());
        Assert.Equal("party:alice", attribution.GetProperty("member_party_id").GetString());
        Assert.Equal(_signer.Signer.IssuerId.ToBase64Url(),
            attribution.GetProperty("attesting_public_key").GetString());
        var authority = ParseAuthority(row.Payload);
        Assert.Equal("party:alice", authority.GetProperty("principal").GetString());
        Assert.Equal(LocalTenantId.Value, authority.GetProperty("tenant").GetString());
        Assert.StartsWith(TeamRolePermissions.LedgerPost,
            authority.GetProperty("act").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("journal-entry", authority.GetProperty("target").GetProperty("record_kind").GetString());
        Assert.Equal("JE-ATTR-1", authority.GetProperty("target").GetProperty("record_id").GetString());
        Assert.NotEmpty(authority.GetProperty("grants").EnumerateArray());
        Assert.NotEmpty(authority.GetProperty("derivation_ids").EnumerateArray());

        // Attestation integrity — the whole payload (attribution included) verifies under the node key.
        var op = NodeAuditSignaturePayload.TryReconstruct(row, _signer.Signer.IssuerId, row.Signature!);
        Assert.NotNull(op);
        Assert.True(_verifier.Verify(op!), "the node-signed attribution envelope must verify (attestation integrity)");

        // The reader surfaces the row as Verified.
        var reader = new NodeAuditEventReader(_factory, _verification);
        var view = Assert.Single((await reader.ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery())).Events);
        Assert.Equal(NodeAuditSignatureClassifier.Verified, view.SignatureState);
    }

    [Fact(DisplayName = "Two members: two distinct bound principals stamp two distinct attribution envelopes — neither collapses to the operator")]
    public async Task TwoMembers_StampTwoDistinctAttributions()
    {
        var storeA = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        var storeB = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await storeA.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPosted("JE-ATTR-A", 10m),
            Authority("party:alice"));
        await storeB.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPosted("JE-ATTR-B", 20m),
            Authority("party:bob"));

        await using var ctx = await _factory.CreateDbContextAsync();
        var rows = await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value)
            .OrderBy(r => r.OccurredAt).ThenBy(r => r.AuditId).ToListAsync();
        Assert.Equal(2, rows.Count);

        var members = rows.Select(r => ParseAttribution(r.Payload).GetProperty("member_party_id").GetString()).ToHashSet();
        Assert.Contains("party:alice", members);
        Assert.Contains("party:bob", members);
        Assert.DoesNotContain(ActiveTeamAuthorizationContext.LocalUserId, members);
    }

    [Fact(DisplayName = "Tamper-evidence: altering ANY attribution field in the signed payload (member_party_id) makes the node signature FAIL to verify — the attestation binds the attribution content")]
    public async Task TamperedAttribution_FailsVerification()
    {
        var store = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPosted("JE-ATTR-TAMPER", 100m),
            Authority("party:alice"));

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = await ctx.Set<NodeAuditEventRow>().FirstAsync(r => r.TenantId == LocalTenantId.Value);

        // Sanity: the untampered envelope verifies.
        var honest = NodeAuditSignaturePayload.TryReconstruct(row, _signer.Signer.IssuerId, row.Signature!);
        Assert.True(_verifier.Verify(honest!));

        // Forge the attribution: swap the attested member Party in the payload, keeping the SAME node
        // signature/issuer/nonce/timestamp. Because the signature covers the payload bytes (attribution
        // included), re-verification of the forged payload FAILS — a node cannot have its attestation of
        // Party alice re-read as an attestation of Party mallory without re-signing under the node key.
        var forgedPayload = row.Payload.Replace("party:alice", "party:mallory");
        Assert.NotEqual(row.Payload, forgedPayload);
        var forged = new SignedOperation<string>(
            Payload: forgedPayload,
            IssuerId: _signer.Signer.IssuerId,
            IssuedAt: row.OccurredAt,
            Nonce: NodeAuditSignaturePayload.NonceFor(row.AuditId),
            Signature: Signature.FromBytes(row.Signature!));
        Assert.False(_verifier.Verify(forged), "a tampered attribution must break attestation integrity");
    }

    [Fact(DisplayName = "Operator fallback: with no bound principal (bootstrap/desktop/detached-workflow), the envelope stamps the operator-fallback attribution and still verifies (ruled option (a))")]
    public async Task NoBoundPrincipal_StampsOperatorFallback_AndVerifies()
    {
        // The request boundary resolves an unbound desktop/bootstrap act to the ruled operator Party
        // before asking the gate for the decision carried into the write.
        var store = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(audit: SigningEnlister()));
        await store.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPosted("JE-ATTR-OP", 42m),
            Authority(ActiveTeamAuthorizationContext.LocalUserId));

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value).ToListAsync());

        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, row.Actor);
        var attribution = ParseAttribution(row.Payload);
        Assert.Equal(NodeAuditWriteEnlister.CarriedDecisionAttributionSchema,
            attribution.GetProperty("schema").GetString());
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, attribution.GetProperty("member_party_id").GetString());
        Assert.Equal(_signer.Signer.IssuerId.ToBase64Url(),
            attribution.GetProperty("attesting_public_key").GetString());
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId,
            ParseAuthority(row.Payload).GetProperty("principal").GetString());

        var op = NodeAuditSignaturePayload.TryReconstruct(row, _signer.Signer.IssuerId, row.Signature!);
        Assert.True(_verifier.Verify(op!), "operator-fallback attestation must verify too");
    }

    [Fact(DisplayName = "Ambient source: projects the listener-published principal into an attribution; no open scope resolves null (operator fallback), and the scope does not outlive its using")]
    public void AmbientSource_ResolvesPublishedPrincipal_AndNullWhenNoScopeIsOpen()
    {
        // No scope open → null (the detached/bootstrap path).
        Assert.Null(NodeCallerAttributionScope.Current);

        var principal = new SelectedSessionRequestPrincipal(
            accountId: "account-alice",
            tenantId: new TenantId("tenant-a"),
            principalUserId: new PrincipalUserId("principal-alice"),
            canonicalParty: new CanonicalPartyReference("party:alice"),
            membershipId: "membership:party:alice",
            membershipOwnerVersion: 7,
            pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant:party:alice", 5)],
            authorizationEpoch: 3,
            sessionCorrelationId: "session:party:alice",
            coordinationCorrelationId: "coord:party:alice");

        using (NodeCallerAttributionScope.Enter(NodeCallerAttribution.From(principal)))
        {
            var resolved = NodeCallerAttributionScope.Current;
            Assert.NotNull(resolved);
            Assert.Equal("party:alice", resolved!.MemberPartyId);
            Assert.Equal("membership:party:alice", resolved.MembershipId);
            Assert.Equal(7, resolved.MembershipOwnerVersion);
            Assert.Equal(3, resolved.AuthorizationEpoch);
            Assert.Equal("session:party:alice", resolved.SessionCorrelationId);
            Assert.Equal("coord:party:alice", resolved.CoordinationCorrelationId);
        }

        // Closing the scope restores the unbound path — a member never leaks into the next write.
        Assert.Null(NodeCallerAttributionScope.Current);
    }

    [Fact(DisplayName = "Ambient source: the attribution does not leak into a task that captured the execution context inside the scope (holder is cleared on dispose)")]
    public async Task AmbientScope_DoesNotLeakIntoACapturedFlow()
    {
        var released = new TaskCompletionSource();
        var observedAfterDispose = new TaskCompletionSource<NodeCallerAttribution?>();

        Task captured;
        using (NodeCallerAttributionScope.Enter(Attribution("party:alice")))
        {
            Assert.NotNull(NodeCallerAttributionScope.Current);
            // Capture the CURRENT execution context (the scope is open) and read it back only after the
            // scope has been disposed on the originating flow.
            captured = Task.Run(async () =>
            {
                await released.Task;
                observedAfterDispose.SetResult(NodeCallerAttributionScope.Current);
            });
        }

        released.SetResult();
        await captured;
        Assert.Null(await observedAfterDispose.Task);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static NodeCallerAttribution Attribution(string party) => new(
        MemberPartyId: party,
        MembershipId: "membership:" + party,
        MembershipOwnerVersion: 7,
        SessionCorrelationId: "session:" + party,
        CoordinationCorrelationId: "coord:" + party,
        AuthorizationEpoch: 3);

    private static JsonElement ParseAttribution(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        // Clone so the element survives the using-scope dispose.
        return doc.RootElement.GetProperty("attribution").Clone();
    }

    private static JsonElement ParseAuthority(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("authority").Clone();
    }

    private static JournalEntry BalancedPosted(string id, decimal amount) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "attribution envelope test",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow))
        {
            Status = JournalEntryStatus.Posted,
        };
}
