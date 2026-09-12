using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.Foundation.Taxonomy.Models;
using Harborline.Api.Foundation.Taxonomy.Services;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Health;

using SchemaId = Harborline.Api.Foundation.Assets.Common.SchemaId;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Projects the content of a tenant's ACTIVE installed packs into the live domain registries the node's
/// read APIs consume — the missing seam between the Pack Composer install engine (B-1b, which stores
/// verified content as an opaque immutable seed layer) and the runtime stores a UI reads. Without this,
/// installing a pack shows in <c>GET /packs/installed</c> yet the <c>GET /asset-registry/types</c>
/// dropdown stays empty (the dead-end this closes for asset types).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch by kind (fail-closed on kinds this node doesn't yet consume).</b>
/// <see cref="PackContentKind.AssetTypeDefinition"/> is projected into the shared
/// <see cref="IEntityTypeRegistry"/> at <see cref="CascadeLayer.Pack"/> provenance.
/// <see cref="PackContentKind.FormDefinition"/> is projected through the same schema synthesis and
/// publish-admission seam as the authoring route.
/// <see cref="PackContentKind.WorkflowDefinition"/> is projected through the durable workflow store,
/// which is the authoring route's fail-closed admission seam.
/// <see cref="PackContentKind.TaxonomyDefinition"/> is projected into the taxonomy registry; its governance
/// regime determines whether an own-roster or Harborline-channel pack may author it.
/// <see cref="PackContentKind.NavWorkspaceConfig"/> is projected directly from active immutable seeds by
/// <see cref="PackNavigationRoutes"/> on every read, so this reconciler only registers it as handled.
/// Every other kind produces a structured refusal and a warning. Install-preview normally prevents such
/// content from entering the store; this defensive branch keeps legacy or bypassed seed layers loud.
/// </para>
/// <para>
/// <b>FormDefinition trust seam.</b> The content body is the pinned
/// <c>SaveFormDefinitionRequest</c> contract plus an optional envelope; id/version come from the verified
/// pack item and an envelope must agree with those coordinates and the install tenant. Projection
/// synthesizes and registers the schema, preserves a carried envelope (or supplies legacy defaults), and
/// runs rule-compile plus classification admission before Register + Publish. Exact replay is a no-op,
/// and a matching Draft is published to recover an interrupted projection; any different content at the
/// pinned tuple is refused.
/// </para>
/// <para>
/// <b>WorkflowDefinition trust seam.</b> Projection server-owns key/version/tenant, replaces any
/// pack-authored owner with the canonical system identity, and stamps <see cref="CascadeLayer.Pack"/>
/// provenance before passing the definition through the same wire mapper, admission validator, durable
/// store, and publish transition as authoring. Exact replay is a no-op; a matching Draft is resumed; a
/// different definition at the pinned tuple is refused with a stable code.
/// </para>
/// <para>
/// <b>Idempotent reconciliation.</b> Every pass first retracts runtime projections owned by Inactive
/// packs, then projects Active packs. Source seed items, tenant overrides, and authored revisions remain
/// durable; reactivation restores the same pinned content. Collision ownership is retained and applied to
/// both directions, so an inactive non-owner cannot retract another pack's live content.
/// </para>
/// <para>
/// <b>Provenance.</b> The projector stamps <see cref="CascadeLayer.Pack"/>; content never self-declares
/// its layer. In this single-tenant node the pack is org-level, so a shared (all-tenant-visible) seed is
/// correct; scoping a pack's seeds to only the installing tenant on a multi-tenant node is a Wave-2 axis.
/// </para>
/// </remarks>
internal interface IPackSeedProjector : IPackProjectionDispatcher
{
    /// <summary>
    /// Reconciles installed packs for <paramref name="tenant"/>: retracts runtime content owned by
    /// Inactive packs, then projects content owned by Active packs. Idempotent; safe on every lifecycle
    /// transition and at startup. Never throws for pack content — malformed items are logged and skipped.
    /// </summary>
    Task<PackSeedProjectionSummary> ProjectActivePacksAsync(
        PackProjectionAuthority authority,
        CancellationToken cancellationToken = default);

    object? IPackProjectionDispatcher.Project(
        PackProjectionAuthority authority,
        CancellationToken cancellationToken) =>
        ProjectActivePacksAsync(authority, cancellationToken).GetAwaiter().GetResult();
}

/// <summary>A count of what one <see cref="IPackSeedProjector.ProjectActivePacksAsync"/> pass did.</summary>
/// <param name="AssetTypesSeeded">Asset-type seeds newly registered this pass.</param>
/// <param name="AssetTypesAlreadyPresent">Asset-type ids already seeded (idempotent skips).</param>
/// <param name="AssetTypesSkippedInvalid">Asset-type items skipped because they were malformed/invalid.</param>
/// <param name="FormDefinitionsDeferred">FormDefinition items deferred because form stores are not composed.</param>
/// <param name="TemplatesPublished">TemplateDefinition items (#111) published into the document-template registry.</param>
/// <param name="TemplatesSkippedInvalid">TemplateDefinition items skipped because they were malformed/invalid.</param>
/// <param name="OtherKindsSkipped">Legacy counter retained for wire compatibility; unsupported kinds are
/// now refusals and do not increment it.</param>
/// <param name="AssetTypesOwnedByOtherPack">Contested asset-type keys skipped because another pack is the
/// RESOLVED owner (dependency chain / recorded choice) — the non-owner defers to the owner (F4).</param>
/// <param name="AssetTypesContestedUnresolved">Contested asset-type keys skipped because no owner is
/// resolved yet — the fail-closed refusal-to-project (defensive: the activate guard normally prevents an
/// active pack from reaching this state).</param>
/// <param name="FormDefinitionsPublished">Form revisions newly published, including recovered drafts.</param>
/// <param name="FormDefinitionsAlreadyPresent">Matching pinned form revisions already published.</param>
/// <param name="FormDefinitionsSkippedInvalid">Malformed, admission-rejected, or mismatched form items.</param>
/// <param name="FormDefinitionsOwnedByOtherPack">Contested form keys owned by another pack.</param>
/// <param name="FormDefinitionsContestedUnresolved">Contested form keys refused without an owner.</param>
/// <param name="AssetTypesRetracted">Pack asset-type seeds hidden because their owning pack is Inactive.</param>
/// <param name="FormDefinitionsRetracted">Pack form revisions withdrawn because their owning pack is Inactive.</param>
/// <param name="WorkflowDefinitionsRetracted">Pack workflow revisions withdrawn because their owner is Inactive.</param>
public sealed record PackSeedProjectionSummary(
    int AssetTypesSeeded,
    int AssetTypesAlreadyPresent,
    int AssetTypesSkippedInvalid,
    int FormDefinitionsDeferred,
    int TemplatesPublished,
    int TemplatesSkippedInvalid,
    int OtherKindsSkipped,
    int AssetTypesOwnedByOtherPack = 0,
    int AssetTypesContestedUnresolved = 0,
    int FormDefinitionsPublished = 0,
    int FormDefinitionsAlreadyPresent = 0,
    int FormDefinitionsSkippedInvalid = 0,
    int FormDefinitionsOwnedByOtherPack = 0,
    int FormDefinitionsContestedUnresolved = 0,
    int WorkflowDefinitionsDeferred = 0,
    int WorkflowDefinitionsPublished = 0,
    int WorkflowDefinitionsAlreadyPresent = 0,
    int WorkflowDefinitionsSkippedInvalid = 0,
    int WorkflowDefinitionsOwnedByOtherPack = 0,
    int WorkflowDefinitionsContestedUnresolved = 0,
    int AssetTypesRetracted = 0,
    int FormDefinitionsRetracted = 0,
    int WorkflowDefinitionsRetracted = 0) : IPackProjectionRefusalReport
{
    /// <inheritdoc />
    /// <remarks>Any content-grain refusal leaves the admission incomplete, so the next boot re-runs the
    /// pass. Pack-grain platform refusals are a steady state the host reacts to elsewhere, not a
    /// half-applied projection, so they do not hold the admission open.</remarks>
    public bool ProjectionRefused => Refusals.Count > 0;

    /// <summary>Stable, locale-independent refusal codes produced by this pass.</summary>
    public IReadOnlyList<PackSeedProjectionRefusal> Refusals { get; init; }
        = Array.Empty<PackSeedProjectionRefusal>();

    /// <summary>
    /// Definitions REMOVED this pass, counted per content kind (L633): deactivation retractions plus the
    /// replaced package's definitions a replacement no longer declares. The three legacy
    /// <c>*Retracted</c> counters above stay wire-compatible; this is the whole picture, including the
    /// kinds that have no counter of their own.
    /// </summary>
    public IReadOnlyDictionary<PackContentKind, int> RetractedByKind { get; init; }
        = new Dictionary<PackContentKind, int>();

    /// <summary>
    /// PACK-grain platform-compatibility refusals (ticket 160): an ACTIVE pack whose declared platform
    /// requirements the RUNNING build cannot satisfy is refused — skipped whole, never projected — with
    /// the pack, the unmet capability, and the declared window surfaced structurally. Empty when the
    /// projector runs without an <see cref="IPackPlatformCompatibility"/> (back-compat embedders).
    /// </summary>
    public IReadOnlyList<PackPlatformProjectionRefusal> PlatformRefusals { get; init; }
        = Array.Empty<PackPlatformProjectionRefusal>();
}

/// <summary>A content-grain projection refusal suitable for localization at the caller.</summary>
public sealed record PackSeedProjectionRefusal(
    string ContentKey,
    PackContentKind ContentKind,
    string Code,
    string Pointer = "/");

/// <summary>Stable projection-refusal codes. Existing reason strings are the published codes.</summary>
public static class PackSeedProjectionRefusalCodes
{
    public const string PlatformIncompatible = PackPlatformProjectionRefusal.Code;
    public const string UnsupportedContentKind = PackSeedProjector.UnsupportedContentKindCode;
    public const string AccessDefinitionRefused = PackSeedProjector.AccessProjectionRefusedCode;
    public const string AccessSeamNotWired = PackSeedProjector.AccessProjectionNotWiredCode;
    public const string TemplateContentKeyConflict = PackSeedProjector.TemplateContentKeyConflictCode;
    public const string FormContentKeyConflict = PackSeedProjector.FormContentKeyConflictCode;
    public const string WorkflowContentKeyConflict = PackSeedProjector.WorkflowContentKeyConflictCode;
    public const string TaxonomyMalformed = PackSeedProjector.TaxonomyMalformedCode;
    public const string TaxonomyAuthoritativeRequiresVendorPack = PackSeedProjector.TaxonomyAuthoritativeRequiresVendorPackCode;
    public const string TaxonomyTenantRegimeRequiresTenantPack = PackSeedProjector.TaxonomyTenantRegimeRequiresTenantPackCode;
    public const string ReportDefinitionMalformed = PackSeedProjector.ReportDefinitionMalformedCode;
    public const string ReportDefinitionAuthoritativeRequiresVendorPack = PackSeedProjector.ReportDefinitionAuthoritativeRequiresVendorPackCode;
    public const string ReportDefinitionTenantRegimeRequiresTenantPack = PackSeedProjector.ReportDefinitionTenantRegimeRequiresTenantPackCode;
    public const string DataExchangeDefinitionMalformed = PackSeedProjector.DataExchangeDefinitionMalformedCode;
    public const string DataExchangeDefinitionAuthoritativeRequiresVendorPack = PackSeedProjector.DataExchangeDefinitionAuthoritativeRequiresVendorPackCode;
    public const string DataExchangeDefinitionTenantRegimeRequiresTenantPack = PackSeedProjector.DataExchangeDefinitionTenantRegimeRequiresTenantPackCode;
    public const string ScheduleDefinitionMalformed = PackSeedProjector.ScheduleDefinitionMalformedCode;
    public const string ScheduleDefinitionAuthoritativeRequiresVendorPack = PackSeedProjector.ScheduleDefinitionAuthoritativeRequiresVendorPackCode;
    public const string ScheduleDefinitionTenantRegimeRequiresTenantPack = PackSeedProjector.ScheduleDefinitionTenantRegimeRequiresTenantPackCode;
    public const string ViewDefinitionMalformed = PackSeedProjector.ViewDefinitionMalformedCode;
    public const string ViewDefinitionAuthoritativeRequiresVendorPack = PackSeedProjector.ViewDefinitionAuthoritativeRequiresVendorPackCode;
    public const string ViewDefinitionTenantRegimeRequiresTenantPack = PackSeedProjector.ViewDefinitionTenantRegimeRequiresTenantPackCode;
    public const string StandingRuleDefinitionMalformed = PackSeedProjector.StandingRuleDefinitionMalformedCode;
    public const string DefinitionRetractionFailedCode = PackSeedProjector.DefinitionRetractionFailedCode;
    public const string TemplateProjectionFailedCode = PackSeedProjector.TemplateProjectionFailedCode;
    public const string FormProjectionFailedCode = PackSeedProjector.FormProjectionFailedCode;
    public const string WorkflowProjectionFailedCode = PackSeedProjector.WorkflowProjectionFailedCode;
    public const string TemplateMalformedCode = PackSeedProjector.TemplateMalformedCode;
    public const string TemplateKeyMismatchCode = PackSeedProjector.TemplateKeyMismatchCode;
    public const string TemplateVersionMismatchCode = PackSeedProjector.TemplateVersionMismatchCode;
    public const string TemplatePinnedTupleConflictCode = PackSeedProjector.TemplatePinnedTupleConflictCode;
    public const string FormMalformedCode = PackSeedProjector.FormMalformedCode;
    public const string FormPinnedTupleConflictCode = PackSeedProjector.FormPinnedTupleConflictCode;
    public const string FormRetractionFailedCode = PackSeedProjector.FormRetractionFailedCode;
    public const string WorkflowMalformedCode = PackSeedProjector.WorkflowMalformedCode;
    public const string WorkflowPinnedTupleConflictCode = PackSeedProjector.WorkflowPinnedTupleConflictCode;
    public const string WorkflowRetractionFailedCode = PackSeedProjector.WorkflowRetractionFailedCode;
    public const string AssetRetractionFailedCode = PackSeedProjector.AssetRetractionFailedCode;
    public const string TaxonomyRegistryNotWiredCode = PackSeedProjector.TaxonomyRegistryNotWiredCode;
    public const string TaxonomyPinnedTupleConflictCode = PackSeedProjector.TaxonomyPinnedTupleConflictCode;
    public const string TaxonomyProjectionFailedCode = PackSeedProjector.TaxonomyProjectionFailedCode;
    public const string ReportDefinitionRegistryNotWiredCode = PackSeedProjector.ReportDefinitionRegistryNotWiredCode;
    public const string ReportDefinitionPinnedTupleConflictCode = PackSeedProjector.ReportDefinitionPinnedTupleConflictCode;
    public const string ReportDefinitionProjectionFailedCode = PackSeedProjector.ReportDefinitionProjectionFailedCode;
    public const string DataExchangeDefinitionRegistryNotWiredCode = PackSeedProjector.DataExchangeDefinitionRegistryNotWiredCode;
    public const string DataExchangeDefinitionPinnedTupleConflictCode = PackSeedProjector.DataExchangeDefinitionPinnedTupleConflictCode;
    public const string DataExchangeDefinitionProjectionFailedCode = PackSeedProjector.DataExchangeDefinitionProjectionFailedCode;
    public const string ScheduleDefinitionRegistryNotWiredCode = PackSeedProjector.ScheduleDefinitionRegistryNotWiredCode;
    public const string ScheduleDefinitionPinnedTupleConflictCode = PackSeedProjector.ScheduleDefinitionPinnedTupleConflictCode;
    public const string ScheduleDefinitionProjectionFailedCode = PackSeedProjector.ScheduleDefinitionProjectionFailedCode;
    public const string ViewDefinitionRegistryNotWiredCode = PackSeedProjector.ViewDefinitionRegistryNotWiredCode;
    public const string ViewDefinitionPinnedTupleConflictCode = PackSeedProjector.ViewDefinitionPinnedTupleConflictCode;
    public const string ViewDefinitionProjectionFailedCode = PackSeedProjector.ViewDefinitionProjectionFailedCode;
    public const string StandingRuleDefinitionStoreNotWiredCode = PackSeedProjector.StandingRuleDefinitionStoreNotWiredCode;
    public const string StandingRuleDefinitionPinnedTupleConflictCode = PackSeedProjector.StandingRuleDefinitionPinnedTupleConflictCode;
    public const string GrantInstanceRefusedCode = PackAuthorizationContentAdmission.GrantInstanceRefusedCode;
    public const string RoleDefinitionMalformedCode = PackAuthorizationContentAdmission.RoleDefinitionMalformedCode;
    public const string CapabilityBindingMalformedCode = PackAuthorizationContentAdmission.CapabilityBindingMalformedCode;
}

