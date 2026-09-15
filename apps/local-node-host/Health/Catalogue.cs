using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Scoring;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.Foundation.Taxonomy.Services;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using AmbientTenantContext = Harborline.Api.Foundation.MultiTenancy.ITenantContext;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>The common read envelope for a projected definition.</summary>
public sealed record CatalogueEntry(
    string Id,
    string Version,
    PackContentKind Kind,
    InternationalizedTextDto? Title,
    CatalogueProvenance Provenance,
    bool Sealed,
    string Status,
    DateTimeOffset UpdatedAt,
    JsonElement Body,
    string? DefinitionHash = null,
    RenderPlan? RenderPlan = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CatalogueFieldSourceBinding? CatalogueFieldBinding = null);

/// <summary>The pack authority that supplied a catalogue entry.</summary>
public sealed record CatalogueProvenance(
    [property: JsonPropertyName("packKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackKey,
    [property: JsonPropertyName("packVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackVersion,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>One compiled, sealed system type represented by the catalogue.</summary>
public sealed record SystemRecordType(
    PackContentKind Kind,
    string Name,
    bool Sealed,
    CatalogueProvenance Provenance)
{
    /// <summary>Every pack content kind is a sealed platform record type.</summary>
    public static IReadOnlyList<SystemRecordType> All { get; } = Enum.GetValues<PackContentKind>()
        .Where(kind => kind != PackContentKind.RecordType)
        .Select(kind => new SystemRecordType(
            kind,
            kind.ToString(),
            Sealed: true,
            new CatalogueProvenance("harborline.platform", null, "platform")))
        .ToArray();

    /// <summary>
    /// Resolves the one compiled descriptor set through the active platform pack's carried catalogue.
    /// The seed supplies lifecycle and provenance; it never supplies a second schema.
    /// </summary>
    public static IReadOnlyList<SystemRecordType> FromActivePlatformPack(InstalledPack? platform)
    {
        if (platform is null
            || platform.Lifecycle != PackLifecycleState.Active
            || !string.Equals(platform.PackKey, "harborline.platform", StringComparison.Ordinal))
        {
            return Array.Empty<SystemRecordType>();
        }

        var carried = new Dictionary<string, PackSeedItem>(StringComparer.Ordinal);
        foreach (var item in platform.SeedItems.Where(item => item.Kind == PackContentKind.RecordType))
        {
            if (!carried.TryAdd(item.Key, item))
                return Array.Empty<SystemRecordType>();
        }

        if (carried.Count != All.Count) return Array.Empty<SystemRecordType>();

        foreach (var descriptor in All)
        {
            if (!carried.TryGetValue(descriptor.Name, out var item) || !IsValidPlatformDeclaration(item))
                return Array.Empty<SystemRecordType>();
        }

        var provenance = new CatalogueProvenance(platform.PackKey, platform.Version, "platform");
        return All.Select(descriptor => descriptor with { Provenance = provenance }).ToArray();
    }

    private static bool IsValidPlatformDeclaration(PackSeedItem item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.CanonicalJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("sealed", out var sealedProperty)
                   && sealedProperty.ValueKind is JsonValueKind.True
                   && root.TryGetProperty("provenance", out var provenance)
                   && provenance.ValueKind == JsonValueKind.Object
                   && provenance.TryGetProperty("kind", out var kind)
                   && kind.ValueKind == JsonValueKind.String
                   && string.Equals(kind.GetString(), "platform", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>A catalogue response including kinds whose projector is not composed in this host.</summary>
public sealed record CatalogueList(
    IReadOnlyList<CatalogueEntry> Entries,
    IReadOnlyList<PackContentKind> KindsUnavailable);

/// <summary>The single tenant-scoped read seam over projected definition stores.</summary>
public interface ICatalogue
{
    ValueTask<CatalogueList> ListAsync(
        TenantId tenant,
        PackContentKind? kind = null,
        CancellationToken cancellationToken = default);

    ValueTask<CatalogueEntry?> GetAsync(
        TenantId tenant,
        PackContentKind kind,
        string id,
        string? version = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapter over the already-composed definition stores. Kinds without a composed adapter are
/// explicit empty/unavailable results: a catalogue read never invents persistence or throws.
/// </summary>
public sealed class ProjectedCatalogue : ICatalogue
{
    private readonly AuthorizedFormDefinitionLifecycle authorizedForms;
    private readonly IViewDefinitionRegistry? viewDefinitions;
    private readonly InMemoryRenderPlanCatalogue? renderPlans;
    private readonly CatalogueRegistries? registries;
    private readonly TerminologyProjection? terminology;

    /// <summary>
    /// Reads forms through the same authorized lifecycle that owns the Form definition route family.
    /// Raw store access remains private to that lifecycle, so composition has one unambiguous catalogue
    /// constructor and no route can bypass the read authority seam.
    /// </summary>
    public ProjectedCatalogue(
        AuthorizedFormDefinitionLifecycle forms,
        IViewDefinitionRegistry? viewDefinitions = null,
        InMemoryRenderPlanCatalogue? renderPlans = null,
        CatalogueRegistries? registries = null,
        TerminologyProjection? terminology = null)
    {
        authorizedForms = forms ?? throw new ArgumentNullException(nameof(forms));
        this.viewDefinitions = viewDefinitions;
        this.renderPlans = renderPlans;
        this.registries = registries;
        this.terminology = terminology;
    }

    private bool IsAvailable(PackContentKind kind) => kind == PackContentKind.FormDefinition
        || (kind == PackContentKind.ViewDefinition && viewDefinitions is not null)
        || (kind == PackContentKind.TerminologyOverride && terminology is not null)
        || registries?.IsAvailable(kind) == true;

    public async ValueTask<CatalogueList> ListAsync(
        TenantId tenant,
        PackContentKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (kind is { } requested && !IsAvailable(requested))
        {
            return new CatalogueList(Array.Empty<CatalogueEntry>(), [requested]);
        }

        var entries = new List<CatalogueEntry>();
        if ((kind is null or PackContentKind.TerminologyOverride) && terminology is not null)
            entries.AddRange(terminology.List(tenant));
        if (kind is null or PackContentKind.FormDefinition)
        {
            await foreach (var definition in ListFormsAsync(tenant, cancellationToken).ConfigureAwait(false))
                entries.Add(From(tenant, definition));
        }

        if ((kind is null || kind == PackContentKind.ViewDefinition) && viewDefinitions is not null)
        {
            var views = await viewDefinitions.ListDefinitionsAsync(tenant.Value, cancellationToken).ConfigureAwait(false);
            entries.AddRange(views.Select(definition => From(tenant, definition)));
        }

        if (registries is not null)
            entries.AddRange(await registries.ReadAsync(tenant, kind, cancellationToken: cancellationToken).ConfigureAwait(false));

        return new CatalogueList(entries, kind is null
            ? Enum.GetValues<PackContentKind>().Where(candidate => !IsAvailable(candidate)).ToArray()
            : Array.Empty<PackContentKind>());
    }

    public async ValueTask<CatalogueEntry?> GetAsync(
        TenantId tenant,
        PackContentKind kind,
        string id,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        if (kind == PackContentKind.TerminologyOverride)
            return terminology?.List(tenant).SingleOrDefault(entry => entry.Id == id && (version is null || entry.Version == version));
        if (kind == PackContentKind.ViewDefinition && viewDefinitions is not null)
        {
            if (version is null)
            {
                var history = await viewDefinitions.ListVersionsAsync(tenant.Value, id, cancellationToken).ConfigureAwait(false);
                return history is null || history.Versions.Count == 0 ? null : From(tenant, history.Versions[0]);
            }

            var view = await viewDefinitions.GetDefinitionAsync(tenant.Value, id, version, cancellationToken).ConfigureAwait(false);
            return view is null ? null : From(tenant, view);
        }

        if (kind != PackContentKind.FormDefinition)
        {
            if (registries is null || !registries.IsAvailable(kind)) return null;
            var entries = await registries.ReadAsync(tenant, kind, id, version, cancellationToken).ConfigureAwait(false);
            return entries.OrderByDescending(entry => entry.Version, Comparer<string>.Create(PackVersion.Compare)).FirstOrDefault();
        }

        try
        {
            var definition = version is null
                ? await GetCurrentPublishedAsync(new DefinitionAddress(tenant, id), cancellationToken).ConfigureAwait(false)
                : await GetAsync(new DefinitionCoordinates(tenant, id, version), cancellationToken).ConfigureAwait(false);
            return definition is null || definition.Status != FormDefinitionStatus.Published ? null : From(tenant, definition);
        }
        catch (FormDefinitionNotFoundException)
        {
            return null;
        }
    }

    private CatalogueEntry From(TenantId tenant, FormDefinition definition)
    {
        var source = definition.PackSource;
        var plan = renderPlans?.Get(tenant, PackContentKind.FormDefinition, definition.Id.Value, definition.Version.ToString());
        return new CatalogueEntry(
            definition.Id.Value,
            definition.Version.ToString(),
            PackContentKind.FormDefinition,
            InternationalizedTextDto.From(definition.Overlay.Title),
            source is null
                ? new CatalogueProvenance(null, null, "tenant")
                : new CatalogueProvenance(source.PackId, source.PackVersion, "pack"),
            Sealed: false,
            definition.Status.ToString(),
            definition.UpdatedAt,
            JsonSerializer.SerializeToElement(FormDefinitionDto.From(definition)),
            plan?.DefinitionHash,
            plan,
            authorizedForms.CatalogueSources.Resolve(tenant, new CatalogueFieldCoordinate(1, "FormDefinition",
                definition.Id.Value, definition.Version.ToString(), "formId"))?.Identity.Binding);
    }

    private CatalogueEntry From(TenantId tenant, ViewDefinition definition)
    {
        var plan = renderPlans?.Get(tenant, PackContentKind.ViewDefinition, definition.Key, definition.Version);
        return new CatalogueEntry(
            definition.Key,
            definition.Version,
            PackContentKind.ViewDefinition,
            new InternationalizedTextDto("en", new Dictionary<string, string> { ["en"] = definition.Title }),
            new CatalogueProvenance(plan?.PackKey, plan?.PackVersion, plan is null ? "tenant" : "pack"),
            Sealed: false,
            "Published",
            DateTimeOffset.MinValue,
            JsonSerializer.SerializeToElement(ViewDefinitionDto.From(definition)),
            plan?.DefinitionHash,
            plan);
    }

    private IAsyncEnumerable<FormDefinition> ListFormsAsync(TenantId tenant, CancellationToken cancellationToken)
        => authorizedForms.ListByTenantAsync(tenant, cancellationToken);

    private ValueTask<FormDefinition?> GetCurrentPublishedAsync(DefinitionAddress address, CancellationToken cancellationToken)
        => authorizedForms.GetCurrentPublishedAsync(address, cancellationToken);

    private ValueTask<FormDefinition> GetAsync(DefinitionCoordinates coordinates, CancellationToken cancellationToken)
        => authorizedForms.GetAsync(coordinates, cancellationToken);
}

/// <summary>
/// Reads the host's domain registries. Active pack coordinates index shared or non-enumerable stores;
/// immutable seed bodies are never returned in place of an admitted registry row.
/// </summary>
public sealed class CatalogueRegistries(
    IPackInstallStore packs,
    IEntityTypeRegistry? assetTypes = null,
    AuthorizedWorkflowDefinitionLifecycle? workflows = null,
    IDocumentTemplateRegistry? templates = null,
    ITaxonomyRegistry? taxonomies = null,
    IReportDefinitionRegistry? reports = null,
    IDataExchangeDefinitionRegistry? exchanges = null,
    IStandingRuleDefinitionStore? standings = null,
    IScheduleDefinitionRegistry? schedules = null,
    IStandardCatalogSeedStore? standards = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether the host supplied the authentic registry for this content kind.</summary>
    public bool IsAvailable(PackContentKind kind) => kind switch
    {
        PackContentKind.AssetTypeDefinition => assetTypes is not null,
        PackContentKind.WorkflowDefinition => workflows is not null,
        PackContentKind.TemplateDefinition => templates is not null,
        PackContentKind.TaxonomyDefinition => taxonomies is not null,
        PackContentKind.ReportDefinition => reports is not null,
        PackContentKind.DataExchangeDefinition => exchanges is not null,
        PackContentKind.StandingRuleDefinition => standings is not null,
        PackContentKind.ScheduleDefinition => schedules is not null,
        PackContentKind.StandardsCatalog => standards is not null,
        _ => false,
    };

    /// <summary>Reads admitted rows using the requested tenant and optional pinned coordinates.</summary>
    public async ValueTask<IReadOnlyList<CatalogueEntry>> ReadAsync(
        TenantId tenant, PackContentKind? kind, string? id = null, string? version = null,
        CancellationToken cancellationToken = default)
    {
        var installed = packs.ListInstalled(tenant);
        var collisions = PackCompositionConflicts.Detect(
                PackCompositionConflicts.ClaimsFromInstalled(installed), packs.GetKeyOwnership(tenant))
            .ToDictionary(collision => collision.ContentKey, StringComparer.Ordinal);
        var active = installed.Where(pack => pack.Lifecycle == PackLifecycleState.Active)
            .SelectMany(pack => pack.SeedItems.Select(item => (Pack: pack, Item: item)))
            .Where(source => !collisions.TryGetValue(source.Item.Key, out var collision)
                || string.Equals(collision.OwnerPackKey, source.Pack.PackKey, StringComparison.Ordinal))
            .ToArray();
        var entries = new List<CatalogueEntry>();
        bool Reads(PackContentKind candidate) => (kind is null || kind == candidate) && IsAvailable(candidate);
        void Add(PackContentKind entryKind, string key, string revision, string? title, object body,
            CatalogueProvenance? provenance = null, DateTimeOffset updatedAt = default, InternationalizedTextDto? localizedTitle = null,
            Func<PackSeedItem, bool>? matchesSource = null, bool sealedEntry = false, string status = "Published")
        {
            if ((id is not null && id != key) || (version is not null && version != revision)) return;
            if (provenance is null)
            {
                var source = active.FirstOrDefault(source => source.Item.Kind == entryKind
                    && source.Item.Key == key && source.Item.Version == revision);
                if (source.Pack is not null)
                {
                    if (matchesSource?.Invoke(source.Item) == false) return;
                    provenance = PackProvenance(source.Pack);
                }
                else if (installed.Any(pack => pack.SeedItems.Any(item => item.Kind == entryKind
                             && item.Key == key && item.Version == revision))) return;
                else provenance = new CatalogueProvenance(null, null, "tenant");
            }
            entries.Add(new CatalogueEntry(key, revision, entryKind,
                localizedTitle ?? (title is null ? null : new InternationalizedTextDto("en", new Dictionary<string, string> { ["en"] = title })),
                provenance, sealedEntry, status, updatedAt, JsonSerializer.SerializeToElement(body, Json)));
        }

        if (Reads(PackContentKind.WorkflowDefinition))
        {
            if (id is not null && version is not null)
            {
                try { AddWorkflow(await workflows!.GetAsync(new DefinitionCoordinates(tenant, id, version), cancellationToken).ConfigureAwait(false)); }
                catch (WorkflowDefinitionNotFoundException) { }
            }
            else
                await foreach (var workflow in workflows!.ListByTenantAsync(tenant, cancellationToken).ConfigureAwait(false)) AddWorkflow(workflow);
        }
        void AddWorkflow(WorkflowDefinitionRecord workflow)
        {
            if (workflow.Status != WorkflowDefinitionStatus.Published) return;
            var source = workflow.PackSource;
            if (source is not null && !active.Any(candidate => candidate.Pack.PackKey == source.PackId
                    && candidate.Pack.Version == source.PackVersion && candidate.Item.Kind == PackContentKind.WorkflowDefinition
                    && candidate.Item.Key == workflow.Key && candidate.Item.Version == workflow.Version)) return;
            var title = workflow.Authored.TryGetProperty("title", out var authoredTitle) && authoredTitle.ValueKind == JsonValueKind.String
                ? authoredTitle.GetString() : null;
            Add(PackContentKind.WorkflowDefinition, workflow.Key, workflow.Version, title, workflow.Authored,
                source is null ? new CatalogueProvenance(null, null, "tenant") : new CatalogueProvenance(source.PackId, source.PackVersion, "pack"),
                workflow.UpdatedAt, authoredTitle.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<InternationalizedTextDto>(authoredTitle, Json) : null);
        }

        if (Reads(PackContentKind.AssetTypeDefinition))
        {
            var tenantTypes = await assetTypes!.ListTypesAsync(tenant, cancellationToken).ConfigureAwait(false);
            foreach (var row in tenantTypes)
                Add(PackContentKind.AssetTypeDefinition, row.Id.Value, string.Empty, row.Descriptor.DisplayName,
                    AssetBody(row.Id, row.Descriptor), new CatalogueProvenance(null, null, "tenant"));
            foreach (var seed in assetTypes.ListSeeds().Where(seed => !tenantTypes.Any(row => row.Id == seed.Id)))
            {
                if (seed.Provenance == CascadeLayer.Base)
                    Add(PackContentKind.AssetTypeDefinition, seed.Id.Value, string.Empty, seed.Descriptor.DisplayName,
                        AssetBody(seed.Id, seed.Descriptor), new CatalogueProvenance(null, null, "platform"));
                else
                    foreach (var source in active.Where(source => source.Item.Kind == PackContentKind.AssetTypeDefinition))
                        if (PackAssetTypeContent.TryParse(source.Item.ParseContent(), source.Item.Version,
                                key => source.Pack.SeedItems.FirstOrDefault(item => item.Key == key && item.Kind == PackContentKind.FormDefinition)?.Version,
                                out var typeId, out var expected, out _) && typeId == seed.Id
                            && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(AssetBody(typeId, expected), Json),
                                JsonSerializer.SerializeToElement(AssetBody(seed.Id, seed.Descriptor), Json)))
                            Add(PackContentKind.AssetTypeDefinition, seed.Id.Value, source.Item.Version, seed.Descriptor.DisplayName,
                                AssetBody(seed.Id, seed.Descriptor), PackProvenance(source.Pack));
            }
        }
        if (Reads(PackContentKind.TaxonomyDefinition))
            foreach (var taxonomy in await taxonomies!.ListDefinitionsAsync(tenant, null, cancellationToken).ConfigureAwait(false))
                if (taxonomy.RetiredAt is null)
                    Add(PackContentKind.TaxonomyDefinition, taxonomy.Id.Value, taxonomy.Version.ToString(), taxonomy.Description,
                        taxonomy, updatedAt: taxonomy.PublishedAt,
                        matchesSource: item => MatchesRegistrySource(item, taxonomy, candidate => new
                        {
                            candidate.Id, candidate.Version, candidate.Governance, candidate.Description, candidate.Owner, candidate.DerivedFrom,
                        }));
        if (Reads(PackContentKind.ReportDefinition))
        {
            IEnumerable<ReportDefinition?> definitions = id is not null && version is not null
                ? (IEnumerable<ReportDefinition?>)new[] { await reports!.GetDefinitionAsync(tenant.Value, id, version, cancellationToken).ConfigureAwait(false) }
                : await reports!.ListDefinitionsAsync(tenant.Value, cancellationToken).ConfigureAwait(false);
            foreach (var report in definitions)
                if (report is not null) Add(PackContentKind.ReportDefinition, report.Key, report.Version, report.Title, ReportDefinitionDto.From(report),
                    matchesSource: item => MatchesRegistrySource(item, report, candidate => ReportDefinitionDto.From(candidate with { Tenant = tenant.Value })));
        }
        if (Reads(PackContentKind.DataExchangeDefinition))
        {
            IEnumerable<DataExchangeDefinition?> definitions = id is not null && version is not null
                ? (IEnumerable<DataExchangeDefinition?>)new[] { await exchanges!.GetDefinitionAsync(tenant.Value, id, version, cancellationToken).ConfigureAwait(false) }
                : await exchanges!.ListDefinitionsAsync(tenant.Value, cancellationToken).ConfigureAwait(false);
            foreach (var exchange in definitions)
                if (exchange is not null) Add(PackContentKind.DataExchangeDefinition, exchange.Key, exchange.Version, exchange.Title, DataExchangeDefinitionDto.From(exchange),
                    matchesSource: item => MatchesRegistrySource(item, exchange, candidate => DataExchangeDefinitionDto.From(candidate with { Tenant = tenant.Value })));
        }
        if (Reads(PackContentKind.StandardsCatalog))
        {
            // StandardCatalogSeed is a shared immutable registry row. Its model deliberately has no
            // revision, lifecycle status, timestamp or owning pack coordinates, so the catalogue
            // leaves those envelope values unknown rather than manufacturing metadata. Tenant
            // scoring overrides remain a separate store concern and never mutate this seed body.
            foreach (var seed in standards!.List())
            {
                if ((id is not null && !string.Equals(id, seed.Key, StringComparison.Ordinal))
                    || (version is not null && version.Length != 0)) continue;
                Add(PackContentKind.StandardsCatalog, seed.Key, string.Empty, seed.Title, seed,
                    new CatalogueProvenance(null, null, SeedProvenance(seed.Provenance)),
                    updatedAt: DateTimeOffset.MinValue, sealedEntry: true, status: string.Empty);
            }
        }
        foreach (var source in active.Where(source => Reads(source.Item.Kind)))
        {
            var item = source.Item;
            if ((id is not null && item.Key != id) || (version is not null && item.Version != version)) continue;
            switch (item.Kind)
            {
                case PackContentKind.TemplateDefinition:
                    if (templates!.Resolve(item.Key, item.Version) is { } template && template.Envelope.Tenant == tenant
                        && PackTemplateContent.TryParse(item.ParseContent(), tenant, out var expectedTemplate, out _)
                        && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(template, Json), JsonSerializer.SerializeToElement(expectedTemplate, Json)))
                        Add(item.Kind, template.Key, template.Version, template.DocumentType, template, PackProvenance(source.Pack));
                    break;
                case PackContentKind.StandingRuleDefinition:
                    if (await standings!.GetAsync(item.Key, item.Version, cancellationToken).ConfigureAwait(false) is { } rule
                        && MatchesStandingSource(rule, item))
                        Add(item.Kind, rule.RuleId, rule.RuleVersion, rule.Standing.Name, rule, PackProvenance(source.Pack));
                    break;
                case PackContentKind.ScheduleDefinition:
                    if (await schedules!.GetDefinitionAsync(tenant.Value, item.Key, item.Version, cancellationToken).ConfigureAwait(false) is { } schedule
                        && MatchesRegistrySource(item, schedule, candidate => candidate with { Tenant = tenant.Value }))
                        Add(item.Kind, schedule.Key, schedule.Version, schedule.Title, schedule, PackProvenance(source.Pack));
                    break;
            }
        }
        return entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ThenBy(entry => entry.Version, StringComparer.Ordinal).ToArray();
    }

    private static CatalogueProvenance PackProvenance(InstalledPack pack) => new(pack.PackKey, pack.Version, "pack");

    private static string SeedProvenance(CascadeLayer layer) => layer switch
    {
        CascadeLayer.Base => "base-seed",
        CascadeLayer.Pack => "pack-seed",
        _ => throw new InvalidOperationException($"Standard catalog seed provenance '{layer}' is not a seed layer."),
    };

    private static bool MatchesRegistrySource<T>(PackSeedItem item, T actual, Func<T, object> projectedBody)
    {
        try
        {
            var expected = JsonSerializer.Deserialize<T>(item.CanonicalJson, Json);
            return expected is not null && JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(projectedBody(actual), Json), JsonSerializer.SerializeToElement(projectedBody(expected), Json));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return false; }
    }

    private static bool MatchesStandingSource(StandingRuleDefinition rule, PackSeedItem item)
    {
        try
        {
            var expected = JsonSerializer.Deserialize<StandingRuleDefinition>(item.CanonicalJson, Json);
            return expected is not null && JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(rule, Json), JsonSerializer.SerializeToElement(expected, Json));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or Harborline.Api.Foundation.RuleEngine.Compilation.RuleCompilationException) { return false; }
    }

    // The registry has no revision/timestamp for authored asset types; the envelope leaves those unknown.
    private static object AssetBody(EntityTypeId id, EntityTypeDescriptor descriptor) => new
    {
        id = id.Value, descriptor.DisplayName, descriptor.Traits, descriptor.ParentType, descriptor.PropertyFormBinding,
        descriptor.Disciplines,
        inspectionFormBindings = descriptor.InspectionFormBindings.ToDictionary(binding => binding.Key.Value, binding => binding.Value),
        descriptor.ExpectedUsefulLifeYears, descriptor.TypicalReplacementCost, descriptor.ConditionScaleMax,
    };
}

/// <summary>The gate-authorized tenant-scoped catalogue read family.</summary>
public static class CatalogueRoutes
{
    public const string RouteBase = "/api/local-node/catalogue/definitions";
    public const string TypesRoute = "/api/local-node/catalogue/types";

    public static void Map(
        IEndpointRouteBuilder app,
        ICatalogue catalogue,
        IPackInstallStore packStore,
        AmbientTenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(packStore);
        ArgumentNullException.ThrowIfNull(tenantContext);

        app.MapGet(RouteBase, async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            var tenant = RequestTenant(http, tenantContext);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of("catalogue-definitions"), ct).ConfigureAwait(false) is { } denied)
                return denied;

            if (!TryKind(http.Request.Query["kind"], out var kind))
                return Results.BadRequest(new { code = "catalogue.kind_unknown" });

            var result = await catalogue.ListAsync(tenant, kind, ct).ConfigureAwait(false);
            return Results.Ok(new { entries = result.Entries, kindsUnavailable = result.KindsUnavailable });
        });

        app.MapGet($"{RouteBase}/{{kind}}/{{id}}", async Task<IResult> (
            string kind, string id, HttpContext http, CancellationToken ct) =>
        {
            var tenant = RequestTenant(http, tenantContext);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of(id), ct).ConfigureAwait(false) is { } denied)
                return denied;

            if (!Enum.TryParse<PackContentKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.BadRequest(new { code = "catalogue.kind_unknown" });

            var result = await catalogue.GetAsync(tenant, parsed, id, http.Request.Query["version"], ct).ConfigureAwait(false);
            return result is null ? Results.NotFound(new { code = "catalogue.definition_not_found" }) : Results.Ok(result);
        });

        app.MapGet(TypesRoute, async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            var tenant = RequestTenant(http, tenantContext);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of("catalogue-types"), ct).ConfigureAwait(false) is { } denied)
                return denied;
            var platform = packStore.GetActive(tenant, "harborline.platform");
            return Results.Ok(SystemRecordType.FromActivePlatformPack(platform));
        });
    }

    private static TenantId RequestTenant(HttpContext http, AmbientTenantContext tenantContext) =>
        http.Features.Get<SelectedSessionRequestPrincipal>()?.TenantId
        ?? tenantContext.Tenant?.Id
        ?? throw new InvalidOperationException("No tenant is resolved for the catalogue request.");

    private static bool TryKind(string? raw, out PackContentKind? kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            kind = null;
            return true;
        }

        if (Enum.TryParse<PackContentKind>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            kind = parsed;
            return true;
        }

        kind = null;
        return false;
    }
}
