using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 217 / L628 -- the Auditor holds exactly one authorization capability: read the audit.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here runs against the REAL platform seed
/// (<c>AccessGrantAuthorizationSeed.InstallAsync</c>), the REAL definition admission, and the REAL
/// <see cref="AuthorizationGate"/> over the definition-joined closure -- so a refusal is the production
/// resolution refusing, not a double's opinion. The role vocabulary and the store are the in-memory ones
/// the access-grant module registers by default, which is the same composition the node host narrows.
/// </para>
/// <para>
/// The three shapes the acceptance names: the CATALOGUE assertion (exactly one effective definition names
/// Auditor, and it is <c>audit:read</c>); the generated REFUSAL MATRIX over every other platform operation,
/// writes included; and WIDENING -- neither a pack install nor a tenant binding revision can give the
/// Auditor a second capability.
/// </para>
/// </remarks>
public sealed class AuditorSingleCapabilityTests
{
    private static readonly TenantId Tenant = new("tenant-auditor");
    private static readonly ActorId TheAuditor = new("auditor-principal");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-06T12:00:00Z");

    /// <summary>Every platform operation the Auditor must NOT hold -- generated from the catalogue, so a
    /// new operation joins the matrix by existing rather than by being remembered here.</summary>
    public static TheoryData<string> EveryOtherOperation()
    {
        var data = new TheoryData<string>();
        foreach (var operation in PermissionVocabulary.Operations)
        {
            if (!string.Equals(operation.Value, Permission.AuditRead, StringComparison.Ordinal))
            {
                data.Add(operation.Value);
            }
        }

        return data;
    }

    [Fact(DisplayName = "Catalogue: 28 offered atoms after adding catalogue:read; only audit:read offers Auditor")]
    public async Task Exactly_one_effective_definition_names_the_auditor()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(28, PermissionVocabulary.Operations.Count);
        Assert.DoesNotContain(PermissionVocabulary.Operations, operation => operation.Value.StartsWith("stories:", StringComparison.Ordinal));
        var rows = await h.Catalogue.ListAsync(Tenant);