/// <summary>
/// One ACTIVE pack refused by a projection pass because the running platform is outside its declared
/// compatibility window (ticket 160). <see cref="Unmet"/> carries each failed requirement (capability +
/// declared minimum platform version + declarer); <see cref="PlatformVersion"/> is the running build.
/// </summary>
public sealed record PackPlatformProjectionRefusal(
    string PackKey,
    string Version,
    string PlatformVersion,
    IReadOnlyList<PackUnmetPlatformRequirement> Unmet,
    string Pointer = "/")
{
    /// <summary>The stable refusal code for this pack-grain refusal (family-consistent with
    /// <c>pack.projection.unsupported_content_kind</c> — projection codes carry no <c>.refused.</c>
    /// segment; the <c>pack.install.refused.*</c> family does).</summary>
    public const string Code = "pack.projection.platform_incompatible";
}

/// <inheritdoc />
internal sealed class PackSeedProjector : IPackSeedProjector
{
    /// <summary>
    /// The projection cases registered by this build. Platform capability claims derive from this runtime
    /// registration, so adding a pillar enum member cannot claim support before its projector lands.
    /// </summary>
    public static IReadOnlyList<PackProjectorCase> RegisteredCases { get; } =
    [
        new(PackContentKind.AssetTypeDefinition, ["assets.registry"]),
        new(PackContentKind.FormDefinition, ["forms.dynamic"]),
        new(PackContentKind.WorkflowDefinition, ["workflow.durable"]),
        new(PackContentKind.NavWorkspaceConfig, Array.Empty<string>()),
        new(PackContentKind.TemplateDefinition, Array.Empty<string>()),
        new(PackContentKind.TaxonomyDefinition, Array.Empty<string>()),
        new(PackContentKind.ReportDefinition, Array.Empty<string>()),
        new(PackContentKind.DataExchangeDefinition, Array.Empty<string>()),
        new(PackContentKind.ScheduleDefinition, Array.Empty<string>()),
        new(PackContentKind.ViewDefinition, Array.Empty<string>()),
        new(PackContentKind.StandingRuleDefinition, Array.Empty<string>()),
        new(PackContentKind.RoleDefinition, Array.Empty<string>()),
        new(PackContentKind.AuthorizationCapabilityBinding, Array.Empty<string>()),
    ];

    private readonly IPackInstallStore _store;
    private readonly IEntityTypeRegistry _types;
    private readonly ILogger<PackSeedProjector> _logger;
    private readonly IDocumentTemplateRegistry? _templates;
    private readonly IPackContentEdgeIndexProvider? _edgeIndex;
    private readonly IFormDefinitionStore? _forms;
    private readonly AuthorizedFormDefinitionLifecycle? _authorizedForms;
    private readonly ISchemaRegistry? _schemas;
    private readonly IWorkflowDefinitionStore? _workflows;
    private readonly AuthorizedWorkflowDefinitionLifecycle? _authorizedWorkflows;
    private readonly ITaxonomyRegistry? _taxonomies;
    private readonly IReportDefinitionRegistry? _reportDefinitions;
    private readonly IDataExchangeDefinitionRegistry? _dataExchangeDefinitions;
    private readonly IScheduleDefinitionRegistry? _scheduleDefinitions;
    private readonly IViewDefinitionRegistry? _viewDefinitions;
    private readonly IStandingRuleDefinitionStore? _standingRules;
    private readonly IRoleVocabularyStore? _roleVocabulary;
    private readonly AuthorizationDefinitionWriter? _authorizationDefinitions;
    private readonly IPackPlatformCompatibility? _platform;
    private readonly TimeProvider _time;
    private static readonly ConcurrentDictionary<Guid, byte> ConsumedAuthorities = new();

    /// <summary>
    /// Constructs the projector over the pack install store + runtime registries. The form and schema stores
    /// are optional for back-compatible embedders; when absent, FormDefinition items are explicitly deferred.
    /// The document-template registry (#111) and app-layer feature-graph content-edge-index provider (G1)
    /// are also optional. The taxonomy registry is optional for back-compatible embedders; when absent,
    /// TaxonomyDefinition items are explicitly refused. The report-definition registry is likewise optional;
    /// when absent, ReportDefinition items are explicitly refused (fail-closed, never skipped). The
    /// data-exchange-definition registry is likewise optional; when absent, DataExchangeDefinition
    /// items are explicitly refused (fail-closed, never skipped). The
    /// schedule-definition registry is likewise optional; when absent, ScheduleDefinition items are
    /// explicitly refused (fail-closed, never skipped). The
    /// view-definition registry is likewise optional; when absent, ViewDefinition items are explicitly
    /// refused (fail-closed, never skipped). An absent template registry defers a pack
    /// <c>TemplateDefinition</c> (never fail-open-skipped); an absent edge-index
    /// provider simply omits the
    /// graph cache-warm (the read-model self-heals on read anyway). When the node wires the edge-index, a
    /// projection pass rebuilds it as a side-effect (design note §2.2 — "the seed projector emits a
    /// rebuildable content-edge-index as it projects").
    /// </summary>
    public PackSeedProjector(
        IPackInstallStore store,
        IEntityTypeRegistry types,
        ILogger<PackSeedProjector> logger,
        IDocumentTemplateRegistry? templates = null,
        IPackContentEdgeIndexProvider? edgeIndex = null,
        IFormDefinitionStore? forms = null,
        ISchemaRegistry? schemas = null,
        IWorkflowDefinitionStore? workflows = null,
        TimeProvider? time = null,
        ITaxonomyRegistry? taxonomies = null,
        IReportDefinitionRegistry? reportDefinitions = null,
        IViewDefinitionRegistry? viewDefinitions = null,
        IScheduleDefinitionRegistry? scheduleDefinitions = null,
        IDataExchangeDefinitionRegistry? dataExchangeDefinitions = null,
        IPackPlatformCompatibility? platform = null,
        IStandingRuleDefinitionStore? standingRules = null,
        AuthorizedFormDefinitionLifecycle? authorizedForms = null,
        AuthorizedWorkflowDefinitionLifecycle? authorizedWorkflows = null,
        IRoleVocabularyStore? roleVocabulary = null,
        AuthorizationDefinitionWriter? authorizationDefinitions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _templates = templates;
        _edgeIndex = edgeIndex;
        _forms = forms;
        _authorizedForms = authorizedForms;
        _schemas = schemas;
        _workflows = workflows;
        _authorizedWorkflows = authorizedWorkflows;
        _taxonomies = taxonomies;
        _reportDefinitions = reportDefinitions;
        _dataExchangeDefinitions = dataExchangeDefinitions;
        _scheduleDefinitions = scheduleDefinitions;
        _viewDefinitions = viewDefinitions;
        _standingRules = standingRules;
        // (L675) Absent, RoleDefinition and AuthorizationCapabilityBinding items are explicitly
        // REFUSED (fail-closed, never skipped) — the same posture every other optional registry takes.
        _roleVocabulary = roleVocabulary;
        _authorizationDefinitions = authorizationDefinitions;
        // Ticket 160: when the host wires platform-compatibility facts, every projection pass (boot
        // re-projection included) re-runs the SAME requirement check install/activate use. Null keeps
        // back-compat embedders (and pre-160 tests) on the unchecked path — the OPPOSITE default from
        // the installer seam, which turns a null platform into PackPlatformCompatibility.Empty and so
        // fails CLOSED on any declared requirement. The projector cannot share that posture (Empty
        // would refuse every already-admitted pack on every back-compat embedder), so the forgotten
        // wiring must at least be VISIBLE: one prominent warning at construction.
        _platform = platform;
        if (_platform is null)
        {
            _logger.LogWarning(
                "PackSeedProjector constructed WITHOUT platform-compatibility facts "
                + "(IPackPlatformCompatibility is null) — ticket 160 per-pass compatibility re-checks are "
                + "OFF for every projection pass. If this embedder installs packs that declare platform "
                + "requirements, wire the platform so an out-of-window ACTIVE pack is refused instead of "
                + "silently projected.");
        }
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<PackSeedProjectionSummary> ProjectActivePacksAsync(
        PackProjectionAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureUsable();
        if (!ConsumedAuthorities.TryAdd(authority.Nonce, 0))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.Replayed);
        var tenant = authority.Tenant;
        var seeded = 0;
        var present = 0;
        var invalid = 0;
        var formsDeferred = 0;
        var templatesPublished = 0;
        var templatesInvalid = 0;
        var otherSkipped = 0;
        var ownedByOther = 0;
        var contestedUnresolved = 0;
        var formsPublished = 0;
        var formsPresent = 0;
        var formsInvalid = 0;
        var formsOwnedByOther = 0;
        var formsContestedUnresolved = 0;
        var workflowsDeferred = 0;
        var workflowsPublished = 0;
        var workflowsPresent = 0;
        var workflowsInvalid = 0;
        var workflowsOwnedByOther = 0;
        var workflowsContestedUnresolved = 0;
        var assetsRetracted = 0;
        var formsRetracted = 0;
        var workflowsRetracted = 0;
        var refusals = new List<PackSeedProjectionRefusal>();
        var platformRefusals = new List<PackPlatformProjectionRefusal>();

        var installed = _store.ListInstalled(tenant);

        // (L624) The ordinary overlay pipeline, read once per pass: the administrator's NARROWING of a
        // content key is an RFC-7396 tenant override row, and applying it here — before any kind's own
        // parse and admission — is the whole of it. Nothing downstream learns a new word: the narrowed
        // body passes exactly the admission the seed body passes, so a narrowing that would somehow
        // widen past a publisher ceiling is still refused there. Reading the rows on EVERY pass is what
        // makes the narrowing survive replay, and the S-10 re-attach (PackReattachPlanner, run inside
        // the install transaction) is what carries the row onto the next version's content key.
        var overlays = _store.GetOverrides(tenant, authority.PackId)
            .ToDictionary(o => o.ContentKey, o => o.OverlayPatch, StringComparer.Ordinal);
        PackSeedItem Overlaid(PackSeedItem item)
        {
            if (!overlays.TryGetValue(item.Key, out var patch))
            {
                return item;
            }

            var seed = item.ParseContent();
            var narrowed = Harborline.Api.Foundation.Catalog.Templates.TemplateMerger.ApplyMergePatch(seed, patch);
            // A patch that changes NOTHING is not a narrowing and must not mint a tuple: it is how a
            // tenant withdraws one (RFC-7396's identity patch, '{}'), and the unnarrowed body then
            // returns at its own seed version through the ordinary projection below.
            if (JsonNode.DeepEquals(narrowed, seed))
            {
                return item;
            }

            var json = narrowed?.ToJsonString() ?? "{}";
            // (L624, slice 4b) A form/workflow tuple is IMMUTABLE once published, so a narrowing
            // authored after the seed version published cannot rewrite it — it publishes the narrowed
            // body at a DERIVED version, exactly as the ordinary authoring route mints the next patch
            // version when a definition is re-saved. Deterministic in the override, so replay projects
            // the same tuple and is an exact no-op. Every other kind keeps the seed version: bindings
            // and roles are not versioned tuples, and no other kind is narrowed today.
            var version = item.Kind is PackContentKind.FormDefinition or PackContentKind.WorkflowDefinition
                ? PackNarrowedVersion.Derive(item.Version, patch)
                : item.Version;
            if (version != item.Version && narrowed is JsonObject narrowedObject)
            {
                // The form contract's own definition envelope declares the pinned tuple, and projection
                // refuses a body whose envelope disagrees with the tuple it is published at. (The
                // workflow contract's version is server-owned from the item, so it needs nothing here.)
                if (narrowedObject["definitionEnvelope"] is JsonObject envelope)
                {
                    envelope["version"] = version;
                }

                json = narrowedObject.ToJsonString();
            }

            // The content address is recomputed, never carried: an overlaid body is NOT the verified
            // seed body, and a row claiming the seed's CID would be a lie about what was signed.
            return item with
            {
                Version = version,
                CanonicalJson = json,
                ContentAddress = Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(json)),
            };
        }

        // (L624, slice 4b) The form/workflow addresses this pass fully admitted, and at which versions.
        // Populated only on Published/AlreadyPresent, so the retraction below can never run ahead of an
        // admission. Empty when the tenant has no overrides — the ordinary pass costs nothing.
        var admittedDefinitions = new List<(PackContentKind Kind, string Key, string Version)>();

        // Ticket 160: platform-compatibility refusals are decided FIRST, so a refused ACTIVE pack is
        // out of the pass ENTIRELY — it neither projects (below) nor participates in the
        // collision/ownership map: a refused owner's claim would otherwise suppress a compatible
        // sibling's copy of a shared key (OwnedByOtherPack) and the key would go live for NOBODY.
        var platformRefused = new HashSet<InstalledPack>();
        if (_platform is not null)
        {
            foreach (var pack in installed.Where(p => p.Lifecycle == PackLifecycleState.Active))
            {
                var unmetPlatform = PackPlatformRequirementCheck.FindUnmet(pack, _platform);
                if (unmetPlatform.Count == 0)
                {
                    continue;
                }

                // An out-of-window pack is REFUSED: skipped whole, loudly, with the pack + reason +
                // declared window surfaced structurally — never silently degraded at runtime. Refuse
                // the PACK, not the process. (The startup hosted service surfaces only the COUNT — this
                // is the single per-refusal detail log site.)
                var first = unmetPlatform[0];
                _logger.LogError(
                    "PackSeedProjector: REFUSED projecting ACTIVE pack {Pack} v{Version} — platform "
                    + "compatibility window unmet ({Code}): capability '{Capability}' declared by "
                    + "'{DeclaredBy}' is {Failure} (declared minimum platform "
                    + "{MinimumPlatformVersion}; running {PlatformVersion}). Nothing projects for this "
                    + "pack this pass: its in-memory registry content stays absent, but durably persisted "
                    + "content from earlier in-window projections (e.g. published form definitions) "
                    + "remains published until retracted. Resolve the compatibility gap or deactivate "
                    + "the pack.",
                    pack.PackKey, pack.Version, PackPlatformProjectionRefusal.Code,
                    first.Capability, first.DeclaredBy, first.Failure,
                    first.MinimumPlatformVersion ?? "(none declared)", _platform.PlatformVersion);
                platformRefusals.Add(new PackPlatformProjectionRefusal(
                    pack.PackKey, pack.Version, _platform.PlatformVersion, unmetPlatform, "/"));
                platformRefused.Add(pack);
            }
        }

        // (ADR 0129 D4/D5 — F4) Resolve cross-pack same-key collisions ONCE for this pass, over ALL
        // installed packs (Draft + Active) so a Draft owner still suppresses an active non-owner — but
        // EXCLUDING platform-refused active packs (above). A contested key projects ONLY for its
        // resolved owner; an unresolved contested key projects for NOBODY (fail-closed) — replacing the
        // old silent first-wins skip.
        var collisions = PackCompositionConflicts.Detect(
                PackCompositionConflicts.ClaimsFromInstalled(installed.Where(p => !platformRefused.Contains(p))),
                _store.GetKeyOwnership(tenant))
            .ToDictionary(c => c.ContentKey, StringComparer.Ordinal);

