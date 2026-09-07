using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Versions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Governance.Definitions;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using System.Text.Json;

namespace Harborline.Api.LocalNodeHost.Tests.Definitions;

/// <summary>Exercises retention and legal-hold resolution through the public definition-envelope seam.</summary>
public sealed class DefinitionEnvelopeResolutionTests
{
    [Fact(DisplayName = "the same definition envelope observes a governed data-class hold")]
    public async Task Same_definition_envelope_observes_governed_data_class_hold()
    {
        var holdStore = new InMemoryLegalHoldStore();
        var resolver = new FormDefinitionEnvelopeResolver(
            new AspectResolver(new InMemoryPolicyRegistry()),
            new DefaultFieldClassAuditEventClassMap(),
            new MutableRetentionPolicyResolver(new RetentionVerdict(
                AuditEventClass.Configuration,
                new DateTimeOffset(2027, 8, 18, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2033, 8, 18, 12, 0, 0, TimeSpan.Zero),
                IsJurisdictionFloor: false)),
            new LegalHoldRegistry(holdStore));
        var definition = DefinitionWithPiiRecords();

        var before = await resolver.ResolveAsync(definition, definition.CreatedAt);
        Assert.Equal(DefinitionLegalHold.NotHeld, before.LegalHold);

        await holdStore.AppendHoldAsync(new LegalHoldEntry(
            new LegalHoldId("hold-definition-pii"),
            definition.Tenant,
            HeldRef.ForClass("pii"),
            "identity records discovery",
            new ActorId("legal-officer"),
            new DateTimeOffset(2026, 8, 18, 13, 0, 0, TimeSpan.Zero)));

        var after = await resolver.ResolveAsync(definition, definition.CreatedAt);

        Assert.Equal(DefinitionLegalHold.Held, after.LegalHold);
    }

    [Fact(DisplayName = "a legal hold blocks definition withdrawal and supersession")]
    public async Task Legal_hold_blocks_definition_withdrawal_and_supersession()
    {
        var holdStore = new InMemoryLegalHoldStore();
        var tenant = new TenantId("tenant-retention-resolution");
        var holdId = new LegalHoldId("hold-definition-financial");
        await holdStore.AppendHoldAsync(new LegalHoldEntry(
            holdId,
            tenant,
            HeldRef.ForClass("Financial"),
            "financial records discovery",
            new ActorId("legal-officer"),
            new DateTimeOffset(2026, 8, 18, 13, 0, 0, TimeSpan.Zero)));
        var retention = new MutableRetentionPolicyResolver(new RetentionVerdict(
            AuditEventClass.Financial,
            new DateTimeOffset(2027, 8, 18, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2033, 8, 18, 12, 0, 0, TimeSpan.Zero),
            IsJurisdictionFloor: false));
        var envelopeResolver = new FormDefinitionEnvelopeResolver(
            new AspectResolver(new InMemoryPolicyRegistry()),
            new DefaultFieldClassAuditEventClassMap(),
            retention,
            new LegalHoldRegistry(holdStore));
        var entityStore = new CountingEntityStore(
            new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System));
        var inner = new EntityStoreFormDefinitionStore(entityStore, TimeProvider.System);
        var heldDefinition = DefinitionWithRetentionClass("Financial");
        await inner.RegisterAsync(heldDefinition);
        entityStore.ResetWrites();
        var lifecycle = TestAuthorization.FormLifecycle(
            inner,
            TestAuthorization.AllowGate(),
            TestAuthorization.RoleGate(),
            new FormDefinitionLegalHoldValidator(envelopeResolver));
        var authority = new AuthorizationWriteContext(
            new ActorId("definition-author"), tenant, heldDefinition.CreatedAt);

        var withdrawal = await Assert.ThrowsAsync<DefinitionUnderLegalHoldException>(async () =>
            await lifecycle.WithdrawAsync(new DefinitionCoordinates(
                heldDefinition.Tenant, heldDefinition.Id.Value, heldDefinition.Version.ToString()), authority));
        Assert.Equal("definition.legal_hold.blocks_withdrawal", withdrawal.ErrorCode);
        Assert.Equal(0, entityStore.WriterCalls);
        Assert.Equal(FormDefinitionStatus.Published, (await inner.GetAsync(new DefinitionCoordinates(
            heldDefinition.Tenant, heldDefinition.Id.Value, heldDefinition.Version.ToString()))).Status);

