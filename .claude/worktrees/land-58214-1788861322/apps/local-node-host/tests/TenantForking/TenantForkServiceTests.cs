using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.LocalNodeHost.TenantForking;

namespace Harborline.Api.LocalNodeHost.Tests.TenantForking;

public sealed class TenantForkServiceTests
{
    [Fact]
    public async Task ForkAsync_SnapshotsAndAppliesVerifiedContentIntoFreshRosterAndLineage()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        source.GetText("name").Insert(0, "Ada Lovelace");
        var identities = new QueueIdentityFactory(
            new NodeIdentity("22222222222222222222222222222222", [2], [22]));
        var service = CreateService(engine, identities);

        var result = await service.ForkAsync(new TenantForkRequest(
            Tenant: new TenantId("tenant-a"),
            Actor: new ActorId("operator-a"),
            Grant: WholeTenantGrant(),
            Purpose: TenantForkPurpose.Troubleshooting,
            TrainingPiiTreatment: null,
            SourceDocuments: [source],
            ForkedAt: DateTimeOffset.UnixEpoch));

        Assert.NotEqual("tenant-a", result.RosterId);
        Assert.Equal("22222222222222222222222222222222", result.NodeIdentity.NodeId);
        var fork = Assert.Single(result.Documents);
        Assert.NotEqual(source.DocumentId, fork.DocumentId);
        Assert.Equal("Ada Lovelace", fork.GetText("name").Value);
        Assert.Matches("^[0-9a-f]{64}$", result.SourceContentHashes[source.DocumentId]);
    }

    [Fact]
    public async Task ForkAsync_CreatesZeroWayIsolationFromSourceRoster()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        source.GetText("name").Insert(0, "Ada");
        var sourceIdentity = new NodeIdentity("11111111111111111111111111111111", [1], [11]);
        var forkIdentity = new NodeIdentity("22222222222222222222222222222222", [2], [22]);
        var service = CreateService(engine, new QueueIdentityFactory(forkIdentity));

        var result = await service.ForkAsync(new TenantForkRequest(
            new TenantId("tenant-a"),
            new ActorId("operator-a"),
            WholeTenantGrant(),
            TenantForkPurpose.Troubleshooting,
            TrainingPiiTreatment: null,
            SourceDocuments: [source],
            ForkedAt: DateTimeOffset.UnixEpoch));
        var fork = Assert.Single(result.Documents);

        fork.GetText("name").Insert(3, " Byron");
        source.GetText("name").Insert(3, " Lovelace");

        Assert.Equal("Ada Lovelace", source.GetText("name").Value);
        Assert.Equal("Ada Byron", fork.GetText("name").Value);
        var sourceTrust = new MemberSetTrustPolicy([sourceIdentity.PublicKey]);
        Assert.False(sourceTrust.IsTrusted(HelloFor(forkIdentity)));
        Assert.False(result.TrustPolicy.IsTrusted(HelloFor(sourceIdentity)));
    }

    [Fact]
    public async Task ForkAsync_RefusesAndAuditsGrantOutsideDocumentScope()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        var audit = new InMemoryAuditLog(new InMemoryAssetStorage());
        var service = CreateService(
            engine,
            new QueueIdentityFactory(new NodeIdentity("22222222222222222222222222222222", [2], [22])),
            audit,
            authorization: new Harborline.Api.LocalNodeHost.Tests.Identity.FixedAuthorizationClosure(
                PermissionAtomSet.Of(PermissionAtom.Parse("tenant-fork:create@/records/calendar"))));
        var scopedGrant = WholeTenantGrant() with
        {
            Scope = ScopeExpression.Parse("/records/calendar"),
        };

        var refusal = await Assert.ThrowsAsync<TenantForkAuthorizationException>(async () =>
            await service.ForkAsync(new TenantForkRequest(
                new TenantId("tenant-a"),
                new ActorId("operator-a"),
                scopedGrant,
                TenantForkPurpose.Troubleshooting,
                TrainingPiiTreatment: null,
                SourceDocuments: [source],
                ForkedAt: DateTimeOffset.UnixEpoch)));

        Assert.Equal("tenant_fork.permission_denied", refusal.ErrorCode);
        var rejected = new List<AuditRecord>();
        await foreach (var record in audit.QueryAsync(new AuditQuery(Op: Op.Reject)))
        {
            rejected.Add(record);
        }

        var auditRecord = Assert.Single(rejected);
        Assert.Equal("operator-a", auditRecord.Actor.Value);
        Assert.Equal("tenant-a", auditRecord.Tenant.Value);
        Assert.Equal("tenant_fork.permission_denied", auditRecord.Payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ForkAsync_TwiceRetiresPriorIdentityWithoutReissuingReplicaPosition()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        var firstIdentity = new NodeIdentity("11111111111111111111111111111111", [1], [11]);
        var secondIdentity = new NodeIdentity("22222222222222222222222222222222", [2], [22]);
        var rosterStore = new InMemoryTenantForkRosterStore();
        var sequences = new RecordingSequenceAllocator();
        var service = CreateService(
            engine,
            new QueueIdentityFactory(firstIdentity, secondIdentity),
            rosterStore: rosterStore,
            sequences: sequences);
        var request = new TenantForkRequest(
            new TenantId("tenant-a"),
            new ActorId("operator-a"),
            WholeTenantGrant(),
            TenantForkPurpose.Training,
            new TrainingPiiTreatment("synthetic-v1"),
            [source],
            DateTimeOffset.UnixEpoch);

        var first = await service.ForkAsync(request);
        var second = await service.ForkAsync(request);

        Assert.NotEqual(first.NodeIdentity.NodeId, second.NodeIdentity.NodeId);
        Assert.Contains(first.NodeIdentity.NodeId, second.RetiredNodeIds);
        Assert.DoesNotContain(second.NodeIdentity.NodeId, second.RetiredNodeIds);
        Assert.NotEqual(
            (first.NodeIdentity.NodeId, first.InitialSequenceNumber),
            (second.NodeIdentity.NodeId, second.InitialSequenceNumber));
        Assert.Equal(
            [first.NodeIdentity.NodeId, second.NodeIdentity.NodeId],
            sequences.ReservedNodeIds);
    }

    [Fact]
    public async Task ForkAsync_TroubleshootingCarriesRegistryRetentionAndLegalHold()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        var minimumRetainUntil = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var holds = new HeldRegistry();
        var authority = new RegistryTenantForkObligationAuthority(
            new FixedRetentionResolver(minimumRetainUntil),
            holds);
        var service = CreateService(
            engine,
            new QueueIdentityFactory(new NodeIdentity("22222222222222222222222222222222", [2], [22])),
            obligations: authority);

        var result = await service.ForkAsync(new TenantForkRequest(
            new TenantId("tenant-a"),
            new ActorId("operator-a"),
            WholeTenantGrant(),
            TenantForkPurpose.Troubleshooting,
            TrainingPiiTreatment: null,
            SourceDocuments: [source],
            ForkedAt: DateTimeOffset.UnixEpoch));

        Assert.Equal("Configuration", result.Obligation.RetentionClass);
        Assert.Equal(minimumRetainUntil, result.Obligation.MinimumRetainUntil);
        Assert.True(result.Obligation.IsUnderLegalHold);
        Assert.Contains("Record:crdt-document/contacts", holds.Consulted);
        Assert.Contains("Class:Configuration", holds.Consulted);
    }

    [Fact]
    public async Task ForkAsync_TrainingRequiresPiiTreatmentAndRemainsDistinctFromTroubleshooting()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        var service = CreateService(
            engine,
            new QueueIdentityFactory(
                new NodeIdentity("11111111111111111111111111111111", [1], [11]),
                new NodeIdentity("22222222222222222222222222222222", [2], [22])));
        var troubleshooting = await service.ForkAsync(new TenantForkRequest(
            new TenantId("tenant-a"),
            new ActorId("operator-a"),
            WholeTenantGrant(),
            TenantForkPurpose.Troubleshooting,
            TrainingPiiTreatment: null,
            SourceDocuments: [source],
            ForkedAt: DateTimeOffset.UnixEpoch));

        var refusal = await Assert.ThrowsAsync<TenantForkDataHandlingException>(async () =>
            await service.ForkAsync(new TenantForkRequest(
                new TenantId("tenant-a"),
                new ActorId("operator-a"),
                WholeTenantGrant(),
                TenantForkPurpose.Training,
                TrainingPiiTreatment: null,
                SourceDocuments: [source],
                ForkedAt: DateTimeOffset.UnixEpoch)));
        var training = await service.ForkAsync(new TenantForkRequest(
            new TenantId("tenant-a"),
            new ActorId("operator-a"),
            WholeTenantGrant(),
            TenantForkPurpose.Training,
            new TrainingPiiTreatment("synthetic-v1"),
            [source],
            DateTimeOffset.UnixEpoch));

        Assert.Equal("tenant_fork.training_pii_treatment_required", refusal.ErrorCode);
        Assert.Equal(TenantForkPurpose.Troubleshooting, troubleshooting.Purpose);
        Assert.Null(troubleshooting.TrainingPiiTreatment);
        Assert.Equal(TenantForkPurpose.Training, training.Purpose);
        Assert.Equal("synthetic-v1", training.TrainingPiiTreatment?.TreatmentId);
    }

    [Fact]
    public void StartupAssertion_RefusesMissingPeerTrustPolicy()
    {
        var refusal = Assert.Throws<PeerTrustPolicyConfigurationException>(() =>
            PeerTrustPolicyStartupAssertion.Require(null));
        var configured = new MemberSetTrustPolicy([[1]]);

        Assert.Equal("sync.peer_trust_policy_required", refusal.ErrorCode);
        Assert.Same(configured, PeerTrustPolicyStartupAssertion.Require(configured));
    }

    [Fact]
    public async Task ForkAsync_RefusesSnapshotWhoseTransferHashDoesNotMatch()
    {
        var engine = new StubCrdtEngine();
        await using var source = engine.CreateDocument("contacts");
        source.GetText("name").Insert(0, "Ada");
        var identities = new QueueIdentityFactory(
            new NodeIdentity("22222222222222222222222222222222", [2], [22]));
        var service = CreateService(
            engine,
            identities,
            transfer: new CorruptingSnapshotTransfer());

        var refusal = await Assert.ThrowsAsync<ContentHashMismatchException>(async () =>
            await service.ForkAsync(new TenantForkRequest(
                new TenantId("tenant-a"),
                new ActorId("operator-a"),
                WholeTenantGrant(),
                TenantForkPurpose.Troubleshooting,
                TrainingPiiTreatment: null,
                SourceDocuments: [source],
                ForkedAt: DateTimeOffset.UnixEpoch)));

        Assert.Equal("contacts", refusal.DocumentId);
        Assert.Equal(0, identities.CreatedCount);
    }

    private static TenantForkService CreateService(
        ICrdtEngine engine,
        INodeIdentityFactory identities,
        IAuditLog? audit = null,
        ITenantForkRosterStore? rosterStore = null,
        IOutboundSequenceAllocator? sequences = null,
        ITenantForkObligationAuthority? obligations = null,
        ITenantForkSnapshotTransfer? transfer = null,
        IAuthorizationClosureReader? authorization = null) =>
        new(
            engine,
            identities,
            obligations ?? new AllowingObligationAuthority(),
            audit ?? new InMemoryAuditLog(new InMemoryAssetStorage()),
            rosterStore ?? new InMemoryTenantForkRosterStore(),
            sequences ?? new RecordingSequenceAllocator(),
            transfer ?? InMemoryTenantForkSnapshotTransfer.Instance,
            TimeProvider.System,
            authorization ?? new Harborline.Api.LocalNodeHost.Tests.Identity.FixedAuthorizationClosure(
                PermissionAtomSet.Of(PermissionAtom.Parse("tenant-fork:create@/"))));

    private static AccessGrant WholeTenantGrant() => new(
        GrantId.New(),
        new TenantId("tenant-a"),
        new ActorId("operator-a"),
        AccessGrantAuthorizationSeed.MemberRole,
        ScopeExpression.Parse("/"), GrantResidency.Cache,
        new GrantValidity(DateTimeOffset.MinValue), GranterKind.Person,
        new ActorId("owner-a"), DateTimeOffset.UnixEpoch,
        new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
            new ActorId("owner-a")), DateTimeOffset.UnixEpoch);

    private static HelloMessage HelloFor(NodeIdentity identity) => new(
        identity.NodeIdBytes,
        "1",
        ["1"],
        identity.PublicKey,
        0,
        []);

    private sealed class QueueIdentityFactory(params NodeIdentity[] identities) : INodeIdentityFactory
    {
        private readonly Queue<NodeIdentity> _identities = new(identities);

        public int CreatedCount { get; private set; }

        public NodeIdentity CreateFresh()
        {
            CreatedCount++;
            return _identities.Dequeue();
        }
    }

    private sealed class CorruptingSnapshotTransfer : ITenantForkSnapshotTransfer
    {
        public ReadOnlyMemory<byte> Transfer(ReadOnlyMemory<byte> snapshot)
        {
            var corrupted = snapshot.ToArray();
            corrupted[^1] ^= 0xff;
            return corrupted;
        }
    }

    private sealed class RecordingSequenceAllocator : IOutboundSequenceAllocator
    {
        public List<string> ReservedNodeIds { get; } = [];

        public ValueTask<ulong> ReserveNextAsync(string nodeId, CancellationToken ct = default)
        {
            ReservedNodeIds.Add(nodeId);
            return ValueTask.FromResult(1UL);
        }
    }

    private sealed class FixedRetentionResolver(DateTimeOffset minimumRetainUntil)
        : IRetentionPolicyResolver
    {
        public ValueTask<AuditRetentionPolicy> GetActiveAsync(
            TenantId tenant,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RetentionVerdict> ResolveAsync(
            TenantId tenant,
            AuditEventClass eventClass,
            DateTimeOffset recordCreatedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RetentionVerdict(
                eventClass,
                minimumRetainUntil,
                minimumRetainUntil.AddDays(30),
                IsJurisdictionFloor: false));
    }

    private sealed class HeldRegistry : ILegalHoldRegistry
    {
        public List<string> Consulted { get; } = [];

        public ValueTask<bool> IsHeldAsync(
            TenantId tenant,
            HeldRef heldRef,
            CancellationToken ct = default)
        {
            Consulted.Add(heldRef.Canonical);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> IsSubjectHeldAsync(
            TenantId tenant,
            SubjectId subject,
            CancellationToken ct = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class AllowingObligationAuthority : ITenantForkObligationAuthority
    {
        public ValueTask<TenantForkObligation> ResolveAsync(
            TenantId tenant,
            IReadOnlyList<string> documentIds,
            DateTimeOffset capturedAt,
            CancellationToken ct = default) =>
            ValueTask.FromResult(new TenantForkObligation(
                RetentionClass: "configuration",
                MinimumRetainUntil: capturedAt.AddDays(30),
                IsUnderLegalHold: false));
    }
}
