using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.LocalNodeHost.BackupRestore;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.BackupRestore;

public sealed class NodeRehostCompositionTests
{
    [Theory]
    [InlineData(null, "rehost.malformed")]
    [InlineData("arbitrary-nonblank", "rehost.malformed")]
    [InlineData("valid", null)]
    [InlineData("read-only", "rehost.out_of_scope")]
    public async Task ComposedRestoreRedeemsBeforeAnyWriteAndRefusesReplay(string? supplied, string? reason)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rehost-composed-{Guid.NewGuid():N}");
        var writes = new RestorePorts();
        var trail = new InMemoryAuditTrail();
        var calls = new List<AuthorizationGateRequest>();
        var at = DateTimeOffset.Parse("2026-09-08T04:00:00Z");
        try
        {
            await Assert.ThrowsAsync<ProbeComplete>(() => global::LocalNodeHostComposition.RunAsync(
                ["--LocalNode:RootSeedHex=" + new string('2', 64), "--urls=http://127.0.0.1:7330"],
                sessionTokenOverride: "rehost-composition-probe", dataDirectory: directory,
                installFootprintRootOverride: directory,
                kernelClock: new FixedClock(at),
                finalServiceRegistration: services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<IRootSeedRestorer>(writes));
                    services.Replace(ServiceDescriptor.Singleton<IHomeEpochStore>(writes));
                    services.Replace(ServiceDescriptor.Singleton(TestAuthorization.Gate(true, calls.Add)));
                    services.Replace(ServiceDescriptor.Singleton(sp => new AuthorizationRefusalAudit(trail,
                        sp.GetRequiredService<IOperationSigner>(), NullLogger<AuthorizationRefusalAudit>.Instance)));
                },
                finalServiceProviderProbe: (services, factory) =>
                {
                    var provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                    using var lifetime = Assert.IsAssignableFrom<IDisposable>(provider);
                    ExerciseAsync(provider).GetAwaiter().GetResult();
                    throw new ProbeComplete();
                }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        async Task ExerciseAsync(IServiceProvider provider)
        {
            var restore = provider.GetRequiredService<NodeRehostService>();
            var grants = provider.GetRequiredService<IRosterRehostGrantProvider>();
            Assert.IsType<SignedRosterRehostGrantProvider>(grants);
            var signer = provider.GetRequiredService<IOperationSigner>();
            var contexts = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using var db = await contexts.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            var tenant = Guid.NewGuid();
            var roster = MemberRoster.Genesis(tenant, "founder", signer, new Ed25519Verifier(),
                at.AddHours(-1), Guid.NewGuid());
            db.RosterRecords.AddRange(roster.EnumerateAdmissions().Select(a =>
                NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(a)
                    .AttestReceipt(signer, "founder", a.Admission.IssuedAt))));
            await db.SaveChangesAsync();
            var identity = new NodeIdentity("replacement", KeyPair.Generate().PrincipalId.AsSpan().ToArray(), []);
            var caller = new ActorId(signer.IssuerId.ToBase64Url());
            RosterSignedRehostGrant? grant = supplied is null ? null : new(supplied);
            if (supplied is "valid" or "read-only")
            {
                grant = await grants.ObtainAsync(tenant.ToString("D"), "old", identity, ["trustee"]);
                if (supplied == "read-only")
                {
                    var issued = JsonSerializer.Deserialize<SignedOperation<RehostGrantPayload>>(grant.SerializedGrant)!;
                    var signed = await signer.SignAsync(issued.Payload with
                    { Acts = [SignedRosterRehostGrantProvider.ReadCanonical] }, at, Guid.NewGuid());
                    grant = new(JsonSerializer.Serialize(signed));
                }
            }
            Assert.Empty(calls);
            Assert.Equal(0, await BurnCount());
            var request = new NodeRehostRequest(tenant.ToString("D"), "old", caller, grant);
            var session = new NodeRehostSession(identity, writes, writes, writes);
            if (reason is not null)
            {
                var refusal = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                    restore.RestoreAsync(request, session).AsTask());
                Assert.Equal(reason, refusal.Decision.Request.GrantRefusal);
                Assert.Same(Assert.Single(calls), refusal.Decision.Request);
                Assert.Empty(writes.Calls); // Includes key recovery, seed store, holder read, promotion and epoch store.
                Assert.Null(await writes.GetCurrentEpochAsync(request.TenantId));
                Assert.Equal(0, await BurnCount());
                Assert.Equal(1, await db.RosterRecords.CountAsync());
                var body = await AuthorizationRefusalRenderer.RenderAsync(refusal.Decision, [], null);
                Assert.Equal(reason, body.Code);
                Assert.Equal(5, JsonSerializer.SerializeToElement(body).EnumerateObject().Count());
                var auditRows = new List<AuditRecord>();
                await foreach (var row in trail.QueryAsync(new AuditQuery(new TenantId(request.TenantId)))) auditRows.Add(row);
                Assert.Equal(reason, Assert.Single(auditRows).Payload.Payload.Body["code"]);
                return;
            }
            var result = await restore.RestoreAsync(request, session);
            Assert.Same(Assert.Single(calls), result.Decision.Request);
            Assert.Equal(AuthorizationVerdict.Allowed, result.Decision.Verdict);
            Assert.Equal(new[] { "recover", "seed", "holders", "promotion", "epoch" }, writes.Calls);
            Assert.Equal("abc"u8.ToArray(), Assert.Single(result.Documents).Snapshot);
            Assert.Equal(1, await BurnCount());
            var replay = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                restore.RestoreAsync(request, session).AsTask());
            Assert.Equal("rehost.already_redeemed", replay.Decision.Request.GrantRefusal);
            Assert.Equal(2, calls.Count);
            Assert.Same(calls[1], replay.Decision.Request);
            Assert.Equal(5, writes.Calls.Count);
            Assert.Equal(1, await BurnCount());

            Task<long> BurnCount() => db.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM rehost_grant_burns").SingleAsync();
        }
    }

    [Fact]
    public async Task MigrationPreservesExistingBurnsAndRoster()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rehost-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new NodeLocalRosterDbContext(new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.GetService<IMigrator>().MigrateAsync("20260831184210_RosterAddAdministratorAuthority");
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE rehost_grant_burns (tenant TEXT NOT NULL, issuer TEXT NOT NULL, nonce TEXT NOT NULL, PRIMARY KEY (tenant, issuer, nonce))");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO rehost_grant_burns VALUES ('tenant', 'issuer', 'nonce')");
            using var keys = KeyPair.Generate();
            var roster = MemberRoster.Genesis(Guid.NewGuid(), "founder", new Ed25519Signer(keys),
                new Ed25519Verifier(), DateTimeOffset.UnixEpoch, Guid.NewGuid());
            var admission = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(
                Assert.Single(roster.EnumerateAdmissions())));
            // Seed the historical schema with its actual columns, before applying later roster migrations.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO roster_records
                    (id, kind, team_id, party_id, public_key, permissions, admitted_by_key,
                     admitted_by_party, nonce, signature, is_genesis, issued_at)
                VALUES ({admission.Id}, {admission.Kind}, {admission.TeamId}, {admission.PartyId},
                    {admission.PublicKeyB64Url}, {admission.SignedPermissionsJson}, {admission.AdmittedByPublicKey},
                    {admission.AdmittedByPartyId}, {admission.NonceGuid}, {admission.SignatureB64Url},
                    {admission.IsGenesis}, {admission.IssuedAtUtc.ToUnixTimeMilliseconds()})
                """);
            await db.Database.MigrateAsync();
            Assert.Contains("20260908030000_RosterAddRehostGrantBurns", await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal("nonce", await db.Database.SqlQueryRaw<string>("SELECT nonce AS Value FROM rehost_grant_burns").SingleAsync());
            Assert.Equal(admission.Id, (await db.RosterRecords.SingleAsync()).Id);
        }
        finally { File.Delete(path); }
    }

    private sealed class ProbeComplete : Exception;
    private sealed class FixedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
    private sealed class RestorePorts : ITrusteeKeyRecovery, IRootSeedRestorer, ICanonicalRehostSource,
        IHomeEpochPromotionAuthority, IHomeEpochStore
    {
        public List<string> Calls { get; } = [];
        private HomeEpochRecord? epoch;
        public ValueTask<RecoveredNodeKeys> RecoverAsync(string replacementNodeId, CancellationToken ct = default)
        { Calls.Add("recover"); return ValueTask.FromResult(new RecoveredNodeKeys(new byte[32], ["trustee"])); }
        public Task RestoreRootSeedAsync(ReadOnlyMemory<byte> seed, CancellationToken ct)
        { Calls.Add("seed"); return Task.CompletedTask; }
        public ValueTask<IReadOnlyList<CanonicalReplicaDocument>> ReConvergeFromHoldersAsync(
            RosterSignedRehostGrant grant, CancellationToken ct = default)
        {
            Calls.Add("holders");
            return ValueTask.FromResult<IReadOnlyList<CanonicalReplicaDocument>>([new("contacts", "abc"u8.ToArray(),
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]);
        }
        public ValueTask<HomeEpochRecord> AuthorizeRecoveryFailoverAsync(string tenantId, string replacementNodeId,
            CancellationToken ct = default)
        {
            Calls.Add("promotion");
            return ValueTask.FromResult(new HomeEpochRecord { TenantId = tenantId, HomeDeviceId = replacementNodeId,
                EpochNumber = 2, PreviousEpochNumber = 1, PromotionKind = HomePromotionKind.RecoveryFailover,
                IssuedAt = DateTimeOffset.UnixEpoch, Nonce = Guid.Empty, IssuerId = "issuer", Signature = "signature" });
        }
        public Task<HomeEpochRecord?> GetCurrentEpochAsync(string tenantId, CancellationToken ct = default) => Task.FromResult(epoch);
        public Task AdvanceAsync(HomeEpochRecord proposed, CancellationToken ct = default)
        { Calls.Add("epoch"); epoch = proposed; return Task.CompletedTask; }
    }
}
