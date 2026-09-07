using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Roster;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

public sealed class CompromisedDeviceResponseServiceTests
{
    private const string TeamId = "71560000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset RevokedAt =
        new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task RespondAsync_records_a_signed_entitlement_snapshot()
    {
        var signer = new Ed25519Signer(KeyPair.Generate());
        var store = new CapturingResponseStore();
        var service = new CompromisedDeviceResponseService(
            new StubRevocationPublisher(),
            new StaticEntitlementSnapshotSource("roster", "contacts", "comms:team-a"),
            new DeferredCompromiseKeyRotation(),
            store,
            signer,
            new FixedTimeProvider(RevokedAt),
            TestAuthorization.AllowGate());

        var result = await service.RespondAsync(new CompromisedDeviceResponseRequest(
            TeamId: TeamId,
            RevokedPartyId: "stolen-node",
            RevokedByPartyId: "operator-a"), Authority());

        var persisted = Assert.Single(store.Records);
        Assert.Equal(result.CorrelationId, persisted.CorrelationId);
        Assert.Equal(
            ["comms:team-a", "contacts", "roster"],
            persisted.Payload.EntitledDocumentIds);
        Assert.Equal("revocation-record-1", persisted.Payload.RevocationRecordId);
        Assert.Equal(RevokedAt, persisted.Payload.RevokedAt);

        var signed = new SignedOperation<string>(
            persisted.CanonicalPayloadJson,
            PrincipalId.FromBase64Url(persisted.SignerPublicKey),
            persisted.SignedAt,
            Guid.Parse(persisted.SigningNonce),
            Signature.FromBytes(persisted.Signature));
        Assert.True(new Ed25519Verifier().Verify(signed));
    }

    [Fact]
    public async Task RespondAsync_persists_the_signed_record_across_store_instances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-device-response-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "roster.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var signer = new Ed25519Signer(KeyPair.Generate());
            var service = new CompromisedDeviceResponseService(
                new StubRevocationPublisher(),
                new StaticEntitlementSnapshotSource("contacts", "roster"),
                new DeferredCompromiseKeyRotation(),
                new NodeRosterCompromisedDeviceResponseStore(factory),
                signer,
                new FixedTimeProvider(RevokedAt),
                TestAuthorization.AllowGate());

            var result = await service.RespondAsync(new CompromisedDeviceResponseRequest(
                TeamId, "stolen-node", "operator-a"), Authority());

            var reopenedStore = new NodeRosterCompromisedDeviceResponseStore(factory);
            var persisted = await reopenedStore.FindAsync(result.CorrelationId);
            Assert.NotNull(persisted);
            Assert.Equal(["contacts", "roster"], persisted.Payload.EntitledDocumentIds);
            Assert.Equal(Signature.LengthInBytes, persisted.Signature.Length);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task RespondAsync_records_key_deferral_operator_account_and_one_correlated_audit_event()
    {
        var store = new CapturingResponseStore();
        var service = new CompromisedDeviceResponseService(
            new StubRevocationPublisher(),
            new StaticEntitlementSnapshotSource("contacts"),
            new DeferredCompromiseKeyRotation(),
            store,
            new Ed25519Signer(KeyPair.Generate()),
            new FixedTimeProvider(RevokedAt),
            TestAuthorization.AllowGate());

        var result = await service.RespondAsync(new CompromisedDeviceResponseRequest(
            TeamId, "stolen-node", "operator-a"), Authority());

        var payload = Assert.Single(store.Records).Payload;
        Assert.Equal("deferred", payload.KeyResponse.Disposition);
        Assert.Equal(
            [
                "install root seed",
                "team transport signing subkey",
                "team direct-message encryption subkey",
                "team recovery subkey",
                "team X-Wing subkey",
                "SQLCipher store key",
                "tenant and subject content keys",
            ],
            payload.KeyResponse.RemainingExposedKeys);
        Assert.Equal(
            "The device retains every document in the entitlement snapshot and the operation history already replicated to it. "
            + "Deleted content remains in its operation log until ticket 039 compaction lands. Revocation blocks future trust "
            + "but removes no data or key material from the device. Re-keying protects future data only; it does not undo "
            + "disclosure or make copies already obtained unreadable. Key rotation is deferred, so every key family listed "
            + "in this account remains exposed.",
            payload.OperatorAccount);
        Assert.Equal(
            ["revocation", "key-response", "exposure-accounting"],
            payload.AuditSteps.Select(step => step.Kind));
        Assert.All(payload.AuditSteps, step => Assert.Equal(result.CorrelationId, step.CorrelationId));
        Assert.All(payload.AuditSteps, step => Assert.Equal(RevokedAt, step.RecordedAt));
    }

    private sealed class StubRevocationPublisher : ICompromisedDeviceRevocationPublisher
    {
        public ValueTask<CompromisedDeviceRevocation> RevokeAsync(
            CompromisedDeviceResponseRequest request,
            string correlationId,
            AuthorizationDecision admittedDecision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CompromisedDeviceRevocation(
                RecordId: "revocation-record-1",
                TeamId: request.TeamId,
                RevokedPartyId: request.RevokedPartyId,
                RevokedByPartyId: request.RevokedByPartyId,
                RevokedAt: admittedDecision.DecidedAt,
                Signature: "signed-roster-revocation"));
    }

    private sealed class StaticEntitlementSnapshotSource(params string[] documentIds)
        : IDeviceEntitlementSnapshotSource
    {
        public IReadOnlyCollection<string> SnapshotDocumentIds(string teamId) => documentIds;
    }

    private sealed class CapturingResponseStore : ICompromisedDeviceResponseStore
    {
        public List<CompromisedDeviceResponseRecord> Records { get; } = [];

        public ValueTask AppendAsync(
            CompromisedDeviceResponseRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static AuthorizationWriteContext Authority() =>
        new(new ActorId("operator-principal"), new TenantId(TeamId), RevokedAt);
}
