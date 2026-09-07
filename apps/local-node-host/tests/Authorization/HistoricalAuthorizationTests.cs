using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class HistoricalAuthorizationTests
{
    private static readonly TenantId Tenant = new("tenant-history");
    private static readonly ActorId Principal = new("approver-a");
    private static readonly ActorId Administrator = new("administrator-a");
    // Ticket 217: the sealed platform Auditor is offered only by the platform's audit:read definition, so a
    // test about historical resolution over a records:read definition holds a package role of the publisher's
    // own instead. Administrator would do as an offer but not as a GRANT -- revoking it here would be
    // revoking the tenant's last administrator.
    private static readonly RoleReference Role = new(RoleVocabularies.Domain, "records-reader");
    private static InMemoryRoleVocabulary Vocabulary() => new(
    [
        RoleDefinition.CreatePackageRole(
            new RoleDefinitionId(new Guid("20600000-0000-0000-0000-000000000003")),
            Role.Name, "Records reader", "harborline.platform"),
    ]);
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    [Fact]
    public async Task HistoricalDefinitionAndBindingRevisions_ResolveAtActTimeAfterLaterNarrowing()
    {
        var clock = new MutableTimeProvider(Origin);
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var writer = Writer(configuration);
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var resolver = new HistoricalAuthorizationResolver(grants, configuration);
        var definition = Definition("records:read", "/");

        clock.Advance(TimeSpan.FromHours(1));
        await WriteAtAsync(writer, new InstallAuthorizationDefinition(definition), clock);
        await grants.AppendAsync(Tenant, Grant(validFrom: Origin, validTo: null));

        var beforeNarrowing = Origin.AddHours(2);
        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, beforeNarrowing)).Covers(
            PermissionAtom.Parse("records:read@/records/a")));

        clock.Advance(TimeSpan.FromHours(2));
        await WriteAtAsync(writer, new ReplaceAuthorizationDefinition(
            definition with { Revision = 2, Atom = PermissionAtom.Parse("records:read@/records") }), clock);

        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, beforeNarrowing)).Covers(
            PermissionAtom.Parse("records:read@/records/a")));
        Assert.False((await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(4))).Covers(
            PermissionAtom.Parse("records:read@/other")));

        clock.Advance(TimeSpan.FromHours(2));
        await WriteAtAsync(writer, new NarrowCapabilityRoleBinding(
            Tenant, definition.DefinitionId, RoleBindingSet.Empty, Administrator, clock.GetUtcNow(),
            new BindingChangeReason("tenant-policy")), clock);

        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(4))).Covers(
            PermissionAtom.Parse("records:read@/records/a")));
        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(6)));
    }

    [Fact]
    public async Task HistoricalGrantValidityAndRevocation_PreservePastVerdicts()
    {
        var clock = new MutableTimeProvider(Origin);
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var writer = Writer(configuration);
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var resolver = new HistoricalAuthorizationResolver(grants, configuration);

        await WriteAtAsync(writer, new InstallAuthorizationDefinition(Definition("records:read", "/")), clock);
        var grant = Grant(Origin.AddHours(2), Origin.AddHours(8));
        await grants.AppendAsync(Tenant, grant);

        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(1)));
        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(3))).Covers(
            PermissionAtom.Parse("records:read@/records/a")));
        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(9)));

        clock.Advance(TimeSpan.FromHours(6));
        await grants.RevokeAsync(Tenant, grant.GrantId, new GrantRevocation(
            Administrator, clock.GetUtcNow(), new GrantReason(GrantReasonCodes.RevocationOffboarding)));

        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(3))).Covers(
            PermissionAtom.Parse("records:read@/records/a")));
        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(7)));
    }

    [Fact]
    public async Task ApprovalAuthority_UsesRecordedInstantAfterLaterRevocation()
    {
        var clock = new MutableTimeProvider(Origin);
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var writer = Writer(configuration);
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var resolver = new HistoricalAuthorizationResolver(grants, configuration);
        await WriteAtAsync(writer, new InstallAuthorizationDefinition(Definition("records:read", "/")), clock);
        var grant = Grant(Origin.AddHours(2), Origin.AddHours(8));
        await grants.AppendAsync(Tenant, grant);
        var approvalRecordedAt = Origin.AddHours(3);

        clock.Advance(TimeSpan.FromHours(6));
        await grants.RevokeAsync(Tenant, grant.GrantId, new GrantRevocation(
            Administrator, clock.GetUtcNow(), new GrantReason(GrantReasonCodes.RevocationOffboarding)));

        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, approvalRecordedAt)).Covers(
            PermissionAtom.Parse("records:read@/records/a")));
        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(1)));
        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(9)));
    }

    [Fact]
    public async Task AccessGrantModule_RegistersHistoricalResolver()
    {
        var services = new ServiceCollection().AddAccessGrantModule();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<HistoricalAuthorizationResolver>(
            provider.GetRequiredService<IHistoricalAuthorizationResolver>());
    }

    [Fact]
    public async Task EfHistoricalResolution_RoundTripsDefinitionInstantAndLaterRevocation()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var clock = new MutableTimeProvider(Origin);
        var configuration = new NodeEfAuthorizationConfigurationStore(
            store.Factory,
            Vocabulary());
        var grants = new NodeEfGrantStore(store.Factory);
        var writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(Vocabulary()),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            grants);
        var resolver = new HistoricalAuthorizationResolver(grants, configuration);

        clock.Advance(TimeSpan.FromHours(1));
        await WriteAtAsync(writer, new InstallAuthorizationDefinition(Definition("records:read", "/")), clock);
        var grant = Grant(Origin.AddHours(2), Origin.AddHours(8));
        await grants.AppendAsync(Tenant, grant);
        clock.Advance(TimeSpan.FromHours(5));
        await grants.RevokeAsync(Tenant, grant.GrantId, new GrantRevocation(
            Administrator, clock.GetUtcNow(), new GrantReason(GrantReasonCodes.RevocationOffboarding)));

        Assert.True((await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(3))).Covers(
            PermissionAtom.Parse("records:read@/records/a")));
        Assert.Equal(PermissionAtomSet.Empty,
            await resolver.AuthorityAtAsync(Tenant, Principal, Origin.AddHours(7)));
        await using var context = store.CreateContext();
        Assert.Equal(
            Origin.AddHours(1).ToUnixTimeMilliseconds(),
            Assert.Single(context.AuthorizationDefinitions).EffectiveAtUnixMs);
    }

    private static AuthorizationDefinitionWriter Writer(
        InMemoryAuthorizationConfigurationStore store) => new(
            store,
            store,
            new AuthorizationDefinitionAdmission(Vocabulary()),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            TestInMemoryAuthorizationStores.GrantStore());

    private static ValueTask<AuthorizationConfigurationWriteResult> WriteAtAsync(
        AuthorizationDefinitionWriter writer,
        AuthorizationConfigurationCommand command,
        TimeProvider clock) => writer.WriteAsync(
        command,
        new AuthorizationWriteContext(
            new ActorId("test:authorization-writer"),
            command is NarrowCapabilityRoleBinding narrow ? narrow.TenantId : new TenantId("test"),
            clock.GetUtcNow()));

    private static AuthorizationCapabilityDefinition Definition(string operation, string scope) => new(
        new AuthorizationCapabilityDefinitionId(new Guid("20600000-0000-0000-0000-000000000001")),
        "harborline.platform",
        1,
        AuthorizationOperation.Parse(operation),
        PermissionAtom.Parse($"{operation}@{scope}"),
        RoleBindingSet.Of(Role));

    private static AccessGrant Grant(DateTimeOffset validFrom, DateTimeOffset? validTo) => new(
        new GrantId(new Guid("20600000-0000-0000-0000-000000000002")),
        Tenant,
        Principal,
        Role,
        ScopeExpression.Parse("/"),
        GrantResidency.Cache,
        new GrantValidity(validFrom, validTo),
        GranterKind.Person,
        Administrator,
        validFrom,
        new GrantProvenance(
            GrantSourceKind.Workflow,
            new GrantReason(GrantReasonCodes.Workflow, "approval-206"),
            Administrator),
        validFrom);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
