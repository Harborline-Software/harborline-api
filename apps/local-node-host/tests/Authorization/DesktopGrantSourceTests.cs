using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class DesktopGrantSourceTests
{
    [Fact]
    public async Task Admin_gate_reads_durable_grants_without_entering_context_HasPermission()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestKernelClock();
        services.AddSingleton(store.Factory);
        services.AddSingleton<IAuthorizationContext>(new ThrowingContext());
        AuthorizationAdminRouteTests.RegisterGate(services);
        await using var provider = services.BuildServiceProvider();
        var tenant = new TenantId("desktop-grant-source");
        var now = TimeProvider.System.GetUtcNow();
        await SeedAsync(provider, tenant, now);
        var request = new AuthorizationWriteContext(new ActorId("local"), tenant, now)
            .Request(AuthorizationOperation.Parse(Permission.OrgManageSettings), "org", "desktop");
        var decision = await provider.GetRequiredService<AuthorizationGate>().DecideAsync(request);
        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Contains(decision.Evidence.Project()[1].Facts,
            fact => fact.Contains("grant:", StringComparison.Ordinal));
        var grant = await provider.GetRequiredService<IGrantStore>().FindBySourceReferenceAsync(
            tenant, "desktop-fixture");
        Assert.NotNull(grant);
        await provider.GetRequiredService<IGrantStore>().RevokeAsync(tenant, grant.GrantId,
            new GrantRevocation(new ActorId("local"), now,
                new GrantReason(GrantReasonCodes.RevocationReview, "regression revocation")));
        var refused = await provider.GetRequiredService<AuthorizationGate>().DecideAsync(request);
        Assert.Equal(AuthorizationVerdict.Denied, refused.Verdict);
    }

    internal static async Task SeedAsync(IServiceProvider provider, TenantId tenant, DateTimeOffset now,
        string permission = Permission.OrgManageSettings)
    {
        var configuration = provider.GetRequiredService<NodeEfAuthorizationConfigurationStore>();
        var grants = provider.GetRequiredService<IGrantStore>();
        var writer = new AuthorizationDefinitionWriter(configuration, configuration,
            new AuthorizationDefinitionAdmission(provider.GetRequiredService<IRoleVocabularyReader>()),
            new AuthorizationCapabilityBindingAdmission(), TestAuthorization.AllowGate(), grants);
        var operation = AuthorizationOperation.Parse(permission);
        await writer.WriteAsync(new InstallAuthorizationDefinition(new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.NewGuid()), AccessGrantAuthorizationSeed.PackageId, 1, operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/")), RoleBindingSet.Of(RoleReference.Administrator, AccessGrantAuthorizationSeed.NodeOperatorRole))));
        var actor = new ActorId("local");
        await grants.AppendAsync(tenant, new AccessGrant(GrantId.New(), tenant, actor,
            AccessGrantAuthorizationSeed.NodeOperatorRole, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(now.AddMinutes(-1)), GranterKind.Person, actor, now.AddMinutes(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), actor),
            now.AddMinutes(-1)), "desktop-fixture");
    }

    private sealed class ThrowingContext : IAuthorizationContext
    {
        public bool HasPermission(string permission) =>
            throw new InvalidOperationException("Grant derivation re-entered context.HasPermission");
    }
}
