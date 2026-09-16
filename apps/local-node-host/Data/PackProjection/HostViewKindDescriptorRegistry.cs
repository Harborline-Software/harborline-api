using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Health;


namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Adapts the host's registered view surfaces to view-definition descriptor admission. This slice
/// admits <see cref="EntityListGridKind"/>, the entity list served by
/// <c>GET /asset-registry/entities?type=</c> and rendered by the Harborline grid. Typed per-kind
/// parameter binding is a recorded deepening follow-up. A <c>views.dashboard/helm</c> kind admitting
/// registered Helm widget ids is likewise deferred until the host composes
/// <c>IHelmWidgetRegistry</c> (ADR 0066); neither follow-up is admitted in this slice.
/// </summary>
public sealed class HostViewKindDescriptorRegistry : IViewDefinitionDescriptorRegistry
{
    /// <summary>The saved entity-list grid kind this host serves today.</summary>
    public const string EntityListGridKind = "views.entity-list/grid";

    /// <summary>The compiled Access holder entity backed by the ordinary <c>IGrantStore</c> read.</summary>
    public const string AccessGrantEntityType = "AccessGrant";

    private readonly IEntityTypeRegistry _types;
    private readonly IFormDefinitionStore _forms;
    private readonly ISchemaRegistry _schemas;

