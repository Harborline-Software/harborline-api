using System.Reflection;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class RoleDefinitionTests
{
    [Fact]
    public void RoleDefinition_IsAPowerlessVocabularyEntry()
    {
        var role = RoleDefinition.CreateTenantRole(
            RoleDefinitionId.New(),
            "reviewer",
            "Reviewer",
            new TenantId("tenant-a"));

        Assert.Equal(new RoleReference(RoleVocabularies.Domain, "reviewer"), role.Role);
        Assert.Equal("Reviewer", role.DisplayName);
        Assert.Equal(new RoleOwner(RoleOwnerKind.Tenant, "tenant-a"), role.Owner);
        Assert.False(role.IsSealed);
    }

    [Fact]
    public async Task RoleVocabularyReader_ResolvesConstructorSeededDefinitions()
    {
        var domainRole = RoleDefinition.CreatePackageRole(
            RoleDefinitionId.New(),
            "member",
            "Member",
            "com.harborline.tax");
        IRoleVocabularyReader vocabulary = new InMemoryRoleVocabulary([domainRole]);

        Assert.Equal(domainRole, await vocabulary.ResolveAsync(domainRole.Role));
        Assert.Contains(await vocabulary.ListAsync(), candidate => candidate == domainRole);
    }

    [Fact]
    public void RoleVocabularyReader_ExposesNoPublicMintingWriteMethod()
    {
        var publicMethods = typeof(InMemoryRoleVocabulary).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(publicMethods, method =>
            method.Name.Contains("Save", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Add", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Create", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase));
    }
}
