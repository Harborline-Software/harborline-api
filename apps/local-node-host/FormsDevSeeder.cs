using System.Collections.Generic;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// DEV-ONLY dynamic-forms seeder (ADR 0055 forms-engine wiring, 2026-06-25). On a development node it
/// registers + publishes ONE first-party <see cref="FormDefinition"/> — an equipment-inspection form for
/// the Dubai asset-management demo — so the Harborline App dynamic-form route renders REAL engine data (a
/// localized <see cref="Harborline.Api.Foundation.Forms.Engine.FormView"/>) instead of a mock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The demo realization of the ADR 0055 forms engine — no new ADR.</b> The engine
/// (<c>foundation-forms-engine</c>) + the durable store + the node render/submit routes
/// (<see cref="Harborline.Api.LocalNodeHost.Health.FormsRoutes"/>) are already built + DI-registered
/// (<see cref="Harborline.Api.LocalNodeHost.Data.Forms.NodeFormsComposition.AddNodeForms"/>). This dev seeder is
/// the FIRST production driver of the otherwise-built-but-unwired engine: it writes the
/// <c>FormDefinition</c> so a GET renders a view and a POST validates + persists a real instance.
/// </para>
/// <para>
/// <b>The dev gate is AIRTIGHT — fail-safe OFF (reuses <see cref="CalendarDevSeeder.ShouldSeed"/>).</b> The
/// seed runs ONLY when <c>IsDevelopment()</c> is true. A Production host never registers this form, so a real
/// tenant's form store is never polluted with demo data.
/// </para>
/// <para>
/// <b>Tenant = the active team (the same resolution the route uses).</b> The form is registered + published
/// for <see cref="NodeTenant.Resolve"/> — the active-team projected tenant — so the route's per-request
/// minted capability (also keyed on <see cref="NodeTenant.Resolve"/>) reads + writes the SAME tenant's
/// definition (no cross-tenant mismatch).
/// </para>
/// <para>
/// <b>Section access = the single-operator role.</b> The form's only section gates read + write on
/// <see cref="FormsRoutes.NodeOperatorRole"/> — the same role the route grants the single node operator —
/// so the minted capability can render every field and save a candidate (the single-operator full-authority
/// posture, matching <c>FormsRouteTests</c>).
/// </para>
/// <para>
/// <b>Bilingual labels (the Dubai ar-AE path).</b> Every label/section title carries en + ar values, so a
/// Harborline App running with the <c>ar-AE</c> locale resolves Arabic field labels through the engine's
/// <see cref="InternationalizedText.Resolve"/> (the engine owns locale resolution; the view carries
/// pre-localized strings — ONR survey recommendation (i)).
/// </para>
/// <para>
/// <b>One PII field.</b> <c>inspectorName</c> is <see cref="PiiSensitivity.Sensitive"/> — it is never placed
/// in a rendered view (the renderer shows it as a redacted row) and is tenant-key-encrypted on save
/// (INV-S3). It proves the PII redaction path end-to-end through the Harborline App UI.
/// </para>
/// <para>
/// <b>Idempotent.</b> A restart re-runs cleanly: a definition already registered for the tenant is left
/// as-is (the store's <c>RegisterAsync</c> on an existing id is the no-op path). Registered AFTER
/// <c>MultiTeamBootstrapHostedService</c> (so an active team exists) and AFTER the forms composition.
/// </para>
/// </remarks>
public sealed class FormsDevSeeder : IHostedService
{
    /// <summary>The demo form's stable id — the Harborline App route fetches THIS id.</summary>
    public const string DemoFormId = "equipment-inspection.v1";