        var replacement = DefinitionWithRetentionClass("Financial") with
        {
            Version = new SemanticVersion(2, 0, 0),
            Status = FormDefinitionStatus.Draft,
        };
        var supersession = await Assert.ThrowsAsync<DefinitionUnderLegalHoldException>(async () =>
            await lifecycle.RegisterAndPublishAsync(replacement, authority));
        Assert.Equal("definition.legal_hold.blocks_supersession", supersession.ErrorCode);
        Assert.Equal(0, entityStore.WriterCalls);
        await Assert.ThrowsAsync<FormDefinitionNotFoundException>(() => inner.GetAsync(new DefinitionCoordinates(
            replacement.Tenant, replacement.Id.Value, replacement.Version.ToString())).AsTask());

        await holdStore.AppendReleaseAsync(new LegalHoldRelease(
            holdId,
            tenant,
            [new ActorId("legal-approver-a"), new ActorId("legal-approver-b")],
            "discovery complete",
            heldDefinition.CreatedAt.AddDays(1)));

        var cleared = await lifecycle.WithdrawAsync(new DefinitionCoordinates(
            heldDefinition.Tenant, heldDefinition.Id.Value, heldDefinition.Version.ToString()), authority);
        Assert.Equal(FormDefinitionStatus.Withdrawn, cleared.Status);
        Assert.True(entityStore.WriterCalls > 0);
    }

    [Fact(DisplayName = "authored envelopes reject retention and legal-hold registry bypasses")]
    public void Authored_envelopes_reject_retention_and_legal_hold_registry_bypasses()
    {
        var retentionBypass = Assert.Throws<DefinitionPolicyAuthorityBypassException>(() =>
            NewAuthoredEnvelope(new DefinitionRetentionClass("Financial")));
        Assert.Equal("definition.retention.registry_bypass", retentionBypass.ErrorCode);

        var legalHoldBypass = Assert.Throws<DefinitionPolicyAuthorityBypassException>(() =>
            NewAuthoredEnvelope(DefinitionLegalHold.Held));
        Assert.Equal("definition.legal_hold.registry_bypass", legalHoldBypass.ErrorCode);
    }

    [Fact(DisplayName = "the same definition envelope observes a retention registry change")]
    public async Task Same_definition_envelope_observes_retention_registry_change()
    {
        var retention = new MutableRetentionPolicyResolver(
            new RetentionVerdict(
                AuditEventClass.Financial,
                new DateTimeOffset(2027, 8, 18, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2033, 8, 18, 12, 0, 0, TimeSpan.Zero),
                IsJurisdictionFloor: false));
        var resolver = new FormDefinitionEnvelopeResolver(
            new AspectResolver(new InMemoryPolicyRegistry()),
            new DefaultFieldClassAuditEventClassMap(),
            retention,
            new LegalHoldRegistry(new InMemoryLegalHoldStore()));
        var definition = DefinitionWithRetentionClass("Financial");
        var recordCreatedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        var first = await resolver.ResolveAsync(definition, recordCreatedAt);

        Assert.Equal(AuditEventClass.Financial, first.RetentionClass.EventClass);
        Assert.Equal(new DateTimeOffset(2027, 8, 18, 12, 0, 0, TimeSpan.Zero), first.RetentionClass.MinimumHoldUntil);

        retention.Current = new RetentionVerdict(
            AuditEventClass.Financial,
            new DateTimeOffset(2033, 8, 18, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2036, 8, 18, 12, 0, 0, TimeSpan.Zero),
            IsJurisdictionFloor: false);

        var second = await resolver.ResolveAsync(definition, recordCreatedAt);

        Assert.Equal(new DateTimeOffset(2033, 8, 18, 12, 0, 0, TimeSpan.Zero), second.RetentionClass.MinimumHoldUntil);
        Assert.Equal(DefinitionLegalHold.NotHeld, second.LegalHold);
    }

    private static FormDefinition DefinitionWithRetentionClass(string floorClass)
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        return new FormDefinition(
            Id: new FormDefinitionId("forms/retention-resolution"),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Published,
            Tenant: new TenantId("tenant-retention-resolution"),
            Owner: IdentityRef.System,
            SchemaRef: new SchemaId("sha256:retention-resolution"),
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["amount"] = new(
                        InternationalizedText.FromInvariant("Amount"),
                        Aspects: new AspectOverlay(
                            Lifecycle: new LifecycleAspect(
                                Retention: new RetentionRequirement("SOX", floorClass, 365)))),
                },
                Sections: Array.Empty<FormSection>(),
                Rules: Array.Empty<RuleDefinition>()),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    private static FormDefinition DefinitionWithPiiRecords()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        return new FormDefinition(
            Id: new FormDefinitionId("forms/pii-hold-resolution"),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Published,
            Tenant: new TenantId("tenant-pii-hold-resolution"),
            Owner: IdentityRef.System,
            SchemaRef: new SchemaId("sha256:pii-hold-resolution"),
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["name"] = new(
                        InternationalizedText.FromInvariant("Name"),
                        PiiSensitivity: PiiSensitivity.Sensitive),
                },
                Sections: Array.Empty<FormSection>(),
                Rules: Array.Empty<RuleDefinition>()),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    private static DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance> NewAuthoredEnvelope(
        DefinitionRetentionClass retentionClass)
        => new(
            new FormDefinitionId("forms/authority-bypass"),
            new SemanticVersion(1, 0, 0),
            new TenantId("tenant-authority-bypass"),
            CascadeLayer.Tenant,
            new FormDefinitionProvenance(IdentityRef.System, Lineage: null),
            retentionClass,
            Array.Empty<DefinitionRequirement>());

    private static DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance> NewAuthoredEnvelope(
        DefinitionLegalHold legalHold)
        => new(
            new FormDefinitionId("forms/authority-bypass"),
            new SemanticVersion(1, 0, 0),
            new TenantId("tenant-authority-bypass"),
            CascadeLayer.Tenant,
            new FormDefinitionProvenance(IdentityRef.System, Lineage: null),
            legalHold,
            Array.Empty<DefinitionRequirement>());

    private sealed class MutableRetentionPolicyResolver(RetentionVerdict current) : IRetentionPolicyResolver
    {
        public RetentionVerdict Current { get; set; } = current;

        public ValueTask<AuditRetentionPolicy> GetActiveAsync(
            TenantId tenant,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AuditRetentionPolicy.Default);

        public ValueTask<RetentionVerdict> ResolveAsync(
            TenantId tenant,
            AuditEventClass eventClass,
            DateTimeOffset recordCreatedAt,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Current);
    }

    private sealed class CountingEntityStore(IEntityMutationStore inner) : IEntityMutationStore
    {
        public int WriterCalls { get; private set; }
        public void ResetWrites() => WriterCalls = 0;

        public Task<Entity?> GetAsync(EntityId id, VersionSelector version = default, CancellationToken ct = default) =>
            inner.GetAsync(id, version, ct);

        public Task<EntityId> CreateAsync(SchemaId schema, JsonDocument body, CreateOptions options, CancellationToken ct = default)
        {
            WriterCalls++;
            return inner.CreateAsync(schema, body, options, ct);
        }

        public Task<IReadOnlyList<EntityId>> CreateBatchAsync(IEnumerable<EntityDraft> drafts, CancellationToken ct = default)
        {
            WriterCalls++;
            return inner.CreateBatchAsync(drafts, ct);
        }

        public Task<VersionId> UpdateAsync(EntityId id, JsonDocument body, UpdateOptions options, CancellationToken ct = default)
        {
            WriterCalls++;
            return inner.UpdateAsync(id, body, options, ct);
        }

        public Task DeleteAsync(EntityId id, DeleteOptions options, CancellationToken ct = default)
        {
            WriterCalls++;
            return inner.DeleteAsync(id, options, ct);
        }

        public IAsyncEnumerable<Entity> QueryAsync(EntityQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);
    }
}
