using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ViewDefinitions;

public sealed class ViewDefinitionShapeRoleTests
{
    [Fact]
    public async Task ShapeRoleMapping_RoundTripStaysInsideItsView()
    {
        var mapped = Definition(
            "schedule",
            new ShapeRoleMapping(Title: "caption", PlacedBy: "starts_at", GroupedBy: "crew"));
        var unmapped = Definition("plain-list");
        var json = JsonSerializer.Serialize(new[] { mapped, unmapped });

        var roundTripped = JsonSerializer.Deserialize<ViewDefinition[]>(json)!;

        Assert.Equal(mapped.ShapeRoles, roundTripped[0].ShapeRoles);
        Assert.Null(roundTripped[1].ShapeRoles);
        await RegisterBothAsync(roundTripped);
    }

    [Fact]
    public async Task TwoViewsOverOneRecordType_MapTheSameShapeRoleToDifferentFields()
    {
        var registry = Registry();
        var bySubject = Definition("by-subject", new ShapeRoleMapping(Title: "subject"));
        var byReference = Definition("by-reference", new ShapeRoleMapping(Title: "reference"));

        await registry.RegisterAsync(bySubject);
        await registry.RegisterAsync(byReference);

        var storedSubject = await registry.GetDefinitionAsync("tenant-a", "by-subject", "1.0.0");
        var storedReference = await registry.GetDefinitionAsync("tenant-a", "by-reference", "1.0.0");
        Assert.Equal("work-item", storedSubject!.Parameters.GetProperty("entityType").GetString());
        Assert.Equal("work-item", storedReference!.Parameters.GetProperty("entityType").GetString());
        Assert.Equal("subject", storedSubject.ShapeRoles!.Title);
        Assert.Equal("reference", storedReference.ShapeRoles!.Title);
    }

    [Fact]
    public async Task AdmissionRejectsMissingShapeRoleFieldWithStableCode()
    {
        var definition = Definition("missing", new ShapeRoleMapping(Title: "not_a_field"));

        var exception = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(
            () => Registry().RegisterAsync(definition).AsTask());

        Assert.Equal("view_definition.shape_role_field_missing", exception.ErrorCode);
    }

    [Theory]
    [InlineData("amount", null, null)]
    [InlineData(null, "subject", null)]
    [InlineData(null, null, "details")]
    public async Task AdmissionRejectsIncompatibleShapeRoleFieldWithStableCode(
        string? title,
        string? placedBy,
        string? groupedBy)
    {
        var definition = Definition(
            "incompatible",
            new ShapeRoleMapping(Title: title, PlacedBy: placedBy, GroupedBy: groupedBy));

        var exception = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(
            () => Registry().RegisterAsync(definition).AsTask());

        Assert.Equal("view_definition.shape_role_field_incompatible", exception.ErrorCode);
    }

    [Fact]
    public async Task AdmissionAcceptsTextOrderedAndScalarMappings()
    {
        var definition = Definition(
            "compatible",
            new ShapeRoleMapping(Title: "subject", PlacedBy: "rank", GroupedBy: "active"));

        var stored = await Registry().RegisterAsync(definition);

        Assert.Equal(definition.ShapeRoles, stored.ShapeRoles);
    }

    [Fact]
    public async Task HostAdmissionProjectsAllShapeRoleCompatibilitiesFromTheRecordJsonSchema()
    {
        var schemas = new InMemorySchemaRegistry(TimeProvider.System);
        using var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var types = new InMemoryEntityTypeRegistry(new InMemoryRegistryAuditLog());
        var tenant = new TenantId("aaaaaaaa-0000-0000-0000-000000000215");
        var schema = await schemas.RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "display_name": { "type": "string" },
                "scheduled_at": { "type": "string", "format": "date-time" },
                "sequence": { "type": "integer" },
                "active": { "type": "boolean" },
                "details": { "type": "object" }
              }
            }
            """);
        var form = new FormDefinition(
            Id: new FormDefinitionId("work-item-fields"),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Published,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schema.Id,
            Overlay: HarborlineOverlay.Empty,
            Lineage: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
        await forms.RegisterAsync(form);
        types.SeedType(new EntityTypeSeed(
            new EntityTypeId("work-item"),
            new EntityTypeDescriptor(
                "Work item",
                EntityTrait.Maintainable,
                PropertyFormBinding: new FormBindingRef(form.Id, form.Version)),
            CascadeLayer.Pack));
        var registry = new InMemoryViewDefinitionRegistry(
            new HostViewKindDescriptorRegistry(types, forms, schemas));
        var definition = Definition(
            "schema-backed",
            new ShapeRoleMapping(
                Title: "display_name",
                PlacedBy: "scheduled_at",
                GroupedBy: "active")) with
        {
            Tenant = tenant.Value,
        };

        var stored = await registry.RegisterAsync(definition);

        Assert.Equal(definition.ShapeRoles, stored.ShapeRoles);
    }

    private static async Task RegisterBothAsync(IEnumerable<ViewDefinition> definitions)
    {
        var registry = Registry();
        foreach (var definition in definitions)
        {
            await registry.RegisterAsync(definition);
        }
    }

    private static InMemoryViewDefinitionRegistry Registry() =>
        new(new RecordDescriptorRegistry());

    private static ViewDefinition Definition(string key, ShapeRoleMapping? shapeRoles = null) => new()
    {
        Tenant = "tenant-a",
        Key = key,
        Version = "1.0.0",
        SchemaVersion = 1,
        ViewKind = "views.entity-list/grid",
        Title = "Work items",
        Parameters = JsonSerializer.SerializeToElement(new { entityType = "work-item" }),
        ShapeRoles = shapeRoles,
    };

    private sealed class RecordDescriptorRegistry : IViewDefinitionDescriptorRegistry
    {
        private static readonly ViewRecordTypeDescriptor RecordType = new(
            "work-item",
            new Dictionary<string, ViewRecordFieldKind>(StringComparer.Ordinal)
            {
                ["subject"] = ViewRecordFieldKind.Text,
                ["reference"] = ViewRecordFieldKind.Text,
                ["caption"] = ViewRecordFieldKind.Text,
                ["starts_at"] = ViewRecordFieldKind.DateTime,
                ["rank"] = ViewRecordFieldKind.Ordered,
                ["crew"] = ViewRecordFieldKind.Scalar,
                ["active"] = ViewRecordFieldKind.Scalar,
                ["amount"] = ViewRecordFieldKind.Ordered,
                ["details"] = ViewRecordFieldKind.Complex,
            });

        public ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<ViewRecordTypeDescriptor?> DescribeRecordTypeAsync(
            ViewDefinition definition,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ViewRecordTypeDescriptor?>(RecordType);
    }
}