        // Role names retract LAST — the exact reverse of the projection order below, which puts them
        // FIRST. Withdrawing a role-gated form or workflow re-runs the role-gate admission, so a role
        // name removed ahead of the definitions gated on it makes their withdrawal unresolvable and
        // leaves the replaced package's content PUBLISHED. (Found by 208 s7b: the Access pack's own
        // submitter role.)
        static IEnumerable<PackSeedItem> RetractionOrder(IEnumerable<PackSeedItem> items) =>
            items.OrderBy(i => i.Kind == PackContentKind.RoleDefinition ? 1 : 0);

        // (L633) One item's reverse projection, shared by the two retraction loops below: every kind the
        // projector parses retracts, so a replacement can never leave the replaced package's copy live.
        var retractedByKind = new Dictionary<PackContentKind, int>();
        async Task RetractItemAsync(InstalledPack pack, PackSeedItem item)
        {
            var result = item.Kind switch
            {
                PackContentKind.AssetTypeDefinition => RetractAssetType(pack, item),
                PackContentKind.FormDefinition => await RetractFormDefinitionAsync(
                    tenant, pack, item, authority, cancellationToken).ConfigureAwait(false),
                PackContentKind.WorkflowDefinition => await RetractWorkflowDefinitionAsync(
                    tenant, pack, item, authority, cancellationToken).ConfigureAwait(false),
                PackContentKind.RoleDefinition or PackContentKind.AuthorizationCapabilityBinding =>
                    await RetractAccessItemAsync(pack, item, authority, cancellationToken)
                        .ConfigureAwait(false),
                _ => await RetractDefinitionAsync(tenant, pack, item, authority, cancellationToken)
                    .ConfigureAwait(false),
            };

            switch (result.Outcome)
            {
                case RetractionOutcome.Retracted:
                    retractedByKind[item.Kind] = retractedByKind.GetValueOrDefault(item.Kind) + 1;
                    if (item.Kind == PackContentKind.AssetTypeDefinition) { assetsRetracted++; }
                    else if (item.Kind == PackContentKind.FormDefinition) { formsRetracted++; }
                    else if (item.Kind == PackContentKind.WorkflowDefinition) { workflowsRetracted++; }
                    break;

                case RetractionOutcome.Invalid:
                    if (item.Kind == PackContentKind.FormDefinition) { formsInvalid++; }
                    else if (item.Kind == PackContentKind.WorkflowDefinition) { workflowsInvalid++; }
                    else { invalid++; }
                    refusals.Add(new PackSeedProjectionRefusal(
                        item.Key, item.Kind, result.RefusalCode ?? PackSeedProjectionRefusalCodes.DefinitionRetractionFailedCode,
                        ContentPointer(pack, item)));
                    break;
            }
        }

        foreach (var pack in installed.Where(p =>
                     p.Lifecycle == PackLifecycleState.Inactive
                     && p.PackKey == authority.PackId
                     && p.Version == authority.PackVersion))
        {
            foreach (var item in RetractionOrder(pack.SeedItems.Select(Overlaid)))
            {
                if (DecideContested(pack, item, collisions) != ContestedDecision.Project)
                {
                    continue;
                }

                await RetractItemAsync(pack, item).ConfigureAwait(false);
            }
        }

