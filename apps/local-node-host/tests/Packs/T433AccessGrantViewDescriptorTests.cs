using System.Text.Json;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class T433AccessGrantViewDescriptorTests
{
    private readonly HostViewKindDescriptorRegistry _descriptors = new(
        new InMemoryEntityTypeRegistry(new InMemoryRegistryAuditLog()),
        Substitute.For<IFormDefinitionStore>(), Substitute.For<ISchemaRegistry>());

    [Fact]
    public async Task Compiled_AccessGrant_identity_is_admitted_with_the_holder_row_shape()
    {
        var definition = Definition(HostViewKindDescriptorRegistry.AccessGrantEntityType);

        await _descriptors.AdmitAsync(definition);
        var descriptor = await _descriptors.DescribeRecordTypeAsync(definition);

        Assert.NotNull(descriptor);
        Assert.Equal("AccessGrant", descriptor.RecordType);
        Assert.Equal(new[] { "principalId", "role", "scope", "status" }, descriptor.Fields.Keys);
        Assert.All(descriptor.Fields.Values, kind => Assert.Equal(ViewRecordFieldKind.Text, kind));
    }

    [Fact]
    public async Task Unknown_compiled_entity_identity_is_refused_fail_closed()
    {
        var exception = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(
            () => _descriptors.AdmitAsync(Definition("AccessGrantTypo")).AsTask());

        Assert.Equal("view_definition.entity_type_unknown", exception.ErrorCode);
    }

    private static ViewDefinition Definition(string entityType) => new()
    {
        Tenant = "43300000-0000-4000-8000-000000000000",
        Key = "access.holders",
        Version = "1.0.0",
        SchemaVersion = 1,
        ViewKind = HostViewKindDescriptorRegistry.EntityListGridKind,
        Title = "Access holders",
        Parameters = JsonSerializer.SerializeToElement(new { entityType }),
    };
}
