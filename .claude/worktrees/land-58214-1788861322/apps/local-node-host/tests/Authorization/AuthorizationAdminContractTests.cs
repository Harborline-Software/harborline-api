using System.Text.Json;

using Harborline.Api.Contracts;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationAdminContractTests
{
    [Fact]
    public void WireContracts_PreserveQualifiedRolesEmptyArraysAndCamelCase()
    {
        var dto = new AuthorizationDefinitionDto(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "platform.core",
            3,
            new PermissionAtomDto("org:manage-settings", "Tenant", "/"),
            [new RoleReferenceDto("sys.platform-roles", "administrator")],
            new AuthorizationBindingDto(4, [], "EmptyBinding"));

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        Assert.Equal("sys.platform-roles", document.RootElement
            .GetProperty("offeredRoles")[0].GetProperty("vocabulary").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("binding").GetProperty("effectiveRoles").GetArrayLength());
        Assert.Equal("EmptyBinding", document.RootElement.GetProperty("binding").GetProperty("warning").GetString());
        Assert.False(document.RootElement.TryGetProperty("DefinitionId", out _));
    }

    [Fact]
    public void WireContracts_KeepUnknownMachineValuesAsStrings()
    {
        const string json = """
            {"roleDefinitionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","role":{"vocabulary":"future.roles","name":"future"},"displayName":"Future","owner":{"kind":"FutureOwner","ownerId":"future"},"isSealed":false}
            """;

        var dto = JsonSerializer.Deserialize<RoleDefinitionDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("future.roles", dto!.Role.Vocabulary);
        Assert.Equal("FutureOwner", dto.Owner.Kind);
    }
}
