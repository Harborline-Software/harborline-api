using System.Text;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Contracts.Fields;
using Harborline.Foundation.Definitions;
using Harborline.Kernel.SchemaValidation.Records;

using Harborline.Blocks.EntityViews;

namespace Harborline.Api.LocalNodeHost.Tests.Views;

/// <summary>
/// A shared definition store composed as a host composes it for Views: <see cref="ViewDefinitionCatalogue.Admission"/> and
/// <see cref="ViewDefinitionCatalogue.Dependencies(ViewAdmissionRegisters)"/> for <see cref="DefinitionKind.Views"/>, and a pass-through Records
/// registry holding a published <c>work-item</c> Record Type in each tenant the tests use. Each work-item field pins one of
/// the admitted test field kinds, as a Records field binds a kind revision (T-1177).
/// </summary>
internal static class ViewStores
{
    public const string KindVersion = "1.0.0";

    /// <summary>The host's admitted field-kind revisions: one per shape, and one per RFC 3339 §5.6 temporal format.</summary>
    public static readonly AdmittedFieldKind[] FieldKinds =
    [
        new("test.text", KindVersion, null),
        new("test.integer", KindVersion, null, FieldScalarValueShape.Integer),
        new("test.number", KindVersion, null, FieldScalarValueShape.Number),
        new("test.boolean", KindVersion, null, FieldScalarValueShape.Boolean),
        new("test.date", KindVersion, null, TemporalFormat: "date"),
        new("test.date-time", KindVersion, null, TemporalFormat: "date-time"),
        new("test.time", KindVersion, null, TemporalFormat: "time"),
    ];

    public static readonly (string Field, string? Kind)[] WorkItemFields =
    [
        ("title", "test.text"), ("state", "test.text"), ("assignee", "test.text"), ("priority", "test.integer"), ("tags", "test.text"),
    ];

    public static ViewAdmissionRegisters Registers(IReadOnlyList<ViewKindDescriptor>? kinds = null) => new(
        kinds ?? [new("layout.table", "hlp.ui.data-grid", [ViewShapeRole.Title])],
        [new("work.count", [])],
        new ViewExpressionFunctionRegistry(),
        [new("helm.counter", ["measure"])],
        [],
        [],
        FieldKinds,
        ViewsExpressionEnvironment.Admitted);

    public static (ViewDefinitionCatalogue Catalogue, IVersionedDefinitionStore Shared) Create(
        ViewAdmissionRegisters? registers = null,
        params string[] tenants)
    {
        registers ??= Registers();
        var shared = new InMemoryVersionedDefinitionStore(
            new Dictionary<DefinitionKind, DefinitionAdmission>
            {
                [DefinitionKind.Views] = ViewDefinitionCatalogue.Admission(registers),
                [DefinitionKind.Records] = (_, _) => [],
            },
            new Dictionary<DefinitionKind, DefinitionDependencyDerivation>
            {
                [DefinitionKind.Views] = ViewDefinitionCatalogue.Dependencies(registers),
            });
        foreach (var tenant in tenants.Length == 0 ? ["tenant-a"] : tenants)
        {
            PublishRecordType(shared, tenant, "work-item", WorkItemFields);
        }
        return (new ViewDefinitionCatalogue(shared), shared);
    }

    /// <summary>A Views catalogue over a freshly composed store, with <c>work-item</c> published in each tenant.</summary>
    public static ViewDefinitionCatalogue Catalogue(params string[] tenants) => Create(null, tenants).Catalogue;


    /// <summary>Publishes a Record Type whose fields pin the named kind at <see cref="KindVersion"/>, or no kind when null.</summary>
    public static void PublishRecordType(IVersionedDefinitionStore shared, string tenant, string recordTypeId,
        IEnumerable<(string Field, string? Kind)> fields) =>
        PublishRecordType(shared, tenant, recordTypeId, fields.Select(field => (field.Field, field.Kind, (string?)KindVersion)));

    /// <summary>
    /// Publishes a Record Type version whose fields pin an exact kind revision, or no kind when the kind is null. A later
    /// version passes the stream's current revision, so it becomes the new published head.
    /// </summary>
    public static void PublishRecordType(IVersionedDefinitionStore shared, string tenant, string recordTypeId,
        IEnumerable<(string Field, string? Kind, string? Version)> fields, string version = "1.0.0", long revision = 0)
    {
        var document = new RecordTypeDocument(
            new RecordsDefinitionEnvelope(tenant, "operations", new DefinitionContractVersion(1, 0)),
            recordTypeId,
            recordTypeId,
            fields.Select(field => new FieldDefinition(field.Field, field.Field, field.Kind is null
                ? null
                : new FieldBindingDefinition(
                    new FieldKindReference(field.Kind, field.Version!, new Dictionary<string, string>()),
                    new FieldConstraintDefinition(false, 0, 1, [], null)))).ToArray());
        var key = RecordTypeDefinitionStore.KeyOf(tenant, recordTypeId);
        var body = Encoding.UTF8.GetString(RecordTypeDefinitionJson.SerializeCanonical(document));
        shared.SaveDraftAsync(new DefinitionDocument(key, version, version, body), revision, $"seed-{tenant}-{recordTypeId}-{version}")
            .AsTask().GetAwaiter().GetResult();
        shared.PublishAsync(key, version, revision + 1, $"publish-{tenant}-{recordTypeId}-{version}").AsTask().GetAwaiter().GetResult();
    }
}