    /// <summary>
    /// The section read/write roles the demo form grants. The route mints the operator's capability with
    /// EITHER the resolved <c>ICurrentUser.Roles</c> (the active-team display role — the single-office default
    /// operator is <c>Admin</c>) OR, when no current user resolves, the <see cref="FormsRoutes.NodeOperatorRole"/>
    /// fallback. So the section grants the UNION of both — every team display role (Admin / Member / Viewer)
    /// AND <c>node:operator</c> — so the single operator can read + write every field in BOTH the live host
    /// (role = "Admin") and the route-test harness (role = "node:operator"). Without this union, a live GET
    /// returns every field <c>isReadable: false</c> (the operator's "Admin" role does not intersect a
    /// node-operator-only section), and the renderer would show only redacted rows.
    /// </summary>
    private static readonly string[] OperatorSectionRoles =
        new[] { FormsRoutes.NodeOperatorRole, "Admin", "Member", "Viewer" };

    private readonly ISchemaRegistry _schemaRegistry;
    private readonly AuthorizedFormDefinitionLifecycle _store;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FormsDevSeeder> _logger;

    public FormsDevSeeder(
        ISchemaRegistry schemaRegistry,
        AuthorizedFormDefinitionLifecycle store,
        IActiveTeamAccessor activeTeam,
        IHostEnvironment environment,
        ILogger<FormsDevSeeder> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _schemaRegistry = schemaRegistry;
        _store = store;
        _activeTeam = activeTeam;
        _environment = environment;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // ── The airtight dev gate — fail-safe OFF (the SAME gate as CalendarDevSeeder). ─────────────────
        if (!CalendarDevSeeder.ShouldSeed(_environment))
        {
            _logger.LogDebug(
                "FormsDevSeeder: environment '{Environment}' is not a development environment and no dev-seed "
                + "flag is set — skipping the demo equipment-inspection form seed (production-safe).",
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
                "FormsDevSeeder: no active team resolved — skipping the demo form seed. (Register this hosted "
                + "service AFTER MultiTeamBootstrapHostedService.)");
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var authority = new AuthorizationWriteContext(
            new ActorId("installer:development-form-seed"), tenantId, now);
        AuthorizedFormDefinitionLifecycle.WriteAuthority decision;
        try
        {
            decision = await _store.DecideAsync(DemoFormId, authority, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorizationDeniedException ex)
        {
            _logger.LogWarning(
                ex,
                "FormsDevSeeder: the development seed principal is not authorized for tenant {TenantId} — skipping the demo form seed.",
                tenantId);
            return;
        }

        // Idempotency: a definition already published for this tenant is left as-is.
        var existing = await _store
            .GetCurrentPublishedAsync(new DefinitionAddress(tenantId, DemoFormId), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug(
                "FormsDevSeeder: demo form '{FormId}' already published for tenant {TenantId} — seed is a "
                + "no-op (idempotent).",
                DemoFormId, tenantId);
            return;
        }

        var schema = await _schemaRegistry
            .RegisterAsync(EquipmentInspectionSchemaJson)
            .ConfigureAwait(false);

        var definition = BuildDefinition(tenantId, schema.Id, now);

        await _store.RegisterAndPublishAsync(definition, decision, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "FormsDevSeeder: published the demo equipment-inspection form '{FormId}' (v{Version}) for tenant "
            + "{TenantId} — the Harborline dynamic-form route now renders REAL engine data (GET "
            + "{RouteBase}/{FormId}). en + ar labels; 'inspectorName' is PII (redacted in views, encrypted on "
            + "save).",
            DemoFormId, definition.Version, tenantId, FormsRoutes.RouteBase, DemoFormId);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Builds the demo equipment-inspection <see cref="FormDefinition"/> — a Dubai asset-management
    /// inspection: assetId (text, required), assetType (select), conditionRating (number, required),
    /// inspectedOn (date, required), status (select), followUp (checkbox), notes (textarea), plus a PII
    /// inspectorName (sensitive). Bilingual en/ar labels.
    /// </summary>
    private static FormDefinition BuildDefinition(TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
        => new(
            Id: new FormDefinitionId(DemoFormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["assetId"] = new(
                        Bilingual("Asset ID", "معرّف الأصل"),
                        HelpText: Bilingual(
                            "The tracked asset's identifier (e.g. metro car or station unit).",
                            "معرّف الأصل المتعقَّب (مثل عربة المترو أو وحدة المحطة)."),
                        ControlHint: "text"),
                    ["assetType"] = new(
                        Bilingual("Asset type", "نوع الأصل"),
                        ControlHint: "select"),
                    // ADR 0101 Rev 3.1 Wave 2c: the Harborline App runner renders this hint via a 1..5 grade
                    // picker (ConditionRatingControl), not a bare number input. Wiring a REGISTERED
                    // ConditionRatingFieldBinding for this demo form (so a submission also projects a
                    // ConditionAssessment onto a registry entity) is Wave-3 catalog-seeding scope — see
                    // the Wave 2c handoff.
                    ["conditionRating"] = new(
                        Bilingual("Condition rating (1–5)", "تقييم الحالة (1–5)"),
                        HelpText: Bilingual(
                            "1 = failed, 5 = excellent.",
                            "1 = غير صالح، 5 = ممتاز."),
                        ControlHint: "condition-rating"),
                    ["inspectedOn"] = new(
                        Bilingual("Inspected on", "تاريخ الفحص"),
                        ControlHint: "date"),
                    ["status"] = new(
                        Bilingual("Status", "الحالة"),
                        ControlHint: "select"),
                    ["followUp"] = new(
                        Bilingual("Follow-up required", "يتطلب متابعة"),
                        ControlHint: "checkbox"),
                    ["notes"] = new(
                        Bilingual("Notes", "ملاحظات"),
                        ControlHint: "textarea"),
                    // PII: the inspector's name — never rendered in a view, encrypted on save (INV-S3).
                    ["inspectorName"] = new(
                        Bilingual("Inspector name", "اسم الفاحص"),
                        PiiSensitivity: PiiSensitivity.Sensitive),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "inspection",
                        Title: Bilingual("Equipment inspection", "فحص المعدات"),
                        Fields: new[]
                        {
                            "assetId", "assetType", "conditionRating", "inspectedOn",
                            "status", "followUp", "notes", "inspectorName",
                        },
                        Access: new SectionAccess(
                            ReadRoles: OperatorSectionRoles,
                            WriteRoles: OperatorSectionRoles)),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Equipment Inspection", "فحص المعدات"),
                Description: Bilingual(
                    "Record an equipment inspection for a tracked asset.",
                    "سجّل فحص المعدات لأصل متعقَّب.")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);

    /// <summary>Builds an en + ar <see cref="InternationalizedText"/> (default locale en).</summary>
    private static InternationalizedText Bilingual(string en, string ar)
        => new("en", new Dictionary<string, string> { ["en"] = en, ["ar"] = ar });

    /// <summary>
    /// The form's JSON Schema (2020-12) — field types + structural validation. <c>assetId</c>,
    /// <c>conditionRating</c>, and <c>inspectedOn</c> are required; <c>assetType</c> + <c>status</c> are
    /// enums (drive the select options server-side); <c>conditionRating</c> is bounded 1–5. The select
    /// option VALUES match the schema enums so a submit validates.
    /// </summary>
    private const string EquipmentInspectionSchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "assetId": { "type": "string", "minLength": 1 },
            "assetType": { "type": "string", "enum": ["metro-car", "tram", "station-unit", "escalator"] },
            "conditionRating": { "type": "integer", "minimum": 1, "maximum": 5 },
            "inspectedOn": { "type": "string", "format": "date" },
            "status": { "type": "string", "enum": ["pass", "fail", "needs-review"] },
            "followUp": { "type": "boolean" },
            "notes": { "type": "string" },
            "inspectorName": { "type": "string" }
          },
          "required": ["assetId", "conditionRating", "inspectedOn"],
          "additionalProperties": false
        }
        """;
}
