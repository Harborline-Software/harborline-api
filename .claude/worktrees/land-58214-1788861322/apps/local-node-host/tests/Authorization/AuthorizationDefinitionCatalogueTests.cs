using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationDefinitionCatalogueTests
{
    [Fact]
    public async Task Catalogue_RetainsExplicitlyEmptiedDefinition()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestAuthorization.AllowGate());
        services.AddAccessGrantModule();
        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<AuthorizationDefinitionWriter>();
        var catalogue = provider.GetRequiredService<IAuthorizationDefinitionCatalogueReader>();
        var tenant = new TenantId("tenant-a");
        var definition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            "platform.core",
            1,
            AuthorizationOperation.Parse("org:manage-settings"),
            PermissionAtom.Parse("org:manage-settings@/"),
            RoleBindingSet.Of(RoleReference.Administrator));
        await writer.WriteAsync(new InstallAuthorizationDefinition(definition));
        await writer.WriteAsync(new NarrowCapabilityRoleBinding(
            tenant, definition.DefinitionId, RoleBindingSet.Empty, new ActorId("actor-a"),
            DateTimeOffset.Parse("2026-09-02T12:00:00Z"), new BindingChangeReason("policy")));

        var rows = await catalogue.ListAsync(tenant);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.BindingRevision);
        Assert.Equal(BindingWarningCode.EmptyBinding, row.Warning);
        Assert.Empty(row.EffectiveRoles.Roles);
    }
}
