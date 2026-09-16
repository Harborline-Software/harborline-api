using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.Foundation.Taxonomy.Services;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Fact]
    public async Task Every_projected_store_reader_waits_for_the_activation_publication_barrier()
    {
        var types = _app.Services.GetRequiredService<IEntityTypeRegistry>();
        var schemas = _app.Services.GetRequiredService<ISchemaRegistry>();
        var templates = new InMemoryDocumentTemplateRegistry();
        var taxonomy = new InMemoryTaxonomyRegistry(TimeProvider.System);
        var standings = new InMemoryStandingRuleDefinitionStore();
        var exchanges = new InMemoryDataExchangeDefinitionRegistry(Substitute.For<IDataExchangeDefinitionDescriptorRegistry>());
        var schedules = new InMemoryScheduleDefinitionRegistry(Substitute.For<IScheduleDefinitionDescriptorRegistry>());
        var terminology = new TerminologyProjection();
        var probes = new (string Name, object Store, Action Read)[]
        {
            ("pack-pointer", _store, () => _store.ListInstalled(Tenant)),
            ("asset-types", types, () => types.ListSeeds()),
            ("forms", _forms, () => _forms.GetCurrentPublishedAsync(new(Tenant, "missing")).GetAwaiter().GetResult()),
            ("workflows", _workflows, () => _workflows.GetCurrentPublishedAsync(new(Tenant, "missing")).GetAwaiter().GetResult()),
            ("schemas", schemas, () => schemas.GetAsync(new("missing")).GetAwaiter().GetResult()),
            ("templates", templates, () => templates.Resolve("missing")),
            ("taxonomies", taxonomy, () => taxonomy.ListDefinitionsAsync(Tenant, null, CancellationToken.None).GetAwaiter().GetResult()),
            ("views", _views, () => _views.ListDefinitionsAsync(Tenant.Value).GetAwaiter().GetResult()),
            ("reports", _reports, () => _reports.ListDefinitionsAsync(Tenant.Value).GetAwaiter().GetResult()),
            ("data-exchanges", exchanges, () => exchanges.ListDefinitionsAsync(Tenant.Value).GetAwaiter().GetResult()),
            ("schedules", schedules, () => schedules.GetDefinitionAsync(Tenant.Value, "missing", "1.0.0").GetAwaiter().GetResult()),
            ("standing-rules", standings, () => standings.GetAsync("missing", "1.0.0").GetAwaiter().GetResult()),
            ("roles", _roles, () => _roles.ListAsync().GetAwaiter().GetResult()),
            ("authorization-definitions-and-bindings", _configuration, () => _configuration.ListAsync(Tenant).GetAwaiter().GetResult()),
            ("defaults", _defaults, () => _defaults.List(Tenant)),
            ("terminology", terminology, () => terminology.List(Tenant)),
            ("render-plans", _renderPlans, () => _renderPlans.Get(Tenant, PackContentKind.ViewDefinition, "missing", "1.0.0")),
            ("catalogue-detail-templates", _details, () => _details.Get(Tenant, "missing", "1.0.0")),
            ("catalogue-field-bindings", _authorizedForms.CatalogueSources,
                () => _authorizedForms.CatalogueSources.Resolve(Tenant, new(1, "FormDefinition", "missing", "1.0.0", "formId"))),
        };
        // This deliberately reads the real public surfaces on a separate execution context. Omitting
        // a read fence from any participant turns that probe into an immediate completion/failure.
        foreach (var probe in probes)
        {
            Task reader;
            using (var transaction = new PackProjectionTransaction())
            {
                transaction.Enlist(probe.Store);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ExecutionContext.SuppressFlow())
                    reader = Task.Factory.StartNew(() => { started.SetResult(); probe.Read(); },
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var timeout = Task.Delay(TimeSpan.FromMilliseconds(100));
                Assert.Same(timeout, await Task.WhenAny(reader, timeout));
            }
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
