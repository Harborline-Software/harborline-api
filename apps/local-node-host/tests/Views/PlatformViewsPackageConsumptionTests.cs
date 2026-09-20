using System.Text.Json;

using Harborline.Blocks.EntityViews;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Views;

public sealed class PlatformViewsPackageConsumptionTests
{
    [Fact]
    public async Task Pinned_package_authors_publishes_and_executes_a_bound_view()
    {
        Assert.Equal("Harborline.Blocks.EntityViews", typeof(ViewQueryRuntime).Assembly.GetName().Name);

        var kinds = new Kinds();
        var records = new RecordTypes();
        var measures = new Measures();
        var store = new InMemoryViewDefinitionStore();
        var authoring = new ViewDefinitionAuthoring(
            new ViewDefinitionAdmission(
                kinds,
                records,
                measures,
                new ViewExpressionFunctionRegistry(),
                new Interactions()),
            store);
        var binding = new ViewBinding(
            "layout.table",
            new Dictionary<ViewShapeRole, string> { [ViewShapeRole.Title] = "title" });
        var definition = new ViewDefinition(
            new ViewDefinitionEnvelope(
                "work.queue",
                "1.0.0",
                "team-a",
                ViewCascadeLayer.Pack,
                JsonSerializer.SerializeToElement(new { source = "api-package-consumer" }),
                []),
            1,
            "Work queue",
            "work-item",
            ViewOwnershipTier.Public,
            "work:read",
            new ViewQueryParameters(
                [new ViewColumn("title", 240)],
                [new ViewSort("title", ViewSortDirection.Ascending)],
                ViewFilter.Equal("state", "open"),
                "state",
                new ViewMeasureBinding("work.count", new Dictionary<string, string>())));

        await authoring.CreateDraftAsync(new ViewDefinitionDraft(definition, binding));
        await store.PublishAsync("team-a", "work.queue", "1.0.0");

        var rows = new RecordingRows();
        var runtime = new ViewQueryRuntime(
            store,
            new OpenGate(),
            kinds,
            records,
            new AccessFilter(),
            rows,
            measures,
            TimeProvider.System);
        var result = await runtime.ExecuteAsync(new ViewQueryRequest(
            "team-a",
            "work.queue",
            "party:operator-1",
            new ViewPage(0, 25),
            binding));

        Assert.Equal([ViewPredicateSource.Access, ViewPredicateSource.Authored],
            rows.Plan!.Predicates.Select(predicate => predicate.Source));
        Assert.Equal("visible", Assert.Single(result.Rows).Id);
        Assert.Equal(new ViewMeasureResult("work.count", 1), result.Measure);
        Assert.True(result.Authority.CanOpen);
    }

    private sealed class Kinds : IViewKindRegistry
    {
        private static readonly ViewKindDescriptor Table =
            new("layout.table", "hlp.ui.data-grid", [ViewShapeRole.Title]);

        public ValueTask<ViewKindDescriptor?> ResolveAsync(
            string kind,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ViewKindDescriptor?>(kind == Table.Kind ? Table : null);

        public ValueTask<IReadOnlyList<ViewKindDescriptor>> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ViewKindDescriptor>>([Table]);
    }

    private sealed class RecordTypes : IViewRecordTypeRegistry
    {
        public ValueTask<ViewRecordTypeDescriptor?> ResolveAsync(
            string recordType,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ViewRecordTypeDescriptor?>(new(
                recordType,
                new Dictionary<string, ViewRecordFieldKind>
                {
                    ["title"] = ViewRecordFieldKind.Text,
                    ["state"] = ViewRecordFieldKind.Text,
                    ["assignee"] = ViewRecordFieldKind.Text,
                }));
    }

    private sealed class Measures : IViewMeasureCatalog
    {
        public ValueTask<ViewMeasureDescriptor?> ResolveAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ViewMeasureDescriptor?>(
                name == "work.count" ? new ViewMeasureDescriptor(name, []) : null);

        // T-624 widened the released contract: a measure is evaluated FOR a tenant and a principal,
        // never ambiently. This consumption stub takes both and still counts rows, which is what the
        // test measures; that the runtime forwards the right two is T-624's own proof, not this one's.
        public ValueTask<ViewMeasureResult> EvaluateAsync(
            ViewMeasureBinding binding,
            IReadOnlyList<ViewRow> rows,
            DateTimeOffset evaluatedAt,
            string tenant,
            string principal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ViewMeasureResult(binding.Name, rows.Count));
    }

    private sealed class Interactions : IViewInteractionRegistry
    {
        public ValueTask<ViewWidgetDescriptor?> ResolveWidgetAsync(
            string widget,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ViewWidgetDescriptor?>(null);

        public ValueTask<bool> HasRowActionAsync(
            string action,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> HasWorkflowTransitionAsync(
            string transition,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class OpenGate : IViewOpenGate
    {
        public ValueTask<ViewAuthority> AuthorizeAsync(
            ViewDefinition definition,
            string principal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ViewAuthority(true, []));
    }

    private sealed class AccessFilter : IViewAccessFilter
    {
        public ValueTask<ViewFilter> BuildAsync(
            string tenant,
            string principal,
            string recordType,
            DateTimeOffset at,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ViewFilter.Equal("assignee", principal));
    }

    private sealed class RecordingRows : IViewRowSource
    {
        public ViewQueryPlan? Plan { get; private set; }

        public ValueTask<ViewRowPage> QueryAsync(
            ViewQueryPlan plan,
            CancellationToken cancellationToken = default)
        {
            Plan = plan;
            var row = new ViewRow("visible", new Dictionary<string, object?>
            {
                ["title"] = "A",
                ["state"] = "open",
                ["assignee"] = "party:operator-1",
            });
            return ValueTask.FromResult(new ViewRowPage([row], 1, [], [row]));
        }
    }
}