        foreach (var pack in installed.Where(p =>
                     p.Lifecycle == PackLifecycleState.Active
                     && p.PackKey == authority.PackId
                     && p.Version == authority.PackVersion))
        {
            // Ticket 160: platform-refused packs (decided in the pre-pass above, which also excluded
            // them from the collision map) are skipped whole — install-time admission is not enough
            // once the platform or the pack has moved.
            if (platformRefused.Contains(pack))
            {
                continue;
            }

            // (L675) BY SHAPE, before anything projects: a pack carrying a grant instance — under any
            // kind, at any depth — admits NOTHING and (refusals being non-empty) removes nothing either.
            // The kind half of the prohibition needs no code: PackContentKind has no grant member and
            // PackFileCodec refuses an undefined kind, so the shape is the only door left to close.
            var seedItems = pack.SeedItems.Select(Overlaid).ToArray();
            var smuggled = seedItems
                .Where(i => PackAuthorizationContentAdmission.IsGrantInstance(TryParseContent(i)))
                .ToArray();
            if (smuggled.Length > 0)
            {
                foreach (var item in smuggled)
                {
                    _logger.LogError(
                        "PackSeedProjector: REFUSED pack {Pack} v{Version} — item '{Key}' ({Kind}) is a "
                        + "grant instance ({Code}). A pack ships the default roles and the default "
                        + "bindings, never the grants. Nothing projects for this pack.",
                        pack.PackKey, pack.Version, item.Key, item.Kind,
                        PackAuthorizationContentAdmission.GrantInstanceRefusedCode);
                    refusals.Add(new PackSeedProjectionRefusal(
                        item.Key, item.Kind,
                        PackSeedProjectionRefusalCodes.GrantInstanceRefusedCode,
                        ContentPointer(pack, item)));
                }

                continue;
            }

            // Compiled system record types are catalogue authority, not portable pack content.
            // Refuse only the claiming items so an otherwise valid pack remains projectable.
            var sealedTypeClaims = seedItems
                .Where(PackSealedSystemTypeAdmission.ClaimsSealedSystemType)
                .ToArray();
            foreach (var item in sealedTypeClaims)
            {
                _logger.LogError(
                    "PackSeedProjector: REFUSED pack {Pack} v{Version} — item '{Key}' ({Kind}) claims a "
                    + "sealed system record type ({Code}). Compiled platform types are not pack content.",
                    pack.PackKey, pack.Version, item.Key, item.Kind,
                    PackSealedSystemTypeAdmission.RefusedCode);
                refusals.Add(new PackSeedProjectionRefusal(
                    item.Key, item.Kind, PackSealedSystemTypeAdmission.RefusedCode));
            }

            // Role names before the bindings that offer them: admission resolves every offered role
            // against the vocabulary, so a binding declared ahead of its role would be refused.
            foreach (var item in seedItems.Except(sealedTypeClaims).OrderBy(
                i => i.Kind == PackContentKind.RoleDefinition ? 0 : 1))
            {
                switch (item.Kind)
                {
                    case PackContentKind.TemplateDefinition:
                        switch (DecideContested(pack, item, collisions))
                        {
                            case ContestedDecision.Project:
                                var template = ProjectTemplate(tenant, pack, item);
                                switch (template.Outcome)
                                {
                                    case TemplateOutcome.Published: templatesPublished++; break;
                                    case TemplateOutcome.Invalid:
                                        templatesInvalid++;
                                        refusals.Add(new PackSeedProjectionRefusal(
                                            item.Key,
                                            item.Kind,
                                            template.RefusalCode ?? PackSeedProjectionRefusalCodes.TemplateProjectionFailedCode,
                                            ContentPointer(pack, item)));
                                        break;
                                    default:
                                        // Deferred or exact replay — recognized, but no new publication.
                                        break;
                                }

                                break;

                            case ContestedDecision.OwnedByOtherPack:
                                _logger.LogInformation(
                                    "PackSeedProjector: template '{Key}' from pack {Pack} v{Version} is a "
                                    + "cross-pack collision resolved to owner '{Owner}' — skipping the "
                                    + "non-owner's copy.",
                                    item.Key, pack.PackKey, pack.Version, collisions[item.Key].OwnerPackKey);
                                break;

                            default:
                                _logger.LogWarning(
                                    "PackSeedProjector: template '{Key}' is contested by packs [{Packs}] with "
                                    + "no resolved owner — refusing with code {Code}.",
                                    item.Key, string.Join(", ", collisions[item.Key].ClaimingPackKeys),
                                    TemplateContentKeyConflictCode);
                                templatesInvalid++;
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, PackSeedProjectionRefusalCodes.TemplateContentKeyConflict, ContentPointer(pack, item)));
                                break;
                        }

                        break;

                    case PackContentKind.AssetTypeDefinition:
                        switch (DecideContested(pack, item, collisions))
                        {
                            case ContestedDecision.Project:
                                switch (ProjectAssetType(pack, item))
                                {
                                    case AssetTypeOutcome.Seeded: seeded++; break;
                                    case AssetTypeOutcome.AlreadyPresent: present++; break;
                                    default: invalid++; break;
                                }

                                break;

                            case ContestedDecision.OwnedByOtherPack:
                                _logger.LogInformation(
                                    "PackSeedProjector: asset type '{Key}' from pack {Pack} v{Version} is a "
                                    + "cross-pack collision RESOLVED to owner '{Owner}' — skipping the non-owner's "
                                    + "copy (D8 per-key ownership).",
                                    item.Key, pack.PackKey, pack.Version,
                                    collisions[item.Key].OwnerPackKey);
                                ownedByOther++;
                                break;

                            default: // ContestedDecision.ContestedUnresolved
                                _logger.LogWarning(
                                    "PackSeedProjector: asset type '{Key}' is contested by packs [{Packs}] with NO "
                                    + "resolved owner — REFUSING to project it (fail-closed). Resolve ownership "
                                    + "(record a choice / declare a dependency) then re-activate.",
                                    item.Key, string.Join(", ", collisions[item.Key].ClaimingPackKeys));
                                contestedUnresolved++;
                                break;
                        }

                        break;

                    case PackContentKind.FormDefinition:
                        switch (DecideContested(pack, item, collisions))
                        {
                            case ContestedDecision.Project:
                                var form = await ProjectFormDefinitionAsync(
                                        tenant, pack, item, authority, cancellationToken)
                                    .ConfigureAwait(false);
                                switch (form.Outcome)
                                {
                                    case FormDefinitionOutcome.Published:
                                        formsPublished++;
                                        admittedDefinitions.Add((item.Kind, item.Key, item.Version));
                                        break;
                                    case FormDefinitionOutcome.AlreadyPresent:
                                        formsPresent++;
                                        admittedDefinitions.Add((item.Kind, item.Key, item.Version));
                                        break;
                                    case FormDefinitionOutcome.Deferred: formsDeferred++; break;
                                    default:
                                        formsInvalid++;
                                        refusals.Add(new PackSeedProjectionRefusal(
                                            item.Key,
                                            item.Kind,
                                            form.RefusalCode ?? PackSeedProjectionRefusalCodes.FormProjectionFailedCode,
                                            ContentPointer(pack, item)));
                                        break;
                                }

                                break;

                            case ContestedDecision.OwnedByOtherPack:
                                _logger.LogInformation(
                                    "PackSeedProjector: form '{Key}' from pack {Pack} v{Version} is a cross-pack "
                                    + "collision RESOLVED to owner '{Owner}' — skipping the non-owner's copy.",
                                    item.Key, pack.PackKey, pack.Version, collisions[item.Key].OwnerPackKey);
                                formsOwnedByOther++;
                                break;

                            default:
                                _logger.LogWarning(
                                    "PackSeedProjector: form '{Key}' is contested by packs [{Packs}] with NO "
                                    + "resolved owner — REFUSING with code {Code} (fail-closed).",
                                    item.Key, string.Join(", ", collisions[item.Key].ClaimingPackKeys),
                                    FormContentKeyConflictCode);
                                formsContestedUnresolved++;
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, PackSeedProjectionRefusalCodes.FormContentKeyConflict, ContentPointer(pack, item)));
                                break;
                        }

                        break;

                    case PackContentKind.WorkflowDefinition:
                        switch (DecideContested(pack, item, collisions))
                        {
                            case ContestedDecision.Project:
                                var workflow = await ProjectWorkflowDefinitionAsync(
                                        tenant, pack, item, authority, cancellationToken)
                                    .ConfigureAwait(false);
                                switch (workflow.Outcome)
                                {
                                    case WorkflowDefinitionOutcome.Published:
                                        workflowsPublished++;
                                        admittedDefinitions.Add((item.Kind, item.Key, item.Version));
                                        break;
                                    case WorkflowDefinitionOutcome.AlreadyPresent:
                                        workflowsPresent++;
                                        admittedDefinitions.Add((item.Kind, item.Key, item.Version));
                                        break;
                                    case WorkflowDefinitionOutcome.Deferred: workflowsDeferred++; break;
                                    default:
                                        workflowsInvalid++;
                                        refusals.Add(new PackSeedProjectionRefusal(
                                            item.Key,
                                            item.Kind,
                                            workflow.RefusalCode ?? PackSeedProjectionRefusalCodes.WorkflowProjectionFailedCode,
                                            ContentPointer(pack, item)));
                                        break;
                                }

                                break;

                            case ContestedDecision.OwnedByOtherPack:
                                _logger.LogInformation(
                                    "PackSeedProjector: workflow '{Key}' from pack {Pack} v{Version} is a "
                                    + "cross-pack collision RESOLVED to owner '{Owner}' — skipping the non-owner's "
                                    + "copy.",
                                    item.Key, pack.PackKey, pack.Version, collisions[item.Key].OwnerPackKey);
                                workflowsOwnedByOther++;
                                break;

                            default:
                                _logger.LogWarning(
                                    "PackSeedProjector: workflow '{Key}' is contested by packs [{Packs}] with NO "
                                    + "resolved owner — refusing with code {Code}.",
                                    item.Key, string.Join(", ", collisions[item.Key].ClaimingPackKeys),
                                    WorkflowContentKeyConflictCode);
                                workflowsContestedUnresolved++;
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, PackSeedProjectionRefusalCodes.WorkflowContentKeyConflict, ContentPointer(pack, item)));
                                break;
                        }

                        break;

                    case PackContentKind.TaxonomyDefinition:
                        if (!TryParseTaxonomy(item, out var taxonomy))
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.TaxonomyMalformed, ContentPointer(pack, item)));
                        }
                        else
                        {
                            var taxonomyRefusal = await ProjectTaxonomyDefinitionAsync(
                                    tenant, pack, taxonomy!, cancellationToken)
                                .ConfigureAwait(false);
                            if (taxonomyRefusal is not null)
                            {
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, taxonomyRefusal, ContentPointer(pack, item)));
                            }
                        }

                        break;

                    case PackContentKind.ReportDefinition:
                        if (!TryParseReportDefinition(item, out var reportDefinition))
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.ReportDefinitionMalformed, ContentPointer(pack, item)));
                        }
                        else
                        {
                            var reportDefinitionRefusal = await ProjectReportDefinitionAsync(
                                    tenant, pack, reportDefinition!, cancellationToken)
                                .ConfigureAwait(false);
                            if (reportDefinitionRefusal is not null)
                            {
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, reportDefinitionRefusal, ContentPointer(pack, item)));
                            }
                        }

                        break;

                    case PackContentKind.DataExchangeDefinition:
                        if (!TryParseDataExchangeDefinition(item, out var dataExchangeDefinition))
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.DataExchangeDefinitionMalformed, ContentPointer(pack, item)));
                        }
                        else
                        {
                            var dataExchangeDefinitionRefusal = await ProjectDataExchangeDefinitionAsync(
                                    tenant, pack, dataExchangeDefinition!, cancellationToken)
                                .ConfigureAwait(false);
                            if (dataExchangeDefinitionRefusal is not null)
                            {
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, dataExchangeDefinitionRefusal, ContentPointer(pack, item)));
                            }
                        }

                        break;

                    case PackContentKind.ScheduleDefinition:
                        if (!TryParseScheduleDefinition(item, out var scheduleDefinition))
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.ScheduleDefinitionMalformed, ContentPointer(pack, item)));
                        }
                        else
                        {
                            var scheduleDefinitionRefusal = await ProjectScheduleDefinitionAsync(
                                    tenant, pack, scheduleDefinition!, cancellationToken)
                                .ConfigureAwait(false);
                            if (scheduleDefinitionRefusal is not null)
                            {
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, scheduleDefinitionRefusal, ContentPointer(pack, item)));
                            }
                        }

                        break;

                    case PackContentKind.ViewDefinition:
                        if (!TryParseViewDefinition(item, out var viewDefinition))
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.ViewDefinitionMalformed, ContentPointer(pack, item)));
                        }
                        else
                        {
                            var viewDefinitionRefusal = await ProjectViewDefinitionAsync(
                                    tenant, pack, viewDefinition!, cancellationToken)
                                .ConfigureAwait(false);
                            if (viewDefinitionRefusal is not null)
                            {
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, viewDefinitionRefusal, ContentPointer(pack, item)));
                            }
                        }

                        break;

                    case PackContentKind.StandingRuleDefinition:
                        if (!TryParseStandingRuleDefinition(item, out var standingRule))
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.StandingRuleDefinitionMalformed, ContentPointer(pack, item)));
                        }
                        else if (_standingRules is null)
                        {
                            refusals.Add(new PackSeedProjectionRefusal(
                                item.Key, item.Kind, PackSeedProjectionRefusalCodes.StandingRuleDefinitionStoreNotWiredCode, ContentPointer(pack, item)));
                        }
                        else
                        {
                            try
                            {
                                await _standingRules.RegisterAsync(standingRule!, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (InvalidOperationException)
                            {
                                refusals.Add(new PackSeedProjectionRefusal(
                                    item.Key, item.Kind, PackSeedProjectionRefusalCodes.StandingRuleDefinitionPinnedTupleConflictCode, ContentPointer(pack, item)));
                            }
                        }

                        break;

                    case PackContentKind.RoleDefinition:
                    case PackContentKind.AuthorizationCapabilityBinding:
                        if (await ProjectAccessItemAsync(pack, item, authority, cancellationToken)
                                .ConfigureAwait(false) is { } accessRefusal)
                        {
                            refusals.Add(new PackSeedProjectionRefusal(item.Key, item.Kind, accessRefusal, ContentPointer(pack, item)));
                        }

                        break;

                    case PackContentKind.NavWorkspaceConfig:
                        // PackNavigationRoutes projects active nav seeds directly on every read; there is no
                        // mutable runtime registry for this reconciler to update.
                        break;

                    default:
                        _logger.LogWarning(
                            "PackSeedProjector: content kind {Kind} ('{Key}', pack {Pack} v{Version}) is not yet "
                            + "projected by this node — refusing with code {Code}.",
                            item.Kind, item.Key, pack.PackKey, pack.Version, UnsupportedContentKindCode);
                        refusals.Add(new PackSeedProjectionRefusal(
                            item.Key,
                            item.Kind,
                            PackSeedProjectionRefusalCodes.UnsupportedContentKind,
                            ContentPointer(pack, item)));
                        break;
                }
            }
        }

        // (L633) REPLACEMENT REMOVES — AFTER the admit loop above, and only when that loop refused
        // nothing. Activating a version supersedes the prior one: its immutable seed layer is retained
        // (S-2) but its runtime projections are NOT the replacement's. Every definition the replaced
        // version projected that no ACTIVE pack re-declares at the same pinned (key, version) is removed
        // here, inside the same projection admission. Ordering is the invariant: a refusal is a value, not
        // an exception, so a removal-first pass whose replacement is then refused would be recorded as a
        // COMPLETE admission with the replaced package's definitions already gone and nothing to repair
        // them. Admit first and skip removal on any refusal, and the replaced version stays exactly as it
        // was; the installer records that admission as refused, so the next boot's ReconcilePending re-runs
        // the whole pass. A re-declared tuple is left alone, which is what makes replacement by an
        // identical pack a no-op that keeps the same definition ids instead of a withdraw-and-republish.
        // Skipping every ACTIVE pack's tuples (not only the authority's) keeps a cross-pack co-declared
        // row that this pass just re-registered from being withdrawn behind the admit loop's back.
        // ...and only when the replacement is actually ACTIVE in this pass. A deactivate pass (the
        // preload service's own "activation reversed" call after a refused activation, or any
        // reconcile over a non-active version) refuses nothing and would otherwise reach this leg
        // with an empty admitted set and retract every definition of the superseded version.
        var replacementActive = installed.Any(p =>
            p.PackKey == authority.PackId
            && p.Version == authority.PackVersion
            && p.Lifecycle == PackLifecycleState.Active
            && !platformRefused.Contains(p));
        if (refusals.Count == 0 && replacementActive)
        {
            var admittedTuples = installed
                .Where(p => p.Lifecycle == PackLifecycleState.Active && !platformRefused.Contains(p))
                .SelectMany(p => p.SeedItems)
                // Overlaid, like every other enumeration in this pass: a narrowed item's admitted tuple
                // is its DERIVED version, and comparing the raw one here would retract the tuple the
                // admit loop just published.
                .Select(i => Overlaid(i))
                .Select(i => (i.Key, i.Version))
                .ToHashSet();
            foreach (var pack in installed.Where(p =>
                         p.Lifecycle == PackLifecycleState.Superseded
                         && p.PackKey == authority.PackId
                         && p.Version != authority.PackVersion))
            {
                foreach (var item in RetractionOrder(pack.SeedItems.Select(Overlaid)))
                {
                    if (admittedTuples.Contains((item.Key, item.Version)))
                    {
                        continue;
                    }

                    await RetractItemAsync(pack, item).ConfigureAwait(false);
                }
            }
        }

        // (L624, slice 4b) THE UNNARROWED TUPLE RETRACTS IN THE SAME PASS — after the admit loop, and
        // only when it refused nothing, on exactly the ordering rule the replacement-removal leg above
        // states. A narrowing publishes the body at a DERIVED version, so the seed version's tuple is
        // still live and would otherwise leave two published revisions of one address with the WIDER
        // one reachable. Withdrawing every OTHER pack-owned published revision of an address this pass
        // admitted is symmetric in every direction: authoring a narrowing retires the unnarrowed tuple,
        // changing it retires the previous derived tuple, and withdrawing it (an identity patch, which
        // Overlaid treats as no override at all) retires the derived tuple and leaves the seed version
        // live again. Pack identity, not pack version, owns these revisions: retire a predecessor
        // version's derived tuple even when the new seed only bumps its patch or stays unchanged.
        // This keeps a lifted derived patch from outranking the current seed after withdrawal.
        // Bounded to tenants that hold override rows; replay finds only the admitted tuple published.
        if (refusals.Count == 0 && overlays.Count > 0 && admittedDefinitions.Count > 0)
        {
            var kept = admittedDefinitions
                .ToLookup(d => (d.Kind, d.Key), d => d.Version);
            bool Superseded(PackContentKind kind, string key, string version) =>
                kept.Contains((kind, key))
                && !kept[(kind, key)].Contains(version, StringComparer.Ordinal);

            if (_forms is not null && _authorizedForms is not null)
            {
                var stale = new List<FormDefinition>();
                await foreach (var existing in _forms
                                   .ListByTenantAsync(tenant, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (existing.Status == FormDefinitionStatus.Published
                        && MatchesAuthoritySource(existing.PackSource, authority)
                        && Superseded(PackContentKind.FormDefinition, existing.Id.Value, existing.Version.ToString()))
                    {
                        stale.Add(existing);
                    }
                }

                foreach (var existing in stale)
                {
                    await _authorizedForms.WithdrawAsync(existing, authority, cancellationToken).ConfigureAwait(false);
                    formsRetracted++;
                    retractedByKind[PackContentKind.FormDefinition] =
                        retractedByKind.GetValueOrDefault(PackContentKind.FormDefinition) + 1;
                    _logger.LogInformation(
                        "PackSeedProjector: retracted form '{Key}' v{FormVersion} superseded by the tenant "
                        + "narrowing this pass published for pack {Pack} v{PackVersion}; authored content "
                        + "remains retained.",
                        existing.Id.Value, existing.Version, authority.PackId, authority.PackVersion);
                }
            }

            if (_workflows is not null && _authorizedWorkflows is not null)
            {
                var stale = new List<WorkflowDefinitionRecord>();
                await foreach (var existing in _workflows
                                   .ListByTenantAsync(tenant, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (existing.Status == WorkflowDefinitionStatus.Published
                        && MatchesAuthoritySource(existing.PackSource, authority)
                        && Superseded(PackContentKind.WorkflowDefinition, existing.Key, existing.Version))
                    {
                        stale.Add(existing);
                    }
                }

                foreach (var existing in stale)
                {
                    await _authorizedWorkflows
                        .WithdrawAsync(existing.Authored, authority, cancellationToken)
                        .ConfigureAwait(false);
                    workflowsRetracted++;
                    retractedByKind[PackContentKind.WorkflowDefinition] =
                        retractedByKind.GetValueOrDefault(PackContentKind.WorkflowDefinition) + 1;
                    _logger.LogInformation(
                        "PackSeedProjector: retracted workflow '{Key}' v{WorkflowVersion} superseded by the "
                        + "tenant narrowing this pass published for pack {Pack} v{PackVersion}; authored "
                        + "content remains retained.",
                        existing.Key, existing.Version, authority.PackId, authority.PackVersion);
                }
            }
        }

        if (seeded > 0 || invalid > 0 || formsDeferred > 0 || formsPublished > 0 || formsInvalid > 0
            || templatesPublished > 0 || templatesInvalid > 0 || ownedByOther > 0 || contestedUnresolved > 0
            || formsOwnedByOther > 0 || formsContestedUnresolved > 0 || workflowsDeferred > 0
            || workflowsPublished > 0 || workflowsInvalid > 0 || workflowsOwnedByOther > 0
            || workflowsContestedUnresolved > 0 || assetsRetracted > 0 || formsRetracted > 0
            || workflowsRetracted > 0)
        {
            _logger.LogInformation(
                "PackSeedProjector: projected tenant {Tenant} — {Seeded} asset type(s) seeded, {Present} already "
                + "present, {Invalid} skipped (invalid), {FormsPublished} form(s) published, "
                + "{FormsPresent} already present, {FormsInvalid} invalid, {FormsDeferred} deferred, {Templates} "
                + "template(s) published, {TemplatesInvalid} template(s) skipped (invalid), {Other} other-kind "
                + "item(s) skipped, {OwnedByOther} asset type(s) deferred to another pack, {Contested} asset "
                + "type(s) contested, {FormsOwnedByOther} form(s) deferred to another pack, "
                + "{FormsContested} form(s) contested, {WorkflowsPublished} workflow(s) published, "
                + "{WorkflowsPresent} already present, {WorkflowsInvalid} refused, {WorkflowsDeferred} deferred, "
                + "{WorkflowsOwnedByOther} workflow(s) deferred to another pack, {WorkflowsContested} contested, "
                + "{AssetsRetracted} asset type(s), {FormsRetracted} form(s), and {WorkflowsRetracted} workflow(s) "
                + "retracted.",
                tenant, seeded, present, invalid, formsPublished, formsPresent, formsInvalid, formsDeferred,
                templatesPublished, templatesInvalid, otherSkipped, ownedByOther, contestedUnresolved,
                formsOwnedByOther, formsContestedUnresolved, workflowsPublished, workflowsPresent,
                workflowsInvalid, workflowsDeferred, workflowsOwnedByOther, workflowsContestedUnresolved,
                assetsRetracted, formsRetracted, workflowsRetracted);
        }

        // G1 side-effect: rebuild the app-layer feature-graph content-edge-index off the fresh install
        // state, so a reader of GET /packs/graph gets a warm cache. Never fails a projection — the graph is
        // derived state and the read-model self-heals if this is skipped.
        try
        {
            _edgeIndex?.Rebuild(tenant);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: rebuilding the feature-graph content-edge-index for tenant {Tenant} "
                + "failed — the graph read-model will rebuild it lazily on next read.",
                tenant);
        }

        return new PackSeedProjectionSummary(
            seeded, present, invalid, formsDeferred, templatesPublished, templatesInvalid, otherSkipped,
            ownedByOther, contestedUnresolved, formsPublished, formsPresent, formsInvalid,
            formsOwnedByOther, formsContestedUnresolved, workflowsDeferred, workflowsPublished,
            workflowsPresent, workflowsInvalid, workflowsOwnedByOther, workflowsContestedUnresolved,
            assetsRetracted, formsRetracted, workflowsRetracted)
        {
            Refusals = refusals,
            PlatformRefusals = platformRefusals,
            RetractedByKind = retractedByKind,
        };
    }

    /// <summary>The active seed carries a content kind for which this node has no projector.</summary>
    public const string UnsupportedContentKindCode = "pack.projection.unsupported_content_kind";

    /// <summary>An authoritative taxonomy was carried by a tenant-authored pack.</summary>
    public const string TaxonomyAuthoritativeRequiresVendorPackCode =
        "pack.taxonomy.authoritative_requires_vendor_pack";

    /// <summary>A tenant-scoped taxonomy was carried by a vendor-authored pack.</summary>
    public const string TaxonomyTenantRegimeRequiresTenantPackCode =
        "pack.taxonomy.tenant_regime_requires_tenant_pack";

    /// <summary>A taxonomy body could not be parsed as a definition matching its pack coordinates.</summary>
    public const string TaxonomyMalformedCode = "pack.taxonomy.malformed";

    /// <summary>The taxonomy registry is absent, so recognized content cannot be projected.</summary>
    public const string TaxonomyRegistryNotWiredCode = "pack.taxonomy.registry_not_wired";

    /// <summary>The pinned taxonomy tuple is already occupied by different content.</summary>
    public const string TaxonomyPinnedTupleConflictCode = "pack.taxonomy.pinned_tuple_conflict";

    /// <summary>An unexpected taxonomy registry failure prevented projection.</summary>
    public const string TaxonomyProjectionFailedCode = "pack.taxonomy.projection_failed";

    /// <summary>An authoritative report definition was carried by a tenant-authored pack.</summary>
    public const string ReportDefinitionAuthoritativeRequiresVendorPackCode =
        "pack.report-definition.authoritative_requires_vendor_pack";

    /// <summary>A tenant-regime report definition was carried by a vendor-authored pack.</summary>
    public const string ReportDefinitionTenantRegimeRequiresTenantPackCode =
        "pack.report-definition.tenant_regime_requires_tenant_pack";

    /// <summary>A report-definition body could not be parsed or admitted as valid content.</summary>
    public const string ReportDefinitionMalformedCode = "pack.report-definition.malformed";

    /// <summary>The report-definition registry is absent, so recognized content cannot be projected.</summary>
    public const string ReportDefinitionRegistryNotWiredCode =
        "pack.report-definition.registry_not_wired";

    /// <summary>The pinned report-definition tuple is already occupied by different content.</summary>
    public const string ReportDefinitionPinnedTupleConflictCode =
        "pack.report-definition.pinned_tuple_conflict";

    /// <summary>An unexpected report-definition registry failure prevented projection.</summary>
    public const string ReportDefinitionProjectionFailedCode =
        "pack.report-definition.projection_failed";

    /// <summary>An authoritative data-exchange definition was carried by a tenant-authored pack.</summary>
    public const string DataExchangeDefinitionAuthoritativeRequiresVendorPackCode =
        "pack.data-exchange-definition.authoritative_requires_vendor_pack";

    /// <summary>A tenant-regime data-exchange definition was carried by a vendor-authored pack.</summary>
    public const string DataExchangeDefinitionTenantRegimeRequiresTenantPackCode =
        "pack.data-exchange-definition.tenant_regime_requires_tenant_pack";

    /// <summary>A data-exchange-definition body could not be parsed or admitted as valid content.</summary>
    public const string DataExchangeDefinitionMalformedCode =
        "pack.data-exchange-definition.malformed";

    /// <summary>The data-exchange-definition registry is absent, so recognized content cannot be projected.</summary>
    public const string DataExchangeDefinitionRegistryNotWiredCode =
        "pack.data-exchange-definition.registry_not_wired";

    /// <summary>The pinned data-exchange-definition tuple is already occupied by different content.</summary>
    public const string DataExchangeDefinitionPinnedTupleConflictCode =
        "pack.data-exchange-definition.pinned_tuple_conflict";

    /// <summary>An unexpected data-exchange-definition registry failure prevented projection.</summary>
    public const string DataExchangeDefinitionProjectionFailedCode =
        "pack.data-exchange-definition.projection_failed";

    /// <summary>An authoritative schedule definition was carried by a tenant-authored pack.</summary>
    public const string ScheduleDefinitionAuthoritativeRequiresVendorPackCode =
        "pack.schedule-definition.authoritative_requires_vendor_pack";

    /// <summary>A tenant-regime schedule definition was carried by a vendor-authored pack.</summary>
    public const string ScheduleDefinitionTenantRegimeRequiresTenantPackCode =
        "pack.schedule-definition.tenant_regime_requires_tenant_pack";

    /// <summary>A schedule-definition body could not be parsed or admitted as valid content.</summary>
    public const string ScheduleDefinitionMalformedCode = "pack.schedule-definition.malformed";

    /// <summary>The schedule-definition registry is absent, so recognized content cannot be projected.</summary>
    public const string ScheduleDefinitionRegistryNotWiredCode =
        "pack.schedule-definition.registry_not_wired";

    /// <summary>The pinned schedule-definition tuple is already occupied by different content.</summary>
    public const string ScheduleDefinitionPinnedTupleConflictCode =
        "pack.schedule-definition.pinned_tuple_conflict";

    /// <summary>An unexpected schedule-definition registry failure prevented projection.</summary>
    public const string ScheduleDefinitionProjectionFailedCode =
        "pack.schedule-definition.projection_failed";

    /// <summary>A standing-rule definition was malformed or disagreed with its pinned item tuple.</summary>
    public const string StandingRuleDefinitionMalformedCode = "pack.standing-rule-definition.malformed";

    /// <summary>The ordinary standing-rule definition store is absent.</summary>
    public const string StandingRuleDefinitionStoreNotWiredCode =
        "pack.standing-rule-definition.store_not_wired";

    /// <summary>The pinned standing-rule definition tuple already carries different content.</summary>
    public const string StandingRuleDefinitionPinnedTupleConflictCode =
        "pack.standing-rule-definition.pinned_tuple_conflict";

    private static bool TryParseStandingRuleDefinition(
        PackSeedItem item,
        out StandingRuleDefinition? definition)
    {
        try
        {
            definition = JsonSerializer.Deserialize<StandingRuleDefinition>(
                item.CanonicalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return definition is not null
                   && string.Equals(definition.RuleId, item.Key, StringComparison.Ordinal)
                   && string.Equals(definition.RuleVersion, item.Version, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or RuleCompilationException)
        {
            definition = null;
            return false;
        }
    }

    /// <summary>An authoritative view definition was carried by a tenant-authored pack.</summary>
    public const string ViewDefinitionAuthoritativeRequiresVendorPackCode =
        "pack.view-definition.authoritative_requires_vendor_pack";

    /// <summary>A tenant-regime view definition was carried by a vendor-authored pack.</summary>
    public const string ViewDefinitionTenantRegimeRequiresTenantPackCode =
        "pack.view-definition.tenant_regime_requires_tenant_pack";

    /// <summary>A view-definition body could not be parsed or admitted as valid content.</summary>
    public const string ViewDefinitionMalformedCode = "pack.view-definition.malformed";

    /// <summary>The view-definition registry is absent, so recognized content cannot be projected.</summary>
    public const string ViewDefinitionRegistryNotWiredCode =
        "pack.view-definition.registry_not_wired";

    /// <summary>The pinned view-definition tuple is already occupied by different content.</summary>
    public const string ViewDefinitionPinnedTupleConflictCode =
        "pack.view-definition.pinned_tuple_conflict";

    /// <summary>An unexpected view-definition registry failure prevented projection.</summary>
    public const string ViewDefinitionProjectionFailedCode =
        "pack.view-definition.projection_failed";

    private async Task<string?> ProjectTaxonomyDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        TaxonomyDefinition taxonomy,
        CancellationToken cancellationToken)
    {
        if (_taxonomies is null)
        {
            return PackSeedProjectionRefusalCodes.TaxonomyRegistryNotWiredCode;
        }

        if (taxonomy.Governance == TaxonomyGovernanceRegime.Authoritative)
        {
            if (pack.VouchingScope != TrustScope.HarborlineChannel)
            {
                return PackSeedProjectionRefusalCodes.TaxonomyAuthoritativeRequiresVendorPack;
            }
        }
        else if (pack.VouchingScope != TrustScope.OwnRoster)
        {
            return PackSeedProjectionRefusalCodes.TaxonomyTenantRegimeRequiresTenantPack;
        }

        try
        {
            var existing = await _taxonomies.GetDefinitionAsync(
                    tenant, taxonomy.Id, taxonomy.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return TaxonomiesAreEquivalent(existing, taxonomy)
                    ? null
                    : PackSeedProjectionRefusalCodes.TaxonomyPinnedTupleConflictCode;
            }

            await _taxonomies.CreateAsync(
                    tenant,
                    taxonomy.Id,
                    taxonomy.Version,
                    taxonomy.Governance,
                    taxonomy.Description,
                    taxonomy.Owner,
                    taxonomy.DerivedFrom,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (TaxonomyGovernanceException)
        {
            return PackSeedProjectionRefusalCodes.TaxonomyPinnedTupleConflictCode;
        }
        catch (TaxonomyConflictException)
        {
            return PackSeedProjectionRefusalCodes.TaxonomyPinnedTupleConflictCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PackSeedProjector: taxonomy '{Key}' v{TaxonomyVersion} from pack {Pack} v{PackVersion} "
                + "failed projection with code {Code}.",
                taxonomy.Id.Value,
                taxonomy.Version,
                pack.PackKey,
                pack.Version,
                TaxonomyProjectionFailedCode);
            return PackSeedProjectionRefusalCodes.TaxonomyProjectionFailedCode;
        }
    }

    private static bool TaxonomiesAreEquivalent(TaxonomyDefinition existing, TaxonomyDefinition expected)
        => existing.Id == expected.Id
           && existing.Version == expected.Version
           && existing.Governance == expected.Governance
           && existing.Description == expected.Description
           && existing.Owner == expected.Owner
           && existing.DerivedFrom == expected.DerivedFrom;

    private static bool TryParseTaxonomy(PackSeedItem item, out TaxonomyDefinition? taxonomy)
    {
        try
        {
            taxonomy = JsonSerializer.Deserialize<TaxonomyDefinition>(
                item.CanonicalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return taxonomy is not null
                   && taxonomy.Id.Value == item.Key
                   && taxonomy.Version.ToString() == item.Version;
        }
        catch (JsonException)
        {
            taxonomy = null;
            return false;
        }
    }

    private async Task<string?> ProjectReportDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        ReportDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_reportDefinitions is null)
        {
            return PackSeedProjectionRefusalCodes.ReportDefinitionRegistryNotWiredCode;
        }

        if (definition.Envelope.CascadeLayer != CascadeLayer.Tenant)
        {
            if (pack.VouchingScope != TrustScope.HarborlineChannel)
            {
                return PackSeedProjectionRefusalCodes.ReportDefinitionAuthoritativeRequiresVendorPack;
            }
        }
        else if (pack.VouchingScope != TrustScope.OwnRoster)
        {
            return PackSeedProjectionRefusalCodes.ReportDefinitionTenantRegimeRequiresTenantPack;
        }

        // Projection preserves the pack's exact version string: parsing pinned it to item.Version, and
        // tenant stamping carries that verbatim value downstream without minting, bumping, or normalizing it.
        var tenantScoped = definition with { Tenant = tenant.Value };

        try
        {
            var existing = await _reportDefinitions.GetDefinitionAsync(
                    tenant.Value, tenantScoped.Key, tenantScoped.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return ReportDefinitionsAreEquivalent(existing, tenantScoped)
                    ? null
                    : PackSeedProjectionRefusalCodes.ReportDefinitionPinnedTupleConflictCode;
            }

            await _reportDefinitions.RegisterAsync(tenantScoped, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ReportDefinitionGovernanceException ex)
        {
            // Tuple conflicts mirror taxonomy conflicts. Descriptor admission and all other governance
            // refusals describe defective carried content and therefore map to malformed.
            return StringComparer.Ordinal.Equals(
                    ex.ErrorCode,
                    "report_definition.pinned_tuple_conflict")
                ? PackSeedProjectionRefusalCodes.ReportDefinitionPinnedTupleConflictCode
                : PackSeedProjectionRefusalCodes.ReportDefinitionMalformed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PackSeedProjector: report definition '{Key}' v{ReportDefinitionVersion} from pack {Pack} "
                + "v{PackVersion} failed projection with code {Code}.",
                tenantScoped.Key,
                tenantScoped.Version,
                pack.PackKey,
                pack.Version,
                ReportDefinitionProjectionFailedCode);
            return PackSeedProjectionRefusalCodes.ReportDefinitionProjectionFailedCode;
        }
    }

    private static bool ReportDefinitionsAreEquivalent(
        ReportDefinition existing,
        ReportDefinition expected)
        => existing.SchemaVersion == expected.SchemaVersion
           && StringComparer.Ordinal.Equals(existing.Key, expected.Key)
           && StringComparer.Ordinal.Equals(existing.Version, expected.Version)
           && StringComparer.Ordinal.Equals(existing.ReportKind, expected.ReportKind)
           && StringComparer.Ordinal.Equals(existing.Title, expected.Title)
           && StringComparer.Ordinal.Equals(
               existing.Parameters.GetRawText(),
               expected.Parameters.GetRawText());

    private static bool TryParseReportDefinition(
        PackSeedItem item,
        out ReportDefinition? definition)
    {
        try
        {
            // Missing required members throw JsonException and follow the malformed-content path.
            definition = JsonSerializer.Deserialize<ReportDefinition>(
                item.CanonicalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return definition is not null
                   && definition.Key == item.Key
                   && definition.Version == item.Version;
        }
        catch (JsonException)
        {
            definition = null;
            return false;
        }
    }

    private async Task<string?> ProjectDataExchangeDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        DataExchangeDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_dataExchangeDefinitions is null)
        {
            return PackSeedProjectionRefusalCodes.DataExchangeDefinitionRegistryNotWiredCode;
        }

        if (definition.Envelope.CascadeLayer != CascadeLayer.Tenant)
        {
            if (pack.VouchingScope != TrustScope.HarborlineChannel)
            {
                return PackSeedProjectionRefusalCodes.DataExchangeDefinitionAuthoritativeRequiresVendorPack;
            }
        }
        else if (pack.VouchingScope != TrustScope.OwnRoster)
        {
            return PackSeedProjectionRefusalCodes.DataExchangeDefinitionTenantRegimeRequiresTenantPack;
        }

        // Projection preserves the pack's exact version string: parsing pinned it to item.Version, and
        // tenant stamping carries that verbatim value downstream without minting, bumping, or normalizing it.
        var tenantScoped = definition with { Tenant = tenant.Value };

        try
        {
            var existing = await _dataExchangeDefinitions.GetDefinitionAsync(
                    tenant.Value, tenantScoped.Key, tenantScoped.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return DataExchangeDefinitionsAreEquivalent(existing, tenantScoped)
                    ? null
                    : PackSeedProjectionRefusalCodes.DataExchangeDefinitionPinnedTupleConflictCode;
            }

            await _dataExchangeDefinitions.RegisterAsync(tenantScoped, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DataExchangeDefinitionGovernanceException ex)
        {
            // Tuple conflicts mirror taxonomy conflicts. Descriptor admission and all other governance
            // refusals describe defective carried content and therefore map to malformed.
            return StringComparer.Ordinal.Equals(
                    ex.ErrorCode,
                    "data_exchange_definition.pinned_tuple_conflict")
                ? PackSeedProjectionRefusalCodes.DataExchangeDefinitionPinnedTupleConflictCode
                : PackSeedProjectionRefusalCodes.DataExchangeDefinitionMalformed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PackSeedProjector: data-exchange definition '{Key}' v{DataExchangeDefinitionVersion} from pack {Pack} "
                + "v{PackVersion} failed projection with code {Code}.",
                tenantScoped.Key,
                tenantScoped.Version,
                pack.PackKey,
                pack.Version,
                DataExchangeDefinitionProjectionFailedCode);
            return PackSeedProjectionRefusalCodes.DataExchangeDefinitionProjectionFailedCode;
        }
    }

    private static bool DataExchangeDefinitionsAreEquivalent(
        DataExchangeDefinition existing,
        DataExchangeDefinition expected)
        => existing.SchemaVersion == expected.SchemaVersion
           && StringComparer.Ordinal.Equals(existing.Key, expected.Key)
           && StringComparer.Ordinal.Equals(existing.Version, expected.Version)
           && StringComparer.Ordinal.Equals(existing.ExchangeKind, expected.ExchangeKind)
           && StringComparer.Ordinal.Equals(existing.Title, expected.Title)
           && StringComparer.Ordinal.Equals(
               existing.Settings.GetRawText(),
               expected.Settings.GetRawText());

    private static bool TryParseDataExchangeDefinition(
        PackSeedItem item,
        out DataExchangeDefinition? definition)
    {
        try
        {
            // Missing required members throw JsonException and follow the malformed-content path.
            definition = JsonSerializer.Deserialize<DataExchangeDefinition>(
                item.CanonicalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return definition is not null
                   && definition.Key == item.Key
                   && definition.Version == item.Version;
        }
        catch (JsonException)
        {
            definition = null;
            return false;
        }
    }

    private async Task<string?> ProjectScheduleDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        ScheduleDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_scheduleDefinitions is null)
        {
            return PackSeedProjectionRefusalCodes.ScheduleDefinitionRegistryNotWiredCode;
        }

        if (definition.Envelope.CascadeLayer != CascadeLayer.Tenant)
        {
            if (pack.VouchingScope != TrustScope.HarborlineChannel)
            {
                return PackSeedProjectionRefusalCodes.ScheduleDefinitionAuthoritativeRequiresVendorPack;
            }
        }
        else if (pack.VouchingScope != TrustScope.OwnRoster)
        {
            return PackSeedProjectionRefusalCodes.ScheduleDefinitionTenantRegimeRequiresTenantPack;
        }

        // Projection preserves the pack's exact version string: parsing pinned it to item.Version, and
        // tenant stamping carries that verbatim value downstream without minting, bumping, or normalizing it.
        var tenantScoped = definition with { Tenant = tenant.Value };

        try
        {
            var existing = await _scheduleDefinitions.GetDefinitionAsync(
                    tenant.Value, tenantScoped.Key, tenantScoped.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return ScheduleDefinitionsAreEquivalent(existing, tenantScoped)
                    ? null
                    : PackSeedProjectionRefusalCodes.ScheduleDefinitionPinnedTupleConflictCode;
            }

            await _scheduleDefinitions.RegisterAsync(tenantScoped, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ScheduleDefinitionGovernanceException ex)
        {
            // Tuple conflicts mirror taxonomy conflicts. Descriptor admission and all other governance
            // refusals describe defective carried content and therefore map to malformed.
            return StringComparer.Ordinal.Equals(
                    ex.ErrorCode,
                    "schedule_definition.pinned_tuple_conflict")
                ? PackSeedProjectionRefusalCodes.ScheduleDefinitionPinnedTupleConflictCode
                : PackSeedProjectionRefusalCodes.ScheduleDefinitionMalformed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PackSeedProjector: schedule definition '{Key}' v{ScheduleDefinitionVersion} from pack {Pack} "
                + "v{PackVersion} failed projection with code {Code}.",
                tenantScoped.Key,
                tenantScoped.Version,
                pack.PackKey,
                pack.Version,
                ScheduleDefinitionProjectionFailedCode);
            return PackSeedProjectionRefusalCodes.ScheduleDefinitionProjectionFailedCode;
        }
    }

    private static bool ScheduleDefinitionsAreEquivalent(
        ScheduleDefinition existing,
        ScheduleDefinition expected)
        => existing.SchemaVersion == expected.SchemaVersion
           && StringComparer.Ordinal.Equals(existing.Key, expected.Key)
           && StringComparer.Ordinal.Equals(existing.Version, expected.Version)
           && StringComparer.Ordinal.Equals(existing.ScheduleKind, expected.ScheduleKind)
           && StringComparer.Ordinal.Equals(existing.Title, expected.Title)
           && StringComparer.Ordinal.Equals(
               existing.Body.GetRawText(),
               expected.Body.GetRawText());

    private static bool TryParseScheduleDefinition(
        PackSeedItem item,
        out ScheduleDefinition? scheduleDefinition)
    {
        try
        {
            // Missing required members throw JsonException and follow the malformed-content path.
            scheduleDefinition = JsonSerializer.Deserialize<ScheduleDefinition>(
                item.CanonicalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return scheduleDefinition is not null
                   && scheduleDefinition.Key == item.Key
                   && scheduleDefinition.Version == item.Version;
        }
        catch (JsonException)
        {
            scheduleDefinition = null;
            return false;
        }
    }

    private async Task<string?> ProjectViewDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        ViewDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_viewDefinitions is null)
        {
            return PackSeedProjectionRefusalCodes.ViewDefinitionRegistryNotWiredCode;
        }

        if (definition.Envelope.CascadeLayer != CascadeLayer.Tenant)
        {
            if (pack.VouchingScope != TrustScope.HarborlineChannel)
            {
                return PackSeedProjectionRefusalCodes.ViewDefinitionAuthoritativeRequiresVendorPack;
            }
        }
        else if (pack.VouchingScope != TrustScope.OwnRoster)
        {
            return PackSeedProjectionRefusalCodes.ViewDefinitionTenantRegimeRequiresTenantPack;
        }

        // Projection preserves the pack's exact version string: parsing pinned it to item.Version, and
        // tenant stamping carries that verbatim value downstream without minting, bumping, or normalizing it.
        var tenantScoped = definition with { Tenant = tenant.Value };

        try
        {
            var existing = await _viewDefinitions.GetDefinitionAsync(
                    tenant.Value, tenantScoped.Key, tenantScoped.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return ViewDefinitionsAreEquivalent(existing, tenantScoped)
                    ? null
                    : PackSeedProjectionRefusalCodes.ViewDefinitionPinnedTupleConflictCode;
            }

            await _viewDefinitions.RegisterAsync(tenantScoped, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ViewDefinitionGovernanceException ex)
        {
            // Tuple conflicts mirror taxonomy conflicts. Descriptor admission and all other governance
            // refusals describe defective carried content and therefore map to malformed.
            return StringComparer.Ordinal.Equals(
                    ex.ErrorCode,
                    "view_definition.pinned_tuple_conflict")
                ? PackSeedProjectionRefusalCodes.ViewDefinitionPinnedTupleConflictCode
                : PackSeedProjectionRefusalCodes.ViewDefinitionMalformed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PackSeedProjector: view definition '{Key}' v{ViewDefinitionVersion} from pack {Pack} "
                + "v{PackVersion} failed projection with code {Code}.",
                tenantScoped.Key,
                tenantScoped.Version,
                pack.PackKey,
                pack.Version,
                ViewDefinitionProjectionFailedCode);
            return PackSeedProjectionRefusalCodes.ViewDefinitionProjectionFailedCode;
        }
    }

    private static bool ViewDefinitionsAreEquivalent(
        ViewDefinition existing,
        ViewDefinition expected)
        => existing.SchemaVersion == expected.SchemaVersion
           && StringComparer.Ordinal.Equals(existing.Key, expected.Key)
           && StringComparer.Ordinal.Equals(existing.Version, expected.Version)
           && StringComparer.Ordinal.Equals(existing.ViewKind, expected.ViewKind)
           && StringComparer.Ordinal.Equals(existing.Title, expected.Title)
           && Equals(existing.ShapeRoles, expected.ShapeRoles)
           && StringComparer.Ordinal.Equals(
               existing.Parameters.GetRawText(),
               expected.Parameters.GetRawText());

    private static bool TryParseViewDefinition(
        PackSeedItem item,
        out ViewDefinition? definition)
    {
        try
        {
            // Missing required members throw JsonException and follow the malformed-content path.
            definition = JsonSerializer.Deserialize<ViewDefinition>(
                item.CanonicalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return definition is not null
                   && definition.Key == item.Key
                   && definition.Version == item.Version;
        }
        catch (JsonException)
        {
            definition = null;
            return false;
        }
    }

    /// <summary>A workflow item could not be parsed as the canonical authoring contract.</summary>
    public const string WorkflowMalformedCode = "pack.workflow.malformed";

    /// <summary>The pinned tuple already belongs to different/non-pack content.</summary>
    public const string WorkflowPinnedTupleConflictCode = "pack.workflow.pinned_tuple_conflict";

    /// <summary>Two packs claim the same workflow key without a recorded owner.</summary>
    public const string WorkflowContentKeyConflictCode = "pack.workflow.content_key_conflict";

    /// <summary>An unexpected durable-store failure prevented projection.</summary>
    public const string WorkflowProjectionFailedCode = "pack.workflow.projection_failed";

    /// <summary>An unexpected durable-store failure prevented reverse projection.</summary>
    public const string WorkflowRetractionFailedCode = "pack.workflow.retraction_failed";

    private async Task<RetractionResult> RetractWorkflowDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        if (_workflows is null || _authorizedWorkflows is null)
        {
            return new RetractionResult(RetractionOutcome.AlreadyRetracted);
        }

        try
        {
            var expected = BuildExpectedWorkflowAuthored(tenant, item);
            WorkflowDefinitionRecord existing;
            try
            {
                existing = await _workflows
                    .GetAsync(new DefinitionCoordinates(tenant, item.Key, item.Version), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkflowDefinitionNotFoundException)
            {
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }

            if (!JsonNode.DeepEquals(
                    WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(existing.Authored),
                    WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(expected)))
            {
                return new RetractionResult(
                    RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.WorkflowPinnedTupleConflictCode);
            }

            if (existing.Status == WorkflowDefinitionStatus.Withdrawn)
            {
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }
            if (!MatchesAuthoritySource(existing.PackSource, authority))
            {
                // A different pack's revision is outside this authority's withdrawal scope.
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }

            await _authorizedWorkflows
                .WithdrawAsync(expected, authority, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "PackSeedProjector: retracted workflow '{Key}' v{WorkflowVersion} from inactive pack "
                + "{Pack} v{PackVersion}; authored content remains retained.",
                item.Key, item.Version, pack.PackKey, pack.Version);
            return new RetractionResult(RetractionOutcome.Retracted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: workflow '{Key}' from inactive pack {Pack} v{Version} failed "
                + "retraction with code {Code}; retained content was not deleted.",
                item.Key, pack.PackKey, pack.Version, WorkflowRetractionFailedCode);
            return new RetractionResult(
                RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.WorkflowRetractionFailedCode);
        }
    }

    private async Task<WorkflowProjectionResult> ProjectWorkflowDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        if (_workflows is null || _authorizedWorkflows is null)
        {
            _logger.LogInformation(
                "PackSeedProjector: WorkflowDefinition '{Key}' (pack {Pack} v{Version}) recognized but the "
                + "workflow definition store is not wired on this node — deferring.",
                item.Key, pack.PackKey, pack.Version);
            return new WorkflowProjectionResult(WorkflowDefinitionOutcome.Deferred);
        }

        try
        {
            var authored = BuildExpectedWorkflowAuthored(tenant, item);
            WorkflowDefinitionRecord? existing = null;
            try
            {
                existing = await _workflows
                    .GetAsync(new DefinitionCoordinates(tenant, item.Key, item.Version), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkflowDefinitionNotFoundException)
            {
                // First projection of this pinned (tenant, key, version) tuple.
            }

            if (existing is not null)
            {
                return await ResumeOrAcceptExistingWorkflowAsync(
                        existing, authored, pack, authority, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                // The store runs the real authoring admission validator before the durable write.
                await _authorizedWorkflows.RegisterAsync(authored, authority, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkflowDefinitionConflictException)
            {
                // A concurrent projector won the read/write race; decide against the durable winner.
                existing = await _workflows
                    .GetAsync(new DefinitionCoordinates(tenant, item.Key, item.Version), cancellationToken)
                    .ConfigureAwait(false);
                return await ResumeOrAcceptExistingWorkflowAsync(
                        existing, authored, pack, authority, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _authorizedWorkflows
                .PublishAsync(authored, authority, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "PackSeedProjector: registered + published workflow '{Key}' v{WorkflowVersion} from pack "
                + "{Pack} v{PackVersion} with System ownership and {Provenance} provenance.",
                item.Key, item.Version, pack.PackKey, pack.Version, CascadeLayer.Pack);
            return new WorkflowProjectionResult(WorkflowDefinitionOutcome.Published);
        }
        catch (WorkflowAdmissionException ex)
        {
            var code = ex.Result.Violations.Count > 0
                ? ex.Result.Violations[0].Code
                : PackSeedProjectionRefusalCodes.WorkflowProjectionFailedCode;
            _logger.LogWarning(
                ex, "PackSeedProjector: workflow '{Key}' from pack {Pack} v{Version} failed authoring admission "
                + "with code {Code}; activation remains unchanged.",
                item.Key, pack.PackKey, pack.Version, code);
            return new WorkflowProjectionResult(WorkflowDefinitionOutcome.Invalid, code);
        }
        catch (RoleGateAdmissionException ex)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: workflow '{Key}' from pack {Pack} v{Version} failed role-gate "
                + "admission with code {Code}; activation remains unchanged.",
                item.Key, pack.PackKey, pack.Version, ex.Code);
            return new WorkflowProjectionResult(WorkflowDefinitionOutcome.Invalid, ex.Code);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: workflow '{Key}' from pack {Pack} v{Version} is malformed; refusing "
                + "with code {Code} without changing activation.",
                item.Key, pack.PackKey, pack.Version, WorkflowMalformedCode);
            return new WorkflowProjectionResult(
                WorkflowDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.WorkflowMalformedCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: workflow '{Key}' from pack {Pack} v{Version} failed projection; "
                + "refusing with code {Code} without changing activation.",
                item.Key, pack.PackKey, pack.Version, WorkflowProjectionFailedCode);
            return new WorkflowProjectionResult(
                WorkflowDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.WorkflowProjectionFailedCode);
        }
    }

    private async Task<WorkflowProjectionResult> ResumeOrAcceptExistingWorkflowAsync(
        WorkflowDefinitionRecord existing,
        JsonElement expectedAuthored,
        InstalledPack pack,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        var existingNode = WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(existing.Authored);
        var expectedNode = WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(expectedAuthored);
        if (!JsonNode.DeepEquals(existingNode, expectedNode))
        {
            _logger.LogWarning(
                "PackSeedProjector: refusing workflow '{Key}' v{WorkflowVersion} from pack {Pack} "
                + "v{PackVersion}: the pinned tuple contains different content, ownership, or provenance "
                + "(code {Code}).",
                existing.Key, existing.Version, pack.PackKey, pack.Version, WorkflowPinnedTupleConflictCode);
            return new WorkflowProjectionResult(
                WorkflowDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.WorkflowPinnedTupleConflictCode);
        }

        if (existing.Status == WorkflowDefinitionStatus.Published)
        {
            if (!MatchesAuthoritySource(existing.PackSource, authority))
            {
                return new WorkflowProjectionResult(
                    WorkflowDefinitionOutcome.Invalid,
                    PackProjectionAuthorityCodes.SourceMismatch);
            }
            return new WorkflowProjectionResult(WorkflowDefinitionOutcome.AlreadyPresent);
        }

        if (existing.Status == WorkflowDefinitionStatus.Withdrawn)
        {
            await _authorizedWorkflows!
                .RestorePackProjectionAsync(expectedAuthored, authority, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "PackSeedProjector: restored withdrawn workflow '{Key}' v{WorkflowVersion} from pack "
                + "{Pack} v{PackVersion}.",
                existing.Key, existing.Version, pack.PackKey, pack.Version);
            return new WorkflowProjectionResult(WorkflowDefinitionOutcome.Published);
        }

        if (existing.Status != WorkflowDefinitionStatus.Draft)
        {
            _logger.LogWarning(
                "PackSeedProjector: refusing matching workflow '{Key}' v{WorkflowVersion} from pack {Pack} "
                + "v{PackVersion}: status {Status} cannot be resumed (code {Code}).",
                existing.Key, existing.Version, pack.PackKey, pack.Version, existing.Status,
                WorkflowPinnedTupleConflictCode);
            return new WorkflowProjectionResult(
                WorkflowDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.WorkflowPinnedTupleConflictCode);
        }

        await _authorizedWorkflows!
            .PublishAsync(expectedAuthored, authority, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "PackSeedProjector: published matching draft workflow '{Key}' v{WorkflowVersion} from pack "
            + "{Pack} v{PackVersion} (restart/interrupted-projection recovery).",
            existing.Key, existing.Version, pack.PackKey, pack.Version);
        return new WorkflowProjectionResult(WorkflowDefinitionOutcome.Published);
    }

    private sealed record WorkflowProjectionResult(
        WorkflowDefinitionOutcome Outcome,
        string? RefusalCode = null);

    private enum WorkflowDefinitionOutcome
    {
        Published,
        AlreadyPresent,
        Deferred,
        Invalid,
    }

    private static JsonElement BuildExpectedWorkflowAuthored(TenantId tenant, PackSeedItem item)
    {
        if (item.ParseContent() is not JsonObject authoredNode)
        {
            throw new JsonException($"WorkflowDefinition '{item.Key}' content must be a JSON object.");
        }

        // The verified envelope owns identity. Pack JSON cannot forge tenant, revision, ownership, or layer.
        authoredNode["key"] = item.Key;
        authoredNode["tenant"] = tenant.Value;
        authoredNode["version"] = item.Version;
        authoredNode["status"] = WorkflowDefinitionStatus.Published.ToString();
        authoredNode["owner"] = new JsonObject
        {
            ["scheme"] = IdentityRef.System.Scheme,
            ["value"] = IdentityRef.System.Value,
        };
        authoredNode["provenance"] = CascadeLayer.Pack.ToString();

        using var authoredDocument = JsonDocument.Parse(authoredNode.ToJsonString());
        return authoredDocument.RootElement.Clone();
    }

    private static WorkflowDefinition WithPackSource(WorkflowDefinition model, InstalledPack pack) => new()
    {
        Envelope = model.Envelope,
        Status = model.Status,
        SubjectFormRef = model.SubjectFormRef,
        Mutability = model.Mutability,
        InitialState = model.InitialState,
        States = model.States,
        Transitions = model.Transitions,
        Triggers = model.Triggers,
        Actions = model.Actions,
        GuardRuleIds = model.GuardRuleIds,
    };

    /// <summary>A form item could not be parsed as the canonical pack form contract.</summary>
    public const string FormMalformedCode = "pack.form.malformed";

    /// <summary>The pinned tuple already belongs to different/non-pack content.</summary>
    public const string FormPinnedTupleConflictCode = "pack.form.pinned_tuple_conflict";

    /// <summary>Two packs claim the same form key without a recorded owner.</summary>
    public const string FormContentKeyConflictCode = "pack.form.content_key_conflict";

    /// <summary>An unexpected form projection failure with no more specific admission code.</summary>
    public const string FormProjectionFailedCode = "pack.form.projection_failed";

    /// <summary>An unexpected form-store failure prevented reverse projection.</summary>
    public const string FormRetractionFailedCode = "pack.form.retraction_failed";

    private async Task<RetractionResult> RetractFormDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        if (_forms is null || _schemas is null || _authorizedForms is null)
        {
            return new RetractionResult(RetractionOutcome.AlreadyRetracted);
        }

        try
        {
            if (!PackFormDefinitionContent.TryParse(
                    item.ParseContent(), out var request, out var envelope, out _))
            {
                return new RetractionResult(
                    RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormMalformedCode);
            }

            var id = new FormDefinitionId(item.Key);
            var version = SemanticVersion.Parse(item.Version);
            var schemaJson = BuilderSchemaSynthesizer.Synthesize(request, id);
            var registeredSchema = await _schemas
                .RegisterAsync(schemaJson, ct: cancellationToken)
                .ConfigureAwait(false);
            if (!EnvelopeCoordinatesMatch(envelope, id, version, tenant))
            {
                return new RetractionResult(
                    RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormPinnedTupleConflictCode);
            }
            var expected = BuildProjectedFormDefinition(
                id, version, tenant, registeredSchema.Id, request.Overlay, envelope, pack, authority);

            FormDefinition existing;
            try
            {
                existing = await _forms.GetAsync(
                    new DefinitionCoordinates(tenant, id.Value, version.ToString()), cancellationToken).ConfigureAwait(false);
            }
            catch (FormDefinitionNotFoundException)
            {
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }

            if (!MatchesPinnedPackDefinition(existing, expected))
            {
                return new RetractionResult(
                    RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormPinnedTupleConflictCode);
            }
            if (existing.Status == FormDefinitionStatus.Withdrawn)
            {
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }
            if (!MatchesAuthoritySource(existing.PackSource, authority))
            {
                // A different pack's revision is outside this authority's withdrawal scope.
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }

            await _authorizedForms.WithdrawAsync(
                expected, authority, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "PackSeedProjector: retracted form '{Key}' v{FormVersion} from inactive pack {Pack} "
                + "v{PackVersion}; authored content remains retained.",
                item.Key, item.Version, pack.PackKey, pack.Version);
            return new RetractionResult(RetractionOutcome.Retracted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: form '{Key}' from inactive pack {Pack} v{Version} failed "
                + "retraction with code {Code}; retained content was not deleted.",
                item.Key, pack.PackKey, pack.Version, FormRetractionFailedCode);
            return new RetractionResult(
                RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormRetractionFailedCode);
        }
    }

    private async Task<FormProjectionResult> ProjectFormDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        if (_forms is null || _schemas is null || _authorizedForms is null)
        {
            _logger.LogInformation(
                "PackSeedProjector: FormDefinition '{Key}' (pack {Pack} v{Version}) recognized but the form "
                + "definition store or schema registry is not wired on this node — deferring.",
                item.Key, pack.PackKey, pack.Version);
            return new FormProjectionResult(FormDefinitionOutcome.Deferred);
        }

        try
        {
            if (!PackFormDefinitionContent.TryParse(
                    item.ParseContent(), out var request, out var envelope, out var parseError))
            {
                _logger.LogWarning(
                    "PackSeedProjector: skipping malformed FormDefinition '{Key}' (pack {Pack} v{Version}): "
                    + "{Error} (code {Code}).",
                    item.Key, pack.PackKey, pack.Version, parseError, FormMalformedCode);
                return new FormProjectionResult(
                    FormDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormMalformedCode);
            }

            var id = new FormDefinitionId(item.Key);
            var version = SemanticVersion.Parse(item.Version);
            var schemaJson = BuilderSchemaSynthesizer.Synthesize(request, id);
            var registeredSchema = await _schemas
                .RegisterAsync(schemaJson, ct: cancellationToken)
                .ConfigureAwait(false);
            if (!EnvelopeCoordinatesMatch(envelope, id, version, tenant))
            {
                _logger.LogWarning(
                    "PackSeedProjector: refusing FormDefinition '{Key}' v{FormVersion} from pack {Pack} "
                    + "v{PackVersion}: its definition envelope disagrees with the verified pack tuple "
                    + "(code {Code}).",
                    item.Key, item.Version, pack.PackKey, pack.Version, FormPinnedTupleConflictCode);
                return new FormProjectionResult(
                    FormDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormPinnedTupleConflictCode);
            }
            var expected = BuildProjectedFormDefinition(
                id, version, tenant, registeredSchema.Id, request.Overlay, envelope, pack, authority);

            FormDefinitionPublishAdmission.ValidateOrThrow(expected);

            FormDefinition? existing = null;
            try
            {
                existing = await _forms.GetAsync(
                    new DefinitionCoordinates(tenant, id.Value, version.ToString()), cancellationToken).ConfigureAwait(false);
            }
            catch (FormDefinitionNotFoundException)
            {
                // First projection of this pinned (tenant, id, version) tuple.
            }

            if (existing is not null)
            {
                return await ResumeOrAcceptExistingAsync(
                        existing, expected, pack, authority, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await _authorizedForms.RegisterAsync(expected, authority, cancellationToken).ConfigureAwait(false);
            }
            catch (FormDefinitionConflictException)
            {
                // A concurrent/retried projector registered the tuple between the read and write.
                existing = await _forms.GetAsync(
                    new DefinitionCoordinates(tenant, id.Value, version.ToString()), cancellationToken).ConfigureAwait(false);
                return await ResumeOrAcceptExistingAsync(
                        existing, expected, pack, authority, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _authorizedForms.PublishAsync(
                expected, authority, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "PackSeedProjector: registered + published form '{Key}' v{FormVersion} from pack {Pack} "
                + "v{PackVersion} after schema, rule-compile, and classification admission.",
                item.Key, version, pack.PackKey, pack.Version);
            return new FormProjectionResult(FormDefinitionOutcome.Published);
        }
        catch (FormDefinitionValidationException ex)
        {
            var code = ex.Code ?? PackSeedProjectionRefusalCodes.FormProjectionFailedCode;
            _logger.LogWarning(
                ex, "PackSeedProjector: FormDefinition '{Key}' (pack {Pack} v{Version}) failed publish "
                + "admission with code {Code}; skipping without affecting other pack content.",
                item.Key, pack.PackKey, pack.Version, code);
            return new FormProjectionResult(FormDefinitionOutcome.Invalid, code);
        }
        catch (RoleGateAdmissionException ex)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: FormDefinition '{Key}' (pack {Pack} v{Version}) failed role-gate "
                + "admission with code {Code}; skipping without affecting other pack content.",
                item.Key, pack.PackKey, pack.Version, ex.Code);
            return new FormProjectionResult(FormDefinitionOutcome.Invalid, ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: FormDefinition '{Key}' (pack {Pack} v{Version}) failed pinned-content "
                + "validation or projection with code {Code} — skipping without affecting other pack content.",
                item.Key, pack.PackKey, pack.Version, FormProjectionFailedCode);
            return new FormProjectionResult(
                FormDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormProjectionFailedCode);
        }
    }

    private async Task<FormProjectionResult> ResumeOrAcceptExistingAsync(
        FormDefinition existing,
        FormDefinition expected,
        InstalledPack pack,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        if (!MatchesPinnedPackDefinition(existing, expected))
        {
            _logger.LogWarning(
                "PackSeedProjector: refusing FormDefinition '{Key}' v{FormVersion} from pack {Pack} v{PackVersion}: "
                + "the pinned tuple already exists with different content or a non-system owner (code {Code}).",
                expected.Id.Value, expected.Version, pack.PackKey, pack.Version, FormPinnedTupleConflictCode);
            return new FormProjectionResult(
                FormDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormPinnedTupleConflictCode);
        }

        if (existing.Status == FormDefinitionStatus.Published)
        {
            if (!MatchesAuthoritySource(existing.PackSource, authority))
            {
                return new FormProjectionResult(
                    FormDefinitionOutcome.Invalid,
                    PackProjectionAuthorityCodes.SourceMismatch);
            }
            return new FormProjectionResult(FormDefinitionOutcome.AlreadyPresent);
        }

        if (existing.Status == FormDefinitionStatus.Withdrawn)
        {
            FormDefinitionPublishAdmission.ValidateOrThrow(expected);
            await _authorizedForms!.RestorePackProjectionAsync(
                    expected, authority, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "PackSeedProjector: restored withdrawn form '{Key}' v{FormVersion} from pack {Pack} "
                + "v{PackVersion}.",
                expected.Id.Value, expected.Version, pack.PackKey, pack.Version);
            return new FormProjectionResult(FormDefinitionOutcome.Published);
        }

        if (existing.Status != FormDefinitionStatus.Draft)
        {
            _logger.LogWarning(
                "PackSeedProjector: refusing FormDefinition '{Key}' v{FormVersion} from pack {Pack} v{PackVersion}: "
                + "the matching revision is {Status}, not Draft or Published (code {Code}).",
                expected.Id.Value, expected.Version, pack.PackKey, pack.Version, existing.Status,
                FormPinnedTupleConflictCode);
            return new FormProjectionResult(
                FormDefinitionOutcome.Invalid, PackSeedProjectionRefusalCodes.FormPinnedTupleConflictCode);
        }

        FormDefinitionPublishAdmission.ValidateOrThrow(expected);
        await _authorizedForms!.PublishAsync(
                expected, authority, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "PackSeedProjector: published matching draft form '{Key}' v{FormVersion} from pack {Pack} "
            + "v{PackVersion} (restart/interrupted-projection recovery).",
            expected.Id.Value, expected.Version, pack.PackKey, pack.Version);
        return new FormProjectionResult(FormDefinitionOutcome.Published);
    }

    private static bool MatchesPinnedPackDefinition(FormDefinition existing, FormDefinition expected)
    {
        if (existing.SchemaRef != expected.SchemaRef)
        {
            return false;
        }

        var existingEnvelope = JsonSerializer.SerializeToNode(existing.Envelope);
        var expectedEnvelope = JsonSerializer.SerializeToNode(expected.Envelope);
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(existingEnvelope, expectedEnvelope))
        {
            return false;
        }

        var existingOverlay = JsonSerializer.SerializeToNode(existing.Overlay);
        var expectedOverlay = JsonSerializer.SerializeToNode(expected.Overlay);
        return System.Text.Json.Nodes.JsonNode.DeepEquals(existingOverlay, expectedOverlay);
    }

    private FormDefinition BuildProjectedFormDefinition(
        FormDefinitionId id,
        SemanticVersion version,
        TenantId tenant,
        SchemaId schemaRef,
        OverlayDto overlay,
        DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>? envelope,
        InstalledPack pack,
        PackProjectionAuthority authority)
    {
        var definition = FormDefinitionRoutes.BuildDefinition(
            id,
            version,
            tenant,
            IdentityRef.System,
            schemaRef,
            overlay,
            authority.ActivationInstant);
        // The HTTP authoring route supplies its operator-role compatibility fallback when access is
        // omitted. Pack content has vendor authority and must not inherit those platform roles: an
        // omitted pack gate means no gate, while an explicitly authored gate remains fully admitted.
        var projectedSections = definition.Overlay.Sections
            .Select((section, index) => overlay.Sections[index].Access is null
                ? section with { Access = new SectionAccess([], []) }
                : section)
            .ToArray();
        return definition with
        {
            Envelope = (envelope ?? definition.Envelope) with { CascadeLayer = CascadeLayer.Pack },
            Overlay = definition.Overlay with { Sections = projectedSections },
            CreatedAt = authority.ActivationInstant,
            UpdatedAt = authority.ActivationInstant,
        };
    }

    private static bool EnvelopeCoordinatesMatch(
        DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>? envelope,
        FormDefinitionId id,
        SemanticVersion version,
        TenantId tenant)
        => envelope is null
            || (envelope.Identity == id
                && envelope.Version == version
                && envelope.Tenant == tenant
                && envelope.CascadeLayer == CascadeLayer.Pack);

    private static bool MatchesAuthoritySource(
        PackProjectionSource? source,
        PackProjectionAuthority authority) =>
        source is not null
        && string.Equals(source.PackId, authority.PackId, StringComparison.Ordinal);

    private enum FormDefinitionOutcome
    {
        Published,
        AlreadyPresent,
        Deferred,
        Invalid,
    }

    private sealed record FormProjectionResult(
        FormDefinitionOutcome Outcome,
        string? RefusalCode = null);

    /// <summary>A template body is structurally invalid JSON for the pinned content contract.</summary>
    public const string TemplateMalformedCode = "pack.template.malformed";

    /// <summary>The server-owned content-envelope key differs from the template body's key.</summary>
    public const string TemplateKeyMismatchCode = "pack.template.key_mismatch";

    /// <summary>The server-owned content-envelope version differs from the template body's version.</summary>
    public const string TemplateVersionMismatchCode = "pack.template.version_mismatch";

    /// <summary>A different template is already published at the same immutable key/version tuple.</summary>
    public const string TemplatePinnedTupleConflictCode = "pack.template.pinned_tuple_conflict";

    /// <summary>Multiple installed packs claim the same template content key without a resolved owner.</summary>
    public const string TemplateContentKeyConflictCode = "pack.template.content_key_conflict";

    /// <summary>The registry rejected an otherwise admissible template.</summary>
    public const string TemplateProjectionFailedCode = "pack.template.projection_failed";

    private TemplateProjectionResult ProjectTemplate(TenantId tenant, InstalledPack pack, PackSeedItem item)
    {
        // The template registry is optional/back-compat: when the node hasn't wired it, recognize the kind
        // and DEFER (never fail-open-skip) — distinct from an unknown kind, exactly like FormDefinition.
        if (_templates is null)
        {
            _logger.LogInformation(
                "PackSeedProjector: TemplateDefinition '{Key}' (pack {Pack} v{Version}) recognized but the "
                + "document-template registry is not wired on this node — deferring.",
                item.Key, pack.PackKey, pack.Version);
            return new TemplateProjectionResult(TemplateOutcome.Deferred);
        }

        try
        {
            if (!PackTemplateContent.TryParse(item.ParseContent(), tenant, out var template, out var parseError))
            {
                _logger.LogWarning(
                    "PackSeedProjector: skipping malformed TemplateDefinition '{Key}' (pack {Pack} v{Version}): "
                    + "{Error}.",
                    item.Key, pack.PackKey, pack.Version, parseError);
                return new TemplateProjectionResult(
                    TemplateOutcome.Invalid, PackSeedProjectionRefusalCodes.TemplateMalformedCode);
            }

            if (!string.Equals(template.Key, item.Key, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "PackSeedProjector: TemplateDefinition envelope key '{EnvelopeKey}' does not match body "
                    + "key '{BodyKey}' (pack {Pack} v{Version}) — refusing with code {Code}.",
                    item.Key, template.Key, pack.PackKey, pack.Version, TemplateKeyMismatchCode);
                return new TemplateProjectionResult(
                    TemplateOutcome.Invalid, PackSeedProjectionRefusalCodes.TemplateKeyMismatchCode);
            }

            if (!string.Equals(template.Version, item.Version, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "PackSeedProjector: TemplateDefinition '{Key}' envelope version '{EnvelopeVersion}' does "
                    + "not match body version '{BodyVersion}' (pack {Pack} v{Version}) — refusing with code "
                    + "{Code}.",
                    item.Key, item.Version, template.Version, pack.PackKey, pack.Version,
                    TemplateVersionMismatchCode);
                return new TemplateProjectionResult(
                    TemplateOutcome.Invalid, PackSeedProjectionRefusalCodes.TemplateVersionMismatchCode);
            }

            var existing = _templates.Resolve(item.Key, item.Version);
            if (existing is not null)
            {
                if (TemplateDefinitionsEqual(existing, template))
                {
                    return new TemplateProjectionResult(TemplateOutcome.AlreadyPresent);
                }

                _logger.LogWarning(
                    "PackSeedProjector: TemplateDefinition '{Key}' v{TemplateVersion} already exists with a "
                    + "different body — refusing with code {Code}.",
                    item.Key, item.Version, TemplatePinnedTupleConflictCode);
                return new TemplateProjectionResult(
                    TemplateOutcome.Invalid, PackSeedProjectionRefusalCodes.TemplatePinnedTupleConflictCode);
            }

            _templates.Publish(template);
            _logger.LogInformation(
                "PackSeedProjector: published template '{Key}' v{TemplateVersion} (documentType {Type}) from pack "
                + "{Pack} v{Version}.",
                template.Key, template.Version, template.DocumentType, pack.PackKey, pack.Version);
            return new TemplateProjectionResult(TemplateOutcome.Published);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: TemplateDefinition '{Key}' (pack {Pack} v{Version}) failed to project — "
                + "skipping.",
                item.Key, pack.PackKey, pack.Version);
            return new TemplateProjectionResult(
                TemplateOutcome.Invalid, PackSeedProjectionRefusalCodes.TemplateProjectionFailedCode);
        }
    }

    private static bool TemplateDefinitionsEqual(TemplateDefinition existing, TemplateDefinition expected)
        => JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(existing),
            JsonSerializer.SerializeToNode(expected));

    private enum TemplateOutcome
    {
        Published,
        AlreadyPresent,
        Deferred,
        Invalid,
    }

    private sealed record TemplateProjectionResult(
        TemplateOutcome Outcome,
        string? RefusalCode = null);

    /// <summary>
    /// Decides whether an active pack's asset-type item should PROJECT, defer to another pack's ownership,
    /// or be refused as an unresolved contested key (F4). A key no OTHER pack contests always projects.
    /// </summary>
    private static ContestedDecision DecideContested(
        InstalledPack pack, PackSeedItem item,
        IReadOnlyDictionary<string, PackCrossPackCollision> collisions)
    {
        if (!collisions.TryGetValue(item.Key, out var collision))
        {
            return ContestedDecision.Project; // not a cross-pack collision — project as normal.
        }

        if (collision.Resolution == PackKeyOwnershipResolution.RequiresChoice)
        {
            return ContestedDecision.ContestedUnresolved; // fail-closed: nobody projects a contested key.
        }

        return string.Equals(collision.OwnerPackKey, pack.PackKey, StringComparison.Ordinal)
            ? ContestedDecision.Project           // this pack is the resolved owner.
            : ContestedDecision.OwnedByOtherPack; // another pack owns it — the non-owner defers.
    }

    private enum ContestedDecision
    {
        Project,
        OwnedByOtherPack,
        ContestedUnresolved,
    }

    private AssetTypeOutcome ProjectAssetType(InstalledPack pack, PackSeedItem item)
    {
        EntityTypeId id;
        EntityTypeDescriptor descriptor;
        string parseError;
        try
        {
            if (!PackAssetTypeContent.TryParse(
                    item.ParseContent(), item.Version, FormVersionByKey(pack), out id, out descriptor, out parseError))
            {
                _logger.LogWarning(
                    "PackSeedProjector: skipping malformed AssetTypeDefinition '{Key}' (pack {Pack} v{Version}): "
                    + "{Error}.",
                    item.Key, pack.PackKey, pack.Version, parseError);
                return AssetTypeOutcome.Invalid;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: AssetTypeDefinition '{Key}' (pack {Pack} v{Version}) failed to parse — "
                + "skipping.",
                item.Key, pack.PackKey, pack.Version);
            return AssetTypeOutcome.Invalid;
        }

        // Idempotency: an already-seeded id (a prior projection pass seeded it — including the resolved
        // OWNER of a contested key re-projecting) is a no-op; SeedType would otherwise reject the duplicate.
        // Cross-pack CONTESTED keys are decided upstream (DecideContested), so a NON-owner never reaches
        // here — the only caller for a contested key is its resolved owner.
        if (_types.GetSeed(id) is not null)
        {
            return AssetTypeOutcome.AlreadyPresent;
        }

        try
        {
            if (_types.RestorePackSeed(id))
            {
                _logger.LogInformation(
                    "PackSeedProjector: restored retained asset type '{Id}' from pack {Pack} v{Version} "
                    + "(provenance Pack).",
                    id.Value, pack.PackKey, pack.Version);
                return AssetTypeOutcome.Seeded;
            }

            _types.SeedType(new EntityTypeSeed(id, descriptor, CascadeLayer.Pack));
            _logger.LogInformation(
                "PackSeedProjector: seeded asset type '{Id}' ({DisplayName}) from pack {Pack} v{Version} "
                + "(provenance Pack).",
                id.Value, descriptor.DisplayName, pack.PackKey, pack.Version);
            return AssetTypeOutcome.Seeded;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Defensive: the registry fail-closes on its own invariants (e.g. a race that seeded the id
            // between the GetSeed check and here). Log + skip rather than bricking the whole projection.
            _logger.LogWarning(
                ex, "PackSeedProjector: registry rejected asset-type seed '{Id}' from pack {Pack} v{Version} — "
                + "skipping.",
                id.Value, pack.PackKey, pack.Version);
            return AssetTypeOutcome.Invalid;
        }
    }

    /// <summary>
    /// Resolves a pack-local <c>FormDefinition</c> content key to the version that leaf declares — the SAME
    /// (key, version) tuple <see cref="ProjectFormDefinitionAsync"/> publishes the form under
    /// (<c>new FormDefinitionId(item.Key)</c> / <c>SemanticVersion.Parse(item.Version)</c>). An
    /// <c>AssetTypeDefinition</c>'s <c>propertyFormBinding</c> therefore resolves to the INSTALLED form's
    /// identity through one key space, never a second one; a key the pack does not carry resolves to
    /// <see langword="null"/> and the type refuses (verify already refuses such a pack outright).
    /// </summary>
    private static Func<string, string?> FormVersionByKey(InstalledPack pack)
        => key => pack.SeedItems
            .FirstOrDefault(i => i.Kind == PackContentKind.FormDefinition
                                 && string.Equals(i.Key, key, StringComparison.Ordinal))
            ?.Version;

    private enum AssetTypeOutcome
    {
        Seeded,
        AlreadyPresent,
        Invalid,
    }

    /// <summary>An unexpected asset registry failure prevented reverse projection.</summary>
    public const string AssetRetractionFailedCode = "pack.asset.retraction_failed";

    private RetractionResult RetractAssetType(InstalledPack pack, PackSeedItem item)
    {
        try
        {
            if (!PackAssetTypeContent.TryParse(
                    item.ParseContent(), item.Version, FormVersionByKey(pack), out var id, out _, out _))
            {
                return new RetractionResult(RetractionOutcome.AlreadyRetracted);
            }

            var retracted = _types.RetractPackSeed(id);
            if (retracted)
            {
                _logger.LogInformation(
                    "PackSeedProjector: retracted asset type '{Id}' from inactive pack {Pack} v{Version}; "
                    + "seed and tenant overrides remain retained.",
                    id.Value, pack.PackKey, pack.Version);
            }
            return new RetractionResult(
                retracted ? RetractionOutcome.Retracted : RetractionOutcome.AlreadyRetracted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: asset type '{Key}' from inactive pack {Pack} v{Version} could not "
                + "be retracted; retained content was not deleted.",
                item.Key, pack.PackKey, pack.Version);
            return new RetractionResult(
                RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.AssetRetractionFailedCode);
        }
    }

    /// <summary>The access-projection seam (role vocabulary / definition writer) is not wired.</summary>
    public const string AccessProjectionNotWiredCode = "pack.projection.access_seam_not_wired";

    /// <summary>The declared role or binding was refused by the platform authorization admission.</summary>
    public const string AccessProjectionRefusedCode = "pack.projection.access_definition_refused";

    /// <summary>Returns the RFC 6901 location of immutable item bytes in its exported pack document.</summary>
    private static string ContentPointer(InstalledPack pack, PackSeedItem item)
    {
        for (var index = 0; index < pack.SeedItems.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(pack.SeedItems[index].Key, item.Key))
            {
                return $"/contents/{index}/contentBase64";
            }
        }

        return "/";
    }

    // (L675) A pack ships default ROLE NAMES and default CAPABILITY BINDINGS. The binding lands as the
    // PUBLISHER CEILING through the ordinary AuthorizationDefinitionWriter and the ordinary
    // AuthorizationDefinitionAdmission — the same admission the platform seed and a tenant replacement
    // pass — so a pack's ceiling is bounded by every rule that admission holds, L628 included: a pack
    // offering the Auditor anything is refused right here, by code it does not get to choose.
    /// <summary>The item body, or null when it is not parseable JSON at all — an unparseable body is
    /// not a grant, and the kind's own parse refuses it a few lines later with its own code. The grant
    /// fence must never be the thing that aborts a pass over a malformed sibling row.</summary>
    private static JsonNode? TryParseContent(PackSeedItem item)
    {
        try
        {
            return JsonNode.Parse(item.CanonicalJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ProjectAccessItemAsync(
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        var content = TryParseContent(item);
        try
        {
            if (item.Kind == PackContentKind.RoleDefinition)
            {
                if (_roleVocabulary is null) return PackSeedProjectionRefusalCodes.AccessSeamNotWired;
                if (!PackAuthorizationContentAdmission.TryParseRoleDefinition(
                        pack.PackKey, content, out var role))
                {
                    return PackSeedProjectionRefusalCodes.RoleDefinitionMalformedCode;
                }

                await _roleVocabulary.InstallAsync(role!, cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (_authorizationDefinitions is null) return PackSeedProjectionRefusalCodes.AccessSeamNotWired;
            if (!PackAuthorizationContentAdmission.TryParseCapabilityBinding(
                    pack.PackKey, content, out var definition))
            {
                return PackSeedProjectionRefusalCodes.CapabilityBindingMalformedCode;
            }

            await _authorizationDefinitions.WritePackDefinitionAsync(
                definition!, authority, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: {Kind} '{Key}' from pack {Pack} v{Version} was refused by the "
                + "authorization admission with code {Code}.",
                item.Kind, item.Key, pack.PackKey, pack.Version, AccessProjectionRefusedCode);
            return PackSeedProjectionRefusalCodes.AccessDefinitionRefused;
        }
    }

    // (L633/L675) The reverse: a withdrawn role name leaves the vocabulary, and a withdrawn binding is
    // narrowed to EMPTY so its capability offers nothing to anyone. The definition revision itself is
    // retained, exactly as a taxonomy version is retired rather than deleted — this store is
    // append-only and narrow-only, and the binding is what makes a definition effective.
    private async Task<RetractionResult> RetractAccessItemAsync(
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        var content = TryParseContent(item);
        try
        {
            bool retracted;
            if (item.Kind == PackContentKind.RoleDefinition)
            {
                retracted = _roleVocabulary is not null
                    && PackAuthorizationContentAdmission.TryParseRoleDefinition(
                        pack.PackKey, content, out var role)
                    && await _roleVocabulary.RemoveAsync(role!.Role, cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                retracted = _authorizationDefinitions is not null
                    && PackAuthorizationContentAdmission.TryParseCapabilityBinding(
                        pack.PackKey, content, out var definition)
                    && await _authorizationDefinitions.WithdrawPackDefinitionAsync(
                        definition!.DefinitionId, authority, cancellationToken).ConfigureAwait(false);
            }

            if (retracted)
            {
                _logger.LogInformation(
                    "PackSeedProjector: retracted {Kind} '{Key}' projected by pack {Pack} v{PackVersion}.",
                    item.Kind, item.Key, pack.PackKey, pack.Version);
            }

            return new RetractionResult(
                retracted ? RetractionOutcome.Retracted : RetractionOutcome.AlreadyRetracted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: {Kind} '{Key}' from pack {Pack} v{PackVersion} could not be "
                + "retracted with code {Code}.",
                item.Kind, item.Key, pack.PackKey, pack.Version, DefinitionRetractionFailedCode);
            return new RetractionResult(
                RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.DefinitionRetractionFailedCode);
        }
    }

    /// <summary>An unexpected registry failure prevented reverse projection.</summary>
    public const string DefinitionRetractionFailedCode = "pack.definition.retraction_failed";

    // (L633) Reverse projection for the definition kinds whose registries hold one pinned
    // (tenant, key, version) row and which had a projection case but never a retraction case.
    // NavWorkspaceConfig needs none: PackNavigationRoutes projects ACTIVE seeds directly on every read, so
    // it stops resolving the moment the pack stops being the active version.
    private async Task<RetractionResult> RetractDefinitionAsync(
        TenantId tenant,
        InstalledPack pack,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        try
        {
            var retracted = item.Kind switch
            {
                PackContentKind.ViewDefinition => _viewDefinitions is not null
                    && await _viewDefinitions.RemoveAsync(
                        tenant.Value, item.Key, item.Version, cancellationToken).ConfigureAwait(false),
                PackContentKind.ReportDefinition => _reportDefinitions is not null
                    && await _reportDefinitions.RemoveAsync(
                        tenant.Value, item.Key, item.Version, cancellationToken).ConfigureAwait(false),
                PackContentKind.ScheduleDefinition => _scheduleDefinitions is not null
                    && await _scheduleDefinitions.RemoveAsync(
                        tenant.Value, item.Key, item.Version, cancellationToken).ConfigureAwait(false),
                PackContentKind.DataExchangeDefinition => _dataExchangeDefinitions is not null
                    && await _dataExchangeDefinitions.RemoveAsync(
                        tenant.Value, item.Key, item.Version, cancellationToken).ConfigureAwait(false),
                PackContentKind.StandingRuleDefinition => _standingRules is not null
                    && await _standingRules.RemoveAsync(
                        item.Key, item.Version, cancellationToken).ConfigureAwait(false),
                PackContentKind.TemplateDefinition => _templates?.Remove(item.Key, item.Version) == true,
                PackContentKind.TaxonomyDefinition => await RetractTaxonomyAsync(
                    tenant, item, authority, cancellationToken).ConfigureAwait(false),
                _ => false,
            };

            if (retracted)
            {
                _logger.LogInformation(
                    "PackSeedProjector: retracted {Kind} '{Key}' v{ItemVersion} projected by pack {Pack} "
                    + "v{PackVersion}; the immutable seed layer and tenant overrides remain retained.",
                    item.Kind, item.Key, item.Version, pack.PackKey, pack.Version);
            }

            return new RetractionResult(
                retracted ? RetractionOutcome.Retracted : RetractionOutcome.AlreadyRetracted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "PackSeedProjector: {Kind} '{Key}' from pack {Pack} v{PackVersion} could not be "
                + "retracted with code {Code}; retained content was not deleted.",
                item.Kind, item.Key, pack.PackKey, pack.Version, DefinitionRetractionFailedCode);
            return new RetractionResult(
                RetractionOutcome.Invalid, PackSeedProjectionRefusalCodes.DefinitionRetractionFailedCode);
        }
    }

    // Taxonomy has no row removal: RETIRE is its reverse of publish, and it is what the registry offers.
    private async Task<bool> RetractTaxonomyAsync(
        TenantId tenant,
        PackSeedItem item,
        PackProjectionAuthority authority,
        CancellationToken cancellationToken)
    {
        if (_taxonomies is null || !TryParseTaxonomy(item, out var taxonomy))
        {
            return false;
        }

        await _taxonomies.RetireDefinitionVersionAsync(
                tenant, taxonomy!.Id, taxonomy.Version, taxonomy.Owner, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private sealed record RetractionResult(
        RetractionOutcome Outcome,
        string? RefusalCode = null);

    private enum RetractionOutcome
    {
        Retracted,
        AlreadyRetracted,
        Invalid,
    }
}