    /// <summary>Initializes descriptor admission over the host's entity-type registry.</summary>
    /// <param name="types">The host's registered entity types.</param>
    /// <param name="forms">The form definitions that bind record types to schemas.</param>
    /// <param name="schemas">The kernel registry that owns record-field schema bodies.</param>
    public HostViewKindDescriptorRegistry(
        IEntityTypeRegistry types,
        IFormDefinitionStore forms,
        ISchemaRegistry schemas)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _forms = forms ?? throw new ArgumentNullException(nameof(forms));
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
    }

    /// <inheritdoc />
    public async ValueTask AdmitAsync(
        ViewDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        if (!StringComparer.Ordinal.Equals(definition.ViewKind, EntityListGridKind))
        {
            throw new ViewDefinitionGovernanceException("view_definition.kind_unknown");
        }

        if (definition.Parameters.ValueKind != JsonValueKind.Object)
        {
            throw new ViewDefinitionGovernanceException("view_definition.parameters_not_object");
        }

        if (!definition.Parameters.TryGetProperty("entityType", out var entityType)
            || entityType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(entityType.GetString()))
        {
            throw new ViewDefinitionGovernanceException("view_definition.entity_type_unknown");
        }

        var id = new EntityTypeId(entityType.GetString()!);
        var target = await _types.GetTypeAsync(
                new TenantId(definition.Tenant), id, cancellationToken)
            .ConfigureAwait(false);
        if (target is null && _types.GetSeed(id) is null
            && !StringComparer.Ordinal.Equals(id.Value, AccessGrantEntityType)
            && !SystemRecordType.All.Any(type => StringComparer.Ordinal.Equals(type.Name, id.Value)))
        {
            throw new ViewDefinitionGovernanceException("view_definition.entity_type_unknown");
        }

        await AdmitRequestsAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AdmitRequestsAsync(ViewDefinition definition, CancellationToken cancellationToken)
    {
        var hasDataSource = definition.Parameters.TryGetProperty("dataSource", out var dataSource);
        var hasActions = definition.Parameters.TryGetProperty("actions", out var actions);
        if (!hasDataSource && (!hasActions || actions.ValueKind == JsonValueKind.Array
            && !actions.EnumerateArray().Any(action => action.ValueKind == JsonValueKind.Object && action.TryGetProperty("dispatch", out _))))
            return;
        var row = await DescribeRecordTypeAsync(definition, cancellationToken).ConfigureAwait(false);
        var selection = row?.Fields.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            ViewRecordFieldKind.Text or ViewRecordFieldKind.DateTime => ViewRequestValueKind.Text,
            ViewRecordFieldKind.Ordered => ViewRequestValueKind.Number,
            ViewRecordFieldKind.Scalar => ViewRequestValueKind.Boolean,
            _ => ViewRequestValueKind.Object,
        }, StringComparer.Ordinal) ?? [];
        if (hasDataSource)
            HostViewRequestDescriptors.Admit(dataSource, new(new Dictionary<string, ViewRequestValueKind>(), new Dictionary<string, ViewRequestValueKind>()));
        if (!hasActions) return;
        if (actions.ValueKind != JsonValueKind.Array)
            throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
                throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
            if (!action.TryGetProperty("dispatch", out var dispatch)) continue;
            var fields = new Dictionary<string, ViewRequestValueKind>(StringComparer.Ordinal);
            var hasInput = action.TryGetProperty("input", out var input);
            if (hasInput)
            {
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("fieldsMeta", out var metadata)
                    || metadata.ValueKind != JsonValueKind.Object)
                    throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
                foreach (var field in metadata.EnumerateObject())
                    fields.Add(field.Name, InputKind(field.Value));
            }
            if (action.TryGetProperty("inputForm", out var form))
            {
                if (hasInput || form.ValueKind != JsonValueKind.Object
                    || !form.TryGetProperty("formId", out var formId) || formId.ValueKind != JsonValueKind.String
                    || !form.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                    throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
                var stored = await _forms.GetAsync(new DefinitionCoordinates(new TenantId(definition.Tenant),
                    formId.GetString()!, version.GetString()!), cancellationToken).ConfigureAwait(false);
                var schema = await _schemas.GetAsync(stored.SchemaRef, cancellationToken).ConfigureAwait(false)
                    ?? throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
                foreach (var field in DescribeFields(schema.JsonSchemaText))
                    fields.Add(field.Key, field.Value switch
                    {
                        ViewRecordFieldKind.Text or ViewRecordFieldKind.DateTime => ViewRequestValueKind.Text,
                        ViewRecordFieldKind.Ordered => ViewRequestValueKind.Number,
                        ViewRecordFieldKind.Scalar => ViewRequestValueKind.Boolean,
                        _ => ViewRequestValueKind.Object,
                    });
                hasInput = true;
            }
            HostViewRequestDescriptors.Admit(dispatch, new(selection, fields, HasInputObject: hasInput));
        }
    }

    private static ViewRequestValueKind InputKind(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
        return type.GetString() switch
        {
            "text" or "select" or "date" or "email" or "phone" or "url" or "textarea" => ViewRequestValueKind.Text,
            "number" or "currency" => ViewRequestValueKind.Number,
            "checkbox" => ViewRequestValueKind.Boolean,
            _ => throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid"),
        };
    }

    /// <inheritdoc />
    public async ValueTask<ViewRecordTypeDescriptor?> DescribeRecordTypeAsync(
        ViewDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadEntityType(definition, out var id))
        {
            return null;
        }

        if (StringComparer.Ordinal.Equals(id.Value, AccessGrantEntityType))
        {
            return new ViewRecordTypeDescriptor(id.Value, new Dictionary<string, ViewRecordFieldKind>(StringComparer.Ordinal)
            {
                ["grantId"] = ViewRecordFieldKind.Text,
                ["principalId"] = ViewRecordFieldKind.Text,
                ["role"] = ViewRecordFieldKind.Text,
                ["scope"] = ViewRecordFieldKind.Text,
                ["status"] = ViewRecordFieldKind.Text,
            });
        }

        var tenant = new TenantId(definition.Tenant);
        var binding = await _types.TryResolvePropertyFormAsync(tenant, id, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null)
        {
            return new ViewRecordTypeDescriptor(id.Value, EmptyFields());
        }

        try
        {
            var form = await _forms.GetAsync(
                    new DefinitionCoordinates(tenant, binding.Definition.Value, binding.PinnedVersion.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);
            var schema = await _schemas.GetAsync(form.SchemaRef, cancellationToken).ConfigureAwait(false);
            return new ViewRecordTypeDescriptor(
                id.Value,
                schema is null ? EmptyFields() : DescribeFields(schema.JsonSchemaText));
        }
        catch (FormDefinitionNotFoundException)
        {
            return new ViewRecordTypeDescriptor(id.Value, EmptyFields());
        }
    }

    private static bool TryReadEntityType(ViewDefinition definition, out EntityTypeId id)
    {
        id = default;
        if (definition.Parameters.ValueKind != JsonValueKind.Object
            || !definition.Parameters.TryGetProperty("entityType", out var entityType)
            || entityType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(entityType.GetString()))
        {
            return false;
        }

        id = new EntityTypeId(entityType.GetString()!);
        return true;
    }

    private static IReadOnlyDictionary<string, ViewRecordFieldKind> DescribeFields(string jsonSchemaText)
    {
        using var document = JsonDocument.Parse(jsonSchemaText);
        if (!document.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return EmptyFields();
        }

        var fields = new Dictionary<string, ViewRecordFieldKind>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            fields[property.Name] = DescribeField(property.Value);
        }
        return fields;
    }

    private static ViewRecordFieldKind DescribeField(JsonElement field)
    {
        if (!field.TryGetProperty("type", out var type))
        {
            return ViewRecordFieldKind.Complex;
        }

        var types = type.ValueKind switch
        {
            JsonValueKind.String => new[] { type.GetString()! },
            JsonValueKind.Array => type.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(value => !StringComparer.Ordinal.Equals(value, "null"))
                .ToArray(),
            _ => [],
        };
        if (types.Length != 1)
        {
            return ViewRecordFieldKind.Complex;
        }

        return types[0] switch
        {
            "string" when IsTemporalFormat(field) => ViewRecordFieldKind.DateTime,
            "string" => ViewRecordFieldKind.Text,
            "integer" or "number" => ViewRecordFieldKind.Ordered,
            "boolean" or "null" => ViewRecordFieldKind.Scalar,
            _ => ViewRecordFieldKind.Complex,
        };
    }

    private static bool IsTemporalFormat(JsonElement field) =>
        field.TryGetProperty("format", out var format)
        && format.ValueKind == JsonValueKind.String
        && format.GetString() is "date" or "time" or "date-time";

    private static IReadOnlyDictionary<string, ViewRecordFieldKind> EmptyFields() =>
        new Dictionary<string, ViewRecordFieldKind>(StringComparer.Ordinal);
}
