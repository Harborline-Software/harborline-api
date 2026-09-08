using System.Collections;
using System.Reflection;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class ScopedAuthorizationModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/north/../south")]
    [InlineData("/north//site")]
    public void ScopeExpression_RejectsRelativeParentAndEmptySegments(string value)
    {
        Assert.Throws<ArgumentException>(() => ScopeExpression.Parse(value));
    }

    [Fact]
    public void ScopeExpression_NormalizesEquivalentPrefixes()
    {
        Assert.Equal(ScopeExpression.Parse("/north"), ScopeExpression.Parse("/north/"));
        Assert.Equal("/", ScopeExpression.Parse("/").ToString());
    }

    [Fact]
    public void ScopeExpression_ContainsDescendantsButNotAncestors()
    {
        var north = ScopeExpression.Parse("/sites/north");
        var boiler = ScopeExpression.Parse("/sites/north/boiler");

        Assert.True(north.Contains(boiler));
        Assert.False(boiler.Contains(north));
    }

    [Fact]
    public void ScopeExpression_DoesNotContainSiblingScope()
    {
        Assert.False(
            ScopeExpression.Parse("/sites/north")
                .Contains(ScopeExpression.Parse("/sites/south")));
    }

    [Fact]
    public void ScopeExpression_IntersectionReturnsTheNarrowerComparablePrefix()
    {
        var north = ScopeExpression.Parse("/sites/north");
        var boiler = ScopeExpression.Parse("/sites/north/boiler");

        Assert.Equal(boiler, north.Intersect(boiler));
        Assert.Equal(boiler, boiler.Intersect(north));
        Assert.Null(north.Intersect(ScopeExpression.Parse("/sites/south")));
    }

    [Fact]
    public void PermissionAtom_RoundTripsResourceVerbAtScope()
    {
        const string text = "records:read@/sites/north";

        Assert.Equal(text, PermissionAtom.Parse(text).ToString());
    }

    [Fact]
    public void PermissionAtom_CoverageRequiresOperationAndScopeContainment()
    {
        var held = PermissionAtom.Parse("records:read@/sites/north");

        Assert.True(held.Covers(PermissionAtom.Parse("records:read@/sites/north/boiler")));
        Assert.False(held.Covers(PermissionAtom.Parse("records:read@/sites/south")));
        Assert.False(held.Covers(PermissionAtom.Parse("records:write@/sites/north")));
    }

    [Fact]
    public void PermissionAtomSet_UnionIsCommutativeAssociativeAndIdempotent()
    {
        var universe = new[]
        {
            PermissionAtom.Parse("records:read@/sites/north"),
            PermissionAtom.Parse("records:write@/sites/north"),
            PermissionAtom.Parse("records:read@/sites/south"),
        };
        var sets = Enumerable.Range(0, 1 << universe.Length)
            .Select(mask => PermissionAtomSet.From(
                universe.Where((_, index) => (mask & (1 << index)) != 0)))
            .ToArray();

        foreach (var left in sets)
        {
            Assert.Equal(left, left.Union(left));
            foreach (var right in sets)
            {
                Assert.Equal(left.Union(right), right.Union(left));
                foreach (var third in sets)
                {
                    Assert.Equal(
                        left.Union(right).Union(third),
                        left.Union(right.Union(third)));
                }
            }
        }
    }

    [Fact]
    public async Task PlatformRoleVocabulary_ContainsOnlyAdministratorAndAuditor()
    {
        IRoleVocabularyReader vocabulary = new InMemoryRoleVocabulary();

        var roles = await vocabulary.ListAsync();
        Assert.Equal(
            [RoleReference.Administrator, RoleReference.Auditor],
            roles.Select(role => role.Role).OrderBy(role => role.Name).ToArray());
        Assert.Equal(
            [RoleReference.Administrator, RoleReference.Auditor],
            PlatformRoleVocabulary.Roles.OrderBy(role => role.Name).ToArray());
    }

    [Fact]
    public async Task PlatformRoles_AreSealedAndPlatformOwned()
    {
        IRoleVocabularyReader vocabulary = new InMemoryRoleVocabulary();

        foreach (var role in await vocabulary.ListAsync())
        {
            Assert.True(role.IsSealed);
            Assert.Equal(RoleOwnerKind.Platform, role.Owner.Kind);
            Assert.Equal(RoleVocabularies.Platform, role.Role.Vocabulary);
        }

        Assert.Throws<ArgumentException>(() => new RoleDefinition(
            RoleDefinitionId.New(),
            RoleReference.Administrator,
            "Administrator",
            new RoleOwner(RoleOwnerKind.Tenant, "tenant-a"),
            IsSealed: false));
    }

    [Fact]
    public void DomainRole_UsesTaxRolesAndRetainsPackageOrTenantOwner()
    {
        var packageRole = RoleDefinition.CreatePackageRole(
            RoleDefinitionId.New(), "author", "Author", "com.harborline.tax");
        var tenantRole = RoleDefinition.CreateTenantRole(
            RoleDefinitionId.New(), "reviewer", "Reviewer", new TenantId("tenant-a"));

        Assert.Equal(RoleVocabularies.Domain, packageRole.Role.Vocabulary);
        Assert.Equal(new RoleOwner(RoleOwnerKind.Package, "com.harborline.tax"), packageRole.Owner);
        Assert.False(packageRole.IsSealed);
        Assert.Equal(RoleVocabularies.Domain, tenantRole.Role.Vocabulary);
        Assert.Equal(new RoleOwner(RoleOwnerKind.Tenant, "tenant-a"), tenantRole.Owner);
        Assert.False(tenantRole.IsSealed);
    }

    [Fact]
    public void RoleDefinition_HasNoPermissionBearingMember()
    {
        var members = typeof(RoleDefinition).GetMembers(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(members, member =>
        {
            var memberType = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => null,
            };
            if (memberType is null)
            {
                return false;
            }

            return memberType == typeof(PermissionSet)
                || memberType == typeof(PermissionAtomSet)
                || IsCollectionOf<PermissionAtom>(memberType)
                || member.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Wildcard", StringComparison.OrdinalIgnoreCase)
                || memberType != typeof(RoleDefinitionId)
                    && memberType.Name.Contains("Definition", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsCollectionOf<T>(Type type) =>
        type != typeof(string)
        && typeof(IEnumerable).IsAssignableFrom(type)
        && type.GetInterfaces()
            .Where(candidate => candidate.IsGenericType)
            .Any(candidate => candidate.GetGenericArguments().Contains(typeof(T)));
}
