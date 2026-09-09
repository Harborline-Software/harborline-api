using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Catalogs;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// DEV-ONLY living-standard catalog seeder (ADR 0101 Rev 3.1 Wave 3a). On a development node it makes the
/// residential living-standard slice LIVE end-to-end: it registers + publishes the catalog item FORM
/// (<see cref="LivingStandardCatalogForm"/>), registers the catalog's condition-rating BINDINGS for the
/// active-team tenant, and seeds the immutable <see cref="ResidentialLivingStandardCatalog"/> catalog into
/// the shared <see cref="IStandardCatalogSeedStore"/>. A submission of the catalog form through the node's
/// forms route then projects a typed condition assessment per rated item onto the inspected unit
/// ("one act, many artifacts").
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive sibling to <see cref="FormsDevSeeder"/>.</b> Same airtight dev gate
/// (<see cref="CalendarDevSeeder.ShouldSeed"/> — IsDevelopment() only); a Production
/// host never seeds. Different form id, so it does not touch the equipment-inspection demo. Idempotent: an
/// already-published catalog form (and an already-seeded catalog) are left as-is. Registered AFTER
/// <c>AddNodeForms</c> + <c>AddNodeAssetRegistry</c> (the stores + binding store exist) and the host-level
/// bootstrap (an active team exists by StartAsync time).
/// </para>
/// </remarks>
public sealed class LivingStandardCatalogDevSeeder : IHostedService
{
    private readonly ISchemaRegistry _schemaRegistry;
    private readonly AuthorizedFormDefinitionLifecycle _store;
    private readonly IConditionRatingFieldBindingStore _bindings;
    private readonly IStandardCatalogSeedStore _catalogs;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LivingStandardCatalogDevSeeder> _logger;

    public LivingStandardCatalogDevSeeder(
        ISchemaRegistry schemaRegistry,
        AuthorizedFormDefinitionLifecycle store,
        IConditionRatingFieldBindingStore bindings,
        IStandardCatalogSeedStore catalogs,
        IActiveTeamAccessor activeTeam,
        IHostEnvironment environment,
        ILogger<LivingStandardCatalogDevSeeder> logger,
        TimeProvider? timeProvider = null)
    {
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!CalendarDevSeeder.ShouldSeed(_environment))
        {
            _logger.LogDebug(
                "LivingStandardCatalogDevSeeder: environment '{Environment}' is not development and no dev-seed "
                + "flag is set — skipping the residential living-standard catalog seed (production-safe).",
                _environment.EnvironmentName);
            return;
        }

        TenantId tenantId;
        try
        {
            tenantId = NodeTenant.Resolve(_activeTeam);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "LivingStandardCatalogDevSeeder: no active team resolved — skipping the catalog seed. (Register "
                + "this hosted service AFTER MultiTeamBootstrapHostedService.)");
            return;
        }

        try
        {
            await SeedAsync(_schemaRegistry, _store, _bindings, _catalogs, tenantId, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AuthorizationDeniedException ex)
        {
            _logger.LogWarning(
                ex,
                "LivingStandardCatalogDevSeeder: the development seed principal is not authorized for tenant {TenantId} — skipping the catalog seed.",
                tenantId);
            return;
        }

        _logger.LogInformation(
            "LivingStandardCatalogDevSeeder: residential living-standard catalog '{Key}' is LIVE for tenant "
            + "{TenantId} — the catalog form '{FormId}' renders in the runner and a submit projects a condition "
            + "assessment per rated item onto the inspected unit.",
            ResidentialLivingStandardCatalog.Key, tenantId, LivingStandardCatalogForm.FormId);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Registers + publishes the catalog form, registers its bindings, and seeds the shared catalog —
    /// idempotently. Reused by the route-level e2e so the test exercises the SAME seeding path.
    /// </summary>
    public static async Task SeedAsync(
        ISchemaRegistry schemaRegistry,
        AuthorizedFormDefinitionLifecycle store,
        IConditionRatingFieldBindingStore bindings,
        IStandardCatalogSeedStore catalogs,
        TenantId tenant,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var formId = new FormDefinitionId(LivingStandardCatalogForm.FormId);
        var authority = new AuthorizationWriteContext(
            new ActorId("installer:development-living-standard-seed"), tenant, now);
        var decision = await store.DecideAsync(formId.Value, authority, cancellationToken).ConfigureAwait(false);

        var existing = await store.GetCurrentPublishedAsync(
            new DefinitionAddress(tenant, formId.Value), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var schema = await schemaRegistry.RegisterAsync(LivingStandardCatalogForm.SchemaJson(), ct: cancellationToken).ConfigureAwait(false);
            var definition = LivingStandardCatalogForm.BuildDefinition(tenant, schema.Id, now);
            await store.RegisterAndPublishAsync(definition, decision, cancellationToken).ConfigureAwait(false);
        }

        // Register the catalog's condition-rating bindings for this tenant (idempotent per identical binding).
        foreach (var binding in ResidentialLivingStandardCatalog.BuildBindings())
        {
            await bindings.RegisterAsync(tenant, binding, cancellationToken).ConfigureAwait(false);
        }

        // Seed the immutable shared catalog once (the store rejects a duplicate key).
        if (catalogs.Get(ResidentialLivingStandardCatalog.Key) is null)
        {
            catalogs.Seed(ResidentialLivingStandardCatalog.BuildSeed());
        }
    }
}
