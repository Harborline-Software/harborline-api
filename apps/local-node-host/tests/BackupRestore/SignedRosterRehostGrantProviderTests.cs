using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.BackupRestore;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.BackupRestore;

public sealed class SignedRosterRehostGrantProviderTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"rehost-292-{Guid.NewGuid():N}.db");
    private static readonly Guid Team = Guid.Parse("29200000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
    private readonly Ed25519Signer founder = new(KeyPair.Generate());
    private readonly Ed25519Signer member = new(KeyPair.Generate());
    private readonly IOperationVerifier verifier = new Ed25519Verifier();
    private readonly InMemoryAuditTrail trail = new();
    private readonly Clock clock = new();
    private readonly NodeIdentity replacement = new("replacement", KeyPair.Generate().PrincipalId.AsSpan().ToArray(), []);
    private Factory factory = null!;
    private MemberRoster roster = null!;
    private int gateCalls;
    private AuthorizationGateRequest? observed;
    private ActorId Caller => new(founder.IssuerId.ToBase64Url());
    private static string Tenant => Team.ToString("D");

    public async Task InitializeAsync()
    {
        factory = new Factory(path);
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        roster = MemberRoster.Genesis(Team, "founder", founder, verifier, At.AddHours(-2), Guid.NewGuid())
            .Admit("founder", founder, "member", member.IssuerId, PermissionCompositions.Member,
                verifier, At.AddHours(-1), Guid.NewGuid());
        db.RosterRecords.AddRange(roster.EnumerateAdmissions().Select(a =>
            NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(a)
                .AttestReceipt(founder, "founder", a.Admission.IssuedAt))));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() { File.Delete(path); return Task.CompletedTask; }

    private SignedRosterRehostGrantProvider Provider(bool allow = true) => new(factory, member, verifier,
        TestAuthorization.Gate(allow, r => { gateCalls++; observed = r; }),
        new AuthorizationRefusalAudit(trail, founder, NullLogger<AuthorizationRefusalAudit>.Instance), clock);

    private async Task<RosterSignedRehostGrant> Grant(string change = "")
    {
        var payload = new RehostGrantPayload(Tenant, "old", replacement.NodeId,
            Convert.ToBase64String(replacement.PublicKey), [SignedRosterRehostGrantProvider.ReadCanonical], At.AddMinutes(5));
        payload = change switch
        {
            "wrong_tenant" => payload with { TenantId = Guid.NewGuid().ToString("D") },
            "wrong_replaced_node" => payload with { ReplacedNodeId = "other" },
            "wrong_replacement" => payload with { ReplacementNodeId = "other" },
            "wrong_key" => payload with { ReplacementPublicKey = "other" },
            "out_of_scope" => payload with { Acts = [SignedRosterRehostGrantProvider.PromoteHome] },
            "unknown_act" => payload with { Acts = [SignedRosterRehostGrantProvider.ReadCanonical, "rehost:erase"] },
            "expired" => payload with { ExpiresAt = At },
            _ => payload
        };
        var issuer = change == "issuer_unadmitted" ? new Ed25519Signer(KeyPair.Generate()) : member;
        var signed = await issuer.SignAsync(payload, change == "not_yet_valid" ? At.AddMinutes(1) : At, Guid.NewGuid());
        if (change == "invalid_signature") signed = signed with { Payload = payload with { ReplacedNodeId = "tampered" } };
        return new RosterSignedRehostGrant(change == "malformed" ? "arbitrary-nonblank" : JsonSerializer.Serialize(signed));
    }

    private Task<AuthorizationDecision> Redeem(RosterSignedRehostGrant grant, bool allow = true) =>
        Provider(allow).RedeemAsync(grant, Tenant, "old", replacement,
            [SignedRosterRehostGrantProvider.ReadCanonical], Caller).AsTask();

    [Fact]
    public async Task LiveMemberVerifiesAndProductionObtainUsesSignedEnvelope()
    {
        var decision = await Redeem(await Grant());
        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Equal(1, gateCalls);
        var obtained = await Provider().ObtainAsync(Tenant, "old", replacement, ["trustee"]);
        var op = JsonSerializer.Deserialize<SignedOperation<RehostGrantPayload>>(obtained.SerializedGrant)!;
        Assert.True(verifier.Verify(op));
        Assert.Equal(new[] { SignedRosterRehostGrantProvider.ReadCanonical, SignedRosterRehostGrantProvider.PromoteHome }, op.Payload.Acts);
        Assert.Equal(1, gateCalls); // Issuing must neither decide nor burn.
        Assert.Equal(AuthorizationVerdict.Allowed, (await Redeem(obtained)).Verdict);
        await Refuses(obtained, "already_redeemed");
    }

    [Theory]
    [InlineData("malformed", "malformed")]
    [InlineData("invalid_signature", "invalid_signature")]
    [InlineData("wrong_tenant", "wrong_tenant")]
    [InlineData("wrong_replaced_node", "wrong_replaced_node")]
    [InlineData("wrong_replacement", "wrong_replacement")]
    [InlineData("wrong_key", "wrong_replacement")]
    [InlineData("out_of_scope", "out_of_scope")]
    [InlineData("unknown_act", "out_of_scope")]
    [InlineData("expired", "expired")]
    [InlineData("not_yet_valid", "not_yet_valid")]
    [InlineData("issuer_unadmitted", "issuer_unadmitted")]
    public async Task EachInvalidGrantReachesGateWithDistinctReason(string change, string reason)
    {
        await Refuses(await Grant(change), reason);
        Assert.Equal(1, gateCalls);
        Assert.Equal("rehost." + reason, observed!.GrantRefusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedIssuerRefusesAtIssueAndAtRedemption(bool between)
    {
        var grant = await Grant();
        await Revoke(between ? At.AddSeconds(1) : At.AddSeconds(-1));
        clock.Now = At.AddSeconds(2);
        await Refuses(grant, between ? "issuer_revoked_at_redemption" : "issuer_revoked_at_issue");
    }

    [Fact]
    public async Task BurnSurvivesRestartAndConcurrentPresentation()
    {
        var grant = await Grant();
        await Redeem(grant);
        factory = new Factory(path);
        await Refuses(grant, "already_redeemed");
        var raced = await Grant();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try { await Redeem(raced); return "allowed"; }
            catch (AuthorizationDeniedException ex) { return ex.Decision.Request.GrantRefusal; }
        })));
        Assert.Equal(new[] { "allowed", "rehost.already_redeemed" }, results.Order().ToArray());
    }

    [Fact]
    public async Task GateDenialDoesNotBurnGrant()
    {
        var grant = await Grant();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Redeem(grant, false));
        Assert.Null(ex.Decision.Request.GrantRefusal);
        Assert.Equal(AuthorizationVerdict.Allowed, (await Redeem(grant)).Verdict);
    }

    [Fact]
    public async Task InvalidDurableChainRefusesWithAuditAndStoredTrace()
    {
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.RosterRecords.SingleAsync(r => r.PartyId == "member");
            row.SignedPermissionsJson = "[]";
            await db.SaveChangesAsync();
        }
        var ex = await Refuses(await Grant(), "invalid_roster");
        var records = new List<AuditRecord>();
        await foreach (var row in trail.QueryAsync(new AuditQuery(new TenantId(Tenant)))) records.Add(row);
        var audit = Assert.Single(records);
        Assert.Equal("rehost.invalid_roster", audit.Payload.Payload.Body["code"]);
        var trace = await new AuthorizationTraceReader(trail, TestAuthorization.AllowGate())
            .ReadAsync(new TenantId(Tenant), Caller, audit.AuditId, At);
        Assert.Equal(AuthorizationTraceAvailability.Available, trace.Availability);
        Assert.Equal(4, trace.Steps.Count);
        Assert.Contains("grant-refusal:rehost.invalid_roster", trace.Steps[3].Facts);
        Assert.Same(observed, ex.Decision.Request);
    }

    private async Task<AuthorizationDeniedException> Refuses(RosterSignedRehostGrant grant, string reason)
    {
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Redeem(grant));
        Assert.Equal("rehost." + reason, ex.Decision.Request.GrantRefusal);
        var rendered = await AuthorizationRefusalRenderer.RenderAsync(ex.Decision, [], null);
        Assert.Equal("rehost." + reason, rendered.Code);
        return ex;
    }

    private async Task Revoke(DateTimeOffset at)
    {
        var (_, signed) = roster.SignRevoke("founder", founder, "member", verifier, at, Guid.NewGuid());
        await using var db = factory.CreateDbContext();
        db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(signed)
            .AttestReceipt(founder, "founder", signed.Signed.IssuedAt)));
        await db.SaveChangesAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Factory(string path) : IDbContextFactory<NodeLocalRosterDbContext>
    {
        public NodeLocalRosterDbContext CreateDbContext() => new(new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