        var auditorRows = rows
            .Where(row => row.EffectiveRoles.Roles.Contains(RoleReference.Auditor))
            .ToArray();
        var row = Assert.Single(auditorRows);
        Assert.Equal(Permission.AuditRead, row.Definition.Operation.Value);
        // The whole catalogue is installed -- every platform operation plus the additive system-principal
        // definitions -- so "exactly one" is a statement about the platform's offer, not about a sparse store.
        Assert.All(
            PermissionVocabulary.Operations,
            operation => Assert.Contains(rows, item => item.Definition.Operation == operation));
    }

    [Fact(DisplayName = "Positive: the Auditor reads the install's audit trail and one audit entry")]
    public async Task The_auditor_reads_the_audit_trail_and_one_entry()
    {
        await using var h = await Harness.CreateAsync();
        await h.GrantAuditorAsync();

        // The LIST act -- over the install's trail, the shape AuditEventRoutes' list route resolves.
        var list = await h.DecideInstallWideAsync(Permission.AuditRead);
        // The DETAIL act -- one entry, ordinary and bootstrap alike; the entry's id is the record target.
        var ordinary = await h.DecideAsync(Permission.AuditRead, "audit-ordinary-1");
        var bootstrap = await h.DecideAsync(Permission.AuditRead, "audit-bootstrap-1");

        Assert.Equal(AuthorizationVerdict.Allowed, list.Verdict);
        Assert.Equal(AuthorizationVerdict.Allowed, ordinary.Verdict);
        Assert.Equal(AuthorizationVerdict.Allowed, bootstrap.Verdict);
    }

    [Theory(DisplayName = "Matrix: every other platform operation refuses the Auditor")]
    [MemberData(nameof(EveryOtherOperation))]
    public async Task Every_other_platform_operation_refuses_the_auditor(string operationValue)
    {
        await using var h = await Harness.CreateAsync();
        await h.GrantAuditorAsync();
        var operation = AuthorizationOperation.Parse(operationValue);

        var recordScoped = await h.DecideAsync(operationValue, "record-1");
        var installWide = PermissionVocabulary.IsInstallWide(operation)
            ? await h.DecideInstallWideAsync(operationValue)
            : null;

        Assert.NotEqual(AuthorizationVerdict.Allowed, recordScoped.Verdict);
        if (installWide is not null)
        {
            Assert.NotEqual(AuthorizationVerdict.Allowed, installWide.Verdict);
        }
    }

    [Fact(DisplayName = "Widening: a pack cannot offer the Auditor a second capability")]
    public async Task A_pack_definition_cannot_offer_the_auditor()
    {
        await using var h = await Harness.CreateAsync();

        var packWidening = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("d1d1d1d1-0000-0000-0000-000000000001")),
            "harborline.some-pack", 1,
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead),
            PermissionAtom.Parse($"{TeamRolePermissions.RecordsRead}@/"),
            RoleBindingSet.Of(RoleReference.Auditor));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Writer.WriteAsync(new InstallAuthorizationDefinition(packWidening)).AsTask());
    }

    [Fact(DisplayName = "Widening: even an audit:read definition from another publisher cannot offer the Auditor")]
    public async Task Only_the_platform_audit_read_definition_may_offer_the_auditor()
    {
        await using var h = await Harness.CreateAsync();

        var impostor = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("d1d1d1d1-0000-0000-0000-000000000002")),
            "harborline.some-pack", 1,
            AuthorizationOperation.Parse(Permission.AuditRead),
            PermissionAtom.Parse($"{Permission.AuditRead}@/"),
            RoleBindingSet.Of(RoleReference.Auditor));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Writer.WriteAsync(new InstallAuthorizationDefinition(impostor)).AsTask());
    }

    [Fact(DisplayName = "Widening: a tenant binding revision narrows the Auditor away and cannot add it back")]
    public async Task A_tenant_binding_cannot_widen_the_auditor()
    {
        await using var h = await Harness.CreateAsync();
        await h.GrantAuditorAsync();
        var rows = await h.Catalogue.ListAsync(Tenant);
        var auditRead = Assert.Single(
            rows, row => row.Definition.Operation.Value == Permission.AuditRead);
        var recordsRead = Assert.Single(
            rows, row => row.Definition.Operation.Value == TeamRolePermissions.RecordsRead);

        // Selecting the Auditor for a definition that does not OFFER it is refused outright: the binding is
        // the tenant's selection, the definition's offer is the publisher's ceiling.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Writer.WriteAsync(new NarrowCapabilityRoleBinding(
                Tenant, recordsRead.Definition.DefinitionId, RoleBindingSet.Of(RoleReference.Auditor),
                new ActorId("tenant-admin"), At, new BindingChangeReason("widen-attempt"))).AsTask());
        var afterWidening = await h.DecideAsync(TeamRolePermissions.RecordsRead, "record-1");

        // And narrowing the one definition that DOES offer it takes the capability away.
        await h.Writer.WriteAsync(new NarrowCapabilityRoleBinding(
            Tenant, auditRead.Definition.DefinitionId, RoleBindingSet.Empty,
            new ActorId("tenant-admin"), At, new BindingChangeReason("policy")));
        var afterNarrowing = await h.DecideInstallWideAsync(Permission.AuditRead);

        Assert.NotEqual(AuthorizationVerdict.Allowed, afterWidening.Verdict);
        Assert.NotEqual(AuthorizationVerdict.Allowed, afterNarrowing.Verdict);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Harness(ServiceProvider provider)
        {
            _provider = provider;
            Writer = provider.GetRequiredService<AuthorizationDefinitionWriter>();
            Catalogue = provider.GetRequiredService<IAuthorizationDefinitionCatalogueReader>();
            // The REAL gate over the definition-joined closure. It is constructed here rather than resolved
            // because the writer needs an allow-all gate for its own authority, and that registration wins
            // the container's AuthorizationGate slot; asking THAT one would allow everything.
            Gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>());
            Grants = provider.GetRequiredService<IGrantStore>();
        }

        public AuthorizationDefinitionWriter Writer { get; }
        public IAuthorizationDefinitionCatalogueReader Catalogue { get; }
        public AuthorizationGate Gate { get; }
        public IGrantStore Grants { get; }

        public static async Task<Harness> CreateAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            var harness = new Harness(provider);
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(Tenant, At, AuthorizationSeedProfile.Production);
            return harness;
        }

        public Task GrantAuditorAsync() => Grants.AppendAsync(Tenant, new AccessGrant(
            GrantId.New(), Tenant, TheAuditor, RoleReference.Auditor, ScopeExpression.Parse("/"),
            GrantResidency.Cache, new GrantValidity(At.AddHours(-1)), GranterKind.Person,
            new ActorId("tenant-admin"), At.AddHours(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                new ActorId("tenant-admin")), At.AddHours(-1)), "auditor-grant");

        public ValueTask<AuthorizationDecision> DecideAsync(string operationValue, string recordId)
        {
            var operation = AuthorizationOperation.Parse(operationValue);
            return Gate.DecideAsync(new AuthorizationWriteContext(TheAuditor, Tenant, At)
                .Request(operation, AuthorizationGate.RecordKindFor(operation), recordId));
        }

        public ValueTask<AuthorizationDecision> DecideInstallWideAsync(string operationValue) =>
            Gate.DecideAsync(new AuthorizationWriteContext(TheAuditor, Tenant, At)
                .InstallWide(AuthorizationOperation.Parse(operationValue)));

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
