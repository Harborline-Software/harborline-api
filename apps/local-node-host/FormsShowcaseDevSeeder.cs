using System.Collections.Generic;
using System.Linq;

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
/// DEV-ONLY prototype-showcase forms seeder (CIC-requested demo artifacts, 2026-07-03). Publishes FIVE
/// real, distinct <see cref="FormDefinition"/>s spanning the platform's form-complexity ladder (L0 flat →
/// L4 deep-nested) so a developer running the Harborline App locally can open each one in the FORM BUILDER
/// (<c>/form-builder?form=&lt;id&gt;</c>, which reconstructs the authoring model — including the Rev-7
/// <see cref="FormItem"/> tree — from the persisted definition) and in the FORM RUNNER
/// (<c>/forms?form=&lt;id&gt;</c>, which renders the engine's <see cref="Harborline.Api.Foundation.Forms.Engine.FormView"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive sibling to <see cref="FormsDevSeeder"/>.</b> This is a SEPARATE hosted service seeding
/// SEPARATE form ids (the <c>showcase-*</c> prefix) — it does not touch the existing equipment-inspection
/// demo form or its seeder. Same airtight dev gate
/// (<see cref="CalendarDevSeeder.ShouldSeed"/>), same tenant resolution (<see cref="NodeTenant.Resolve"/>),
/// same idempotent register-then-publish pattern.
/// </para>
/// <para>
/// <b>The render engine now walks the Rev-7 tree (the showcase's original gap, closed).</b>
/// <c>FormEngine.BuildView</c> / <c>BuildGovernedViewAsync</c> project each section's
/// <see cref="FormSection.Items"/> tree (Group / Collection / Content / Action; References expanded to
/// Groups) into <see cref="FormView"/>.<c>Sections[].Items</c>, so the RUNNER renders the nested structure
/// — nested group fieldsets, the table-presented collection + its sibling collection, the content block,
/// the open-url action — not a flat field list. Every definition still ALSO carries a flattened
/// <see cref="FormSection.Fields"/> list: per the <see cref="FormSection.Items"/> doc comment it is the
/// "flat authorization/order fallback" (and a pre-tree consumer still gets a complete form). Because the
/// tree now drives the runner, the group / collection VALUES nest under the item key
/// (per <c>SubmitValidationGate</c>) — so each definition's JSON Schema below is the NESTED value shape
/// the tree submits (arrays for collections, objects for groups), not a flat one.
/// </para>
/// <para>
/// <b>L3 reference resolution (now consumed by the render path).</b> The L3 definition's backing
/// <see cref="ReusableUnit"/> is registered + published through the REAL
/// <see cref="Harborline.Api.Foundation.Forms.IReusableUnitStore"/> (wired in
/// <see cref="Harborline.Api.LocalNodeHost.Data.Forms.NodeFormsComposition"/>, which also registers
/// <see cref="Harborline.Api.Foundation.Forms.IReuseResolver"/>). <c>FormEngine.RenderAsync</c> now expands the
/// reuse cascade before walking the tree, so the referenced premises-address block resolves (CP-locked)
/// into the live GET view — and the resolver still REJECTS a CP-lock override fail-closed
/// (<c>reuse.locked_field_override</c>), proven by <c>FormsShowcaseDevSeederTests</c>.
/// </para>
/// </remarks>
public sealed class FormsShowcaseDevSeeder : IHostedService
{
    /// <summary>L0 — flat: a contact/enquiry form (mixed field types, one section, no nesting).</summary>
    public const string L0ContactEnquiryFormId = "showcase-contact-enquiry.v1";

    /// <summary>L1 — sectioned: a rental application (groups, a content block, an open-url action,
    /// a conditional-visibility rule, validation).</summary>
    public const string L1RentalApplicationFormId = "showcase-rental-application.v1";

    /// <summary>L2 — collections: an invoice-lines form (two sibling Collections with stable keys +
    /// instance bounds + a table presentation).</summary>
    public const string L2InvoiceLinesFormId = "showcase-invoice-lines.v1";

    /// <summary>L3 — reference reuse: a form embedding a version-pinned, CP-locked
    /// <see cref="ReusableUnit"/> (a reusable "premises address" catalog block).</summary>
    public const string L3CatalogReferenceFormId = "showcase-catalog-reference.v1";

    /// <summary>The backing reusable unit L3 references.</summary>
    public static readonly ReusableUnitId L3AddressBlockUnitId = new("catalog/premises-address-block");

    /// <summary>L4 — deep: a scored mini living-standard inspection (2 disciplines × 3 categories ×
    /// condition-rating questions, one safety pass/fail, one measurement, nested groups to depth 4).</summary>
    public const string L4LivingStandardInspectionFormId = "showcase-living-standard-inspection.v1";

    /// <summary>The union of roles every showcase section grants — mirrors
    /// <see cref="FormsDevSeeder"/>'s <c>OperatorSectionRoles</c> (single-operator full authority, matching
    /// both the live host's "Admin" display role and the route-test harness's "node:operator").</summary>
    private static readonly string[] OperatorRoles =
        new[] { FormsRoutes.NodeOperatorRole, "Admin", "Member", "Viewer" };

    private readonly ISchemaRegistry _schemaRegistry;
    private readonly AuthorizedFormDefinitionLifecycle _formStore;
    private readonly IReusableUnitStore _unitStore;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FormsShowcaseDevSeeder> _logger;

    public FormsShowcaseDevSeeder(
        ISchemaRegistry schemaRegistry,
        AuthorizedFormDefinitionLifecycle formStore,
        IReusableUnitStore unitStore,
        IActiveTeamAccessor activeTeam,
        IHostEnvironment environment,
        ILogger<FormsShowcaseDevSeeder> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(formStore);
        ArgumentNullException.ThrowIfNull(unitStore);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _schemaRegistry = schemaRegistry;
        _formStore = formStore;
        _unitStore = unitStore;
        _activeTeam = activeTeam;
        _environment = environment;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!CalendarDevSeeder.ShouldSeed(_environment))
        {
            _logger.LogDebug(
                "FormsShowcaseDevSeeder: environment '{Environment}' is not development and no dev-seed flag "
                + "is set — skipping the prototype-showcase form ladder (production-safe).",
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
                "FormsShowcaseDevSeeder: no active team resolved — skipping the showcase ladder. (Register "
                + "AFTER MultiTeamBootstrapHostedService.)");
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // L3's backing reusable unit MUST exist (published) before the L3 definition references it.
        await SeedAddressBlockUnitAsync(tenantId, now, cancellationToken).ConfigureAwait(false);

        try
        {
            await SeedFormAsync(L0ContactEnquiryFormId, BuildL0ContactEnquiry, tenantId, now, cancellationToken)
                .ConfigureAwait(false);
            await SeedFormAsync(L1RentalApplicationFormId, BuildL1RentalApplication, tenantId, now, cancellationToken)
                .ConfigureAwait(false);
            await SeedFormAsync(L2InvoiceLinesFormId, BuildL2InvoiceLines, tenantId, now, cancellationToken)
                .ConfigureAwait(false);
            await SeedFormAsync(L3CatalogReferenceFormId, BuildL3CatalogReference, tenantId, now, cancellationToken)
                .ConfigureAwait(false);
            await SeedFormAsync(
                    L4LivingStandardInspectionFormId, BuildL4LivingStandardInspection, tenantId, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AuthorizationDeniedException ex)
        {
            _logger.LogWarning(
                ex,
                "FormsShowcaseDevSeeder: the development seed principal is not authorized for tenant {TenantId} — skipping the showcase ladder.",
                tenantId);
            return;
        }

        _logger.LogInformation(
            "FormsShowcaseDevSeeder: published the 5-level prototype-showcase form ladder (L0–L4) for tenant "
            + "{TenantId} — open each at /form-builder?form=<id> and /forms?form=<id> in the Harborline app.",
            tenantId);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Shared seed plumbing ─────────────────────────────────────────────────────────────────────────

    private async Task SeedAddressBlockUnitAsync(TenantId tenant, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await _unitStore.GetCurrentPublishedAsync(tenant, L3AddressBlockUnitId, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return; // idempotent.
        }

        var body = new FormComponentBody(
            Items: new[]
            {
                FormItem.OfGroup(
                    "premisesAddress",
                    new[]
                    {
                        FormItem.OfField("addressLine1"),
                        FormItem.OfField("addressCity"),
                        FormItem.OfField("addressEmirate"),
                    },
                    Bilingual("Premises address", "عنوان المبنى")),
            },
            Fields: new Dictionary<string, FieldOverlay>
            {
                ["addressLine1"] = new(Bilingual("Address line 1", "العنوان - السطر 1"), ControlHint: "text"),
                ["addressCity"] = new(Bilingual("City", "المدينة"), ControlHint: "text"),
                ["addressEmirate"] = new(Bilingual("Emirate", "الإمارة"), ControlHint: "select"),
            });

        var unit = new ReusableUnit(
            Id: L3AddressBlockUnitId,
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Kind: ReusableUnitKind.FormComponent,
            Owner: IdentityRef.System,
            Component: body,
            Title: Bilingual("Premises address block", "كتلة عنوان المبنى"),
            CreatedAt: now,
            UpdatedAt: now);

        await _unitStore.RegisterAsync(unit, ct).ConfigureAwait(false);
        await _unitStore.PublishAsync(tenant, L3AddressBlockUnitId, unit.Version, ct).ConfigureAwait(false);
    }

    private async Task SeedFormAsync(
        string formId,
        Func<TenantId, SchemaId, DateTimeOffset, FormDefinition> build,
        TenantId tenant,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var authority = new AuthorizationWriteContext(
            new ActorId("installer:development-form-showcase-seed"), tenant, now);
        var decision = await _formStore.DecideAsync(formId, authority, ct).ConfigureAwait(false);
        var existing = await _formStore
            .GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId), ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return; // idempotent — a restart re-runs cleanly.
        }

        var (schemaJson, buildDef) = SchemaFor(formId);
        var schema = await _schemaRegistry.RegisterAsync(schemaJson, ct: ct).ConfigureAwait(false);
        var definition = build(tenant, schema.Id, now);

        await _formStore.RegisterAndPublishAsync(definition, decision, ct).ConfigureAwait(false);
    }

    /// <summary>Maps a form id to its (schema JSON, unused-marker) so <see cref="SeedFormAsync"/> can
    /// register the schema before building the definition. The build delegate ignores the marker;
    /// this indirection just keeps the schema text next to its definition builder below.</summary>
    private static (string Json, object Marker) SchemaFor(string formId) => formId switch
    {
        L0ContactEnquiryFormId => (L0SchemaJson, formId),
        L1RentalApplicationFormId => (L1SchemaJson, formId),
        L2InvoiceLinesFormId => (L2SchemaJson, formId),
        L3CatalogReferenceFormId => (L3SchemaJson, formId),
        L4LivingStandardInspectionFormId => (L4SchemaJson, formId),
        _ => throw new ArgumentOutOfRangeException(nameof(formId), formId, "unknown showcase form id"),
    };

    /// <summary>Bilingual (en default + ar) <see cref="InternationalizedText"/> helper.</summary>
    private static InternationalizedText Bilingual(string en, string ar)
        => new("en", new Dictionary<string, string> { ["en"] = en, ["ar"] = ar });

    private static SectionAccess FullAccess() => new(OperatorRoles, OperatorRoles);

    // ── L0 — flat: contact / enquiry ─────────────────────────────────────────────────────────────────

    private static FormDefinition BuildL0ContactEnquiry(TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
        => new(
            Id: new FormDefinitionId(L0ContactEnquiryFormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["fullName"] = new(Bilingual("Full name", "الاسم الكامل"), ControlHint: "text"),
                    ["email"] = new(Bilingual("Email", "البريد الإلكتروني"), ControlHint: "text"),
                    ["phone"] = new(Bilingual("Phone", "الهاتف"), ControlHint: "text"),
                    ["enquiryType"] = new(Bilingual("Enquiry type", "نوع الاستفسار"), ControlHint: "select"),
                    ["preferredContactDate"] = new(
                        Bilingual("Preferred contact date", "تاريخ التواصل المفضل"), ControlHint: "date"),
                    ["budgetCeiling"] = new(
                        Bilingual("Budget ceiling (AED)", "سقف الميزانية (درهم)"),
                        ControlHint: "currency",
                        Config: new FieldConfig(CurrencyCode: "AED")),
                    ["subscribeUpdates"] = new(
                        Bilingual("Send me availability updates", "أرسل لي تحديثات التوفر"), ControlHint: "checkbox"),
                    ["message"] = new(Bilingual("Message", "الرسالة"), ControlHint: "textarea"),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "enquiry",
                        Title: Bilingual("Contact us", "تواصل معنا"),
                        Fields: new[]
                        {
                            "fullName", "email", "phone", "enquiryType",
                            "preferredContactDate", "budgetCeiling", "subscribeUpdates", "message",
                        },
                        Access: FullAccess()),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Contact / Enquiry", "تواصل / استفسار"),
                Description: Bilingual(
                    "L0 (flat) — a single-section contact form. The complexity-ladder floor: no groups, "
                    + "no collections, no nesting.",
                    "المستوى صفر (مسطّح) — نموذج تواصل بقسم واحد.")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);

    private const string L0SchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "fullName": { "type": "string", "minLength": 1 },
            "email": { "type": "string", "format": "email" },
            "phone": { "type": "string" },
            "enquiryType": { "type": "string", "enum": ["leasing", "sales", "maintenance", "general"] },
            "preferredContactDate": { "type": "string", "format": "date" },
            "budgetCeiling": { "type": "number", "minimum": 0 },
            "subscribeUpdates": { "type": "boolean" },
            "message": { "type": "string" }
          },
          "required": ["fullName", "email", "enquiryType"],
          "additionalProperties": false
        }
        """;

    // ── L1 — sectioned: rental application ───────────────────────────────────────────────────────────

    private static FormDefinition BuildL1RentalApplication(TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
        => new(
            Id: new FormDefinitionId(L1RentalApplicationFormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["applicantName"] = new(Bilingual("Applicant name", "اسم مقدم الطلب"), ControlHint: "text"),
                    ["applicantEmail"] = new(Bilingual("Email", "البريد الإلكتروني"), ControlHint: "text"),
                    ["unitType"] = new(Bilingual("Unit type", "نوع الوحدة"), ControlHint: "select"),
                    ["desiredMoveIn"] = new(Bilingual("Desired move-in date", "تاريخ الانتقال المطلوب"), ControlHint: "date"),
                    ["monthlyIncome"] = new(
                        Bilingual("Monthly income (AED)", "الدخل الشهري (درهم)"),
                        ControlHint: "currency",
                        Config: new FieldConfig(CurrencyCode: "AED")),
                    ["hasGuarantor"] = new(Bilingual("Has a guarantor", "لديه ضامن"), ControlHint: "checkbox"),
                    ["guarantorName"] = new(Bilingual("Guarantor name", "اسم الضامن"), ControlHint: "text"),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "applicant",
                        Title: Bilingual("Applicant", "مقدم الطلب"),
                        Fields: new[] { "applicantName", "applicantEmail", "unitType", "desiredMoveIn" },
                        Access: FullAccess(),
                        Items: new[]
                        {
                            FormItem.OfContent(
                                "applicantIntro",
                                new[]
                                {
                                    new ContentNode(
                                        ContentNodeKinds.Heading,
                                        Bilingual("Before you begin", "قبل أن تبدأ"),
                                        Level: 3),
                                    new ContentNode(
                                        ContentNodeKinds.Paragraph,
                                        Bilingual(
                                            "Applications are reviewed within two business days.",
                                            "تتم مراجعة الطلبات خلال يومي عمل.")),
                                }),
                            FormItem.OfGroup(
                                "applicant",
                                new[]
                                {
                                    FormItem.OfField("applicantName"),
                                    FormItem.OfField("applicantEmail"),
                                    FormItem.OfField("unitType"),
                                    FormItem.OfField("desiredMoveIn"),
                                },
                                Bilingual("Applicant details", "بيانات مقدم الطلب")),
                            FormItem.OfAction(
                                "viewListings",
                                new FormActionConfig(
                                    FormActionKinds.OpenUrl,
                                    Bilingual("View available listings", "عرض الوحدات المتاحة"),
                                    Url: "https://example.harborline-software.com/listings")),
                        }),
                    new FormSection(
                        Id: "financial",
                        Title: Bilingual("Financial", "البيانات المالية"),
                        Fields: new[] { "monthlyIncome", "hasGuarantor", "guarantorName" },
                        Access: FullAccess(),
                        Items: new[]
                        {
                            FormItem.OfGroup(
                                "financial",
                                new[]
                                {
                                    FormItem.OfField("monthlyIncome"),
                                    FormItem.OfField("hasGuarantor"),
                                    FormItem.OfField("guarantorName"),
                                },
                                Bilingual("Financial details", "البيانات المالية")),
                        }),
                },
                // Rev-7 nesting note: the applicant / financial groups nest their fields under the group
                // key, so a per-field conditional-visibility rule keyed on a sibling field (guarantorName
                // shown-when hasGuarantor) needs a NESTED rule target the SPINE-1 field-grain cell address
                // does not yet express. Rather than ship a rule that silently no-ops under nesting, L1
                // omits it — the nested-path rule target is the documented follow-up (see the showcase
                // index). L1 still showcases groups, a content block, and an open-url action.
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Rental Application", "طلب استئجار"),
                Description: Bilingual(
                    "L1 (sectioned) — groups (nested fieldsets), a content block, and an open-url action.",
                    "المستوى الأول (بأقسام) — مجموعات (حقول متداخلة) وكتلة محتوى وإجراء رابط.")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);

    // Rev-7 nested value shape: the applicant / financial GROUPS nest their fields under the group key
    // (per SubmitValidationGate), so the candidate is { applicant: {…}, financial: {…} }. The content +
    // action items carry no value. The schema mirrors the two group objects.
    private const string L1SchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "applicant": {
              "type": "object",
              "properties": {
                "applicantName": { "type": "string", "minLength": 1 },
                "applicantEmail": { "type": "string", "format": "email" },
                "unitType": { "type": "string", "enum": ["studio", "one-bed", "two-bed", "three-bed"] },
                "desiredMoveIn": { "type": "string", "format": "date" }
              },
              "required": ["applicantName", "applicantEmail", "unitType", "desiredMoveIn"],
              "additionalProperties": false
            },
            "financial": {
              "type": "object",
              "properties": {
                "monthlyIncome": { "type": "number", "minimum": 0 },
                "hasGuarantor": { "type": "boolean" },
                "guarantorName": { "type": "string" }
              },
              "additionalProperties": false
            }
          },
          "required": ["applicant"],
          "additionalProperties": false
        }
        """;

    // ── L2 — collections: invoice lines + occupants ──────────────────────────────────────────────────

    private static FormDefinition BuildL2InvoiceLines(TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
        => new(
            Id: new FormDefinitionId(L2InvoiceLinesFormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["customerName"] = new(Bilingual("Customer name", "اسم العميل"), ControlHint: "text"),
                    ["invoiceNumber"] = new(Bilingual("Invoice number", "رقم الفاتورة"), ControlHint: "text"),
                    ["lineDescription"] = new(Bilingual("Description", "الوصف"), ControlHint: "text"),
                    ["lineQuantity"] = new(Bilingual("Quantity", "الكمية"), ControlHint: "number"),
                    ["lineUnitPrice"] = new(
                        Bilingual("Unit price (AED)", "سعر الوحدة (درهم)"),
                        ControlHint: "currency",
                        Config: new FieldConfig(CurrencyCode: "AED")),
                    ["occupantName"] = new(Bilingual("Occupant name", "اسم الشاغل"), ControlHint: "text"),
                    ["occupantRelationship"] = new(
                        Bilingual("Relationship to lessee", "العلاقة بالمستأجر"), ControlHint: "select"),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "billing",
                        Title: Bilingual("Invoice", "الفاتورة"),
                        Fields: new[]
                        {
                            "customerName", "invoiceNumber",
                            "lineDescription", "lineQuantity", "lineUnitPrice",
                            "occupantName", "occupantRelationship",
                        },
                        Access: FullAccess(),
                        Items: new FormItem[]
                        {
                            FormItem.OfField("customerName"),
                            FormItem.OfField("invoiceNumber"),
                            FormItem.OfCollection(
                                "lineItems",
                                new[]
                                {
                                    FormItem.OfField("lineDescription"),
                                    FormItem.OfField("lineQuantity"),
                                    FormItem.OfField("lineUnitPrice"),
                                },
                                cardinality: new Cardinality(Min: 1, Max: 50),
                                title: Bilingual("Line items", "بنود الفاتورة"))
                            with
                            {
                                Table = new CollectionTableConfig(
                                    Columns: new Dictionary<string, CollectionColumn>
                                    {
                                        ["lineDescription"] = new(Width: "1/2"),
                                        ["lineQuantity"] = new(Width: "1/4", Align: "end"),
                                        ["lineUnitPrice"] = new(Width: "1/4", Align: "end"),
                                    },
                                    Totals: new[] { "lineUnitPrice" }),
                            },
                            // A sibling collection at the SAME section — showcases fixed sibling rendering
                            // (two independent repeatable lists side by side under one section).
                            FormItem.OfCollection(
                                "occupants",
                                new[]
                                {
                                    FormItem.OfField("occupantName"),
                                    FormItem.OfField("occupantRelationship"),
                                },
                                cardinality: new Cardinality(Min: 0, Max: 12),
                                title: Bilingual("Occupants", "الشاغلون")),
                        }),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Invoice Lines", "بنود الفاتورة"),
                Description: Bilingual(
                    "L2 (collections) — a table-presented Collection with stable keys + instance bounds, "
                    + "and a sibling Collection.",
                    "المستوى الثاني (مجموعات) — تجميعة معروضة كجدول مع حدود على عدد العناصر.")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);

    // Rev-7 nested value shape (the render engine walks Section.Items ⇒ a Collection's rows nest as an
    // ARRAY of row objects under the collection key, per SubmitValidationGate). The schema mirrors that:
    // lineItems / occupants are arrays of the row template, cardinality mirrored as minItems / maxItems.
    private const string L2SchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "customerName": { "type": "string", "minLength": 1 },
            "invoiceNumber": { "type": "string", "minLength": 1 },
            "lineItems": {
              "type": "array",
              "minItems": 1,
              "maxItems": 50,
              "items": {
                "type": "object",
                "properties": {
                  "lineDescription": { "type": "string" },
                  "lineQuantity": { "type": "number", "minimum": 0 },
                  "lineUnitPrice": { "type": "number", "minimum": 0 }
                },
                "additionalProperties": false
              }
            },
            "occupants": {
              "type": "array",
              "maxItems": 12,
              "items": {
                "type": "object",
                "properties": {
                  "occupantName": { "type": "string" },
                  "occupantRelationship": { "type": "string", "enum": ["spouse", "child", "parent", "other"] }
                },
                "additionalProperties": false
              }
            }
          },
          "required": ["customerName", "invoiceNumber", "lineItems"],
          "additionalProperties": false
        }
        """;

    // ── L3 — reference reuse: catalog ReusableUnit ───────────────────────────────────────────────────

    private static FormDefinition BuildL3CatalogReference(TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
        => new(
            Id: new FormDefinitionId(L3CatalogReferenceFormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["accountName"] = new(Bilingual("Account name", "اسم الحساب"), ControlHint: "text"),
                    // The referenced unit's own field keys (addressLine1/addressCity/addressEmirate) are
                    // DELIBERATELY NOT declared here. ReuseResolver correctly rejects a consumer that
                    // redeclares a unit-owned field as a CP-lock-override attempt
                    // (reuse.locked_field_override) — proven by FormsShowcaseDevSeederTests. So the flat
                    // runner fallback for THIS definition can only ever carry "accountName"; the
                    // referenced premises-address block does not appear in a live GET view until the
                    // render engine walks Section.Items (the known gap — see the class remarks).
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "account",
                        Title: Bilingual("Account", "الحساب"),
                        Fields: new[] { "accountName" },
                        Access: FullAccess(),
                        Items: new FormItem[]
                        {
                            FormItem.OfField("accountName"),
                            FormItem.OfReference(
                                "premisesAddress",
                                new ReusableUnitRef(
                                    L3AddressBlockUnitId,
                                    ReusableUnitVersionSelector.Pin(new SemanticVersion(1, 0, 0)))),
                        }),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Catalog Reference Demo", "عرض توضيحي للمرجع"),
                Description: Bilingual(
                    "L3 (reference reuse) — embeds the version-pinned, CP-locked "
                    + "\"catalog/premises-address-block\" ReusableUnit.",
                    "المستوى الثالث (إعادة استخدام مرجعية) — يضمّن وحدة عنوان قابلة لإعادة الاستخدام.")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);

    // Rev-7 nested value shape: the resolver expands the `premisesAddress` Reference into a GROUP, so the
    // referenced unit's fields nest under `premisesAddress` in the candidate. The schema carries that
    // nested object (the unit-owned keys are CP-locked in the OVERLAY — not redeclared there — but the
    // VALUE schema must still describe the shape the runner submits).
    private const string L3SchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "accountName": { "type": "string", "minLength": 1 },
            "premisesAddress": {
              "type": "object",
              "properties": {
                "addressLine1": { "type": "string" },
                "addressCity": { "type": "string" },
                "addressEmirate": { "type": "string" }
              },
              "additionalProperties": false
            }
          },
          "required": ["accountName"],
          "additionalProperties": false
        }
        """;

    // ── L4 — deep: living-standard mini inspection ───────────────────────────────────────────────────

    private static FormDefinition BuildL4LivingStandardInspection(
        TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
    {
        // 2 disciplines x 3 categories x ~2 rating questions (kept to ~2 per category so the whole tree
        // stays well under the depth-8 / fan-out caps while still nesting groups 4 deep: discipline >
        // category > questions-group > field). One safety pass/fail leaf + one free-text measurement leaf
        // round out the "8ish rating questions" scope at a showcase-appropriate size.
        FormItem RatingQuestion(string key, string en, string ar)
            => FormItem.OfField(key);

        FormItem Category(string key, string en, string ar, params FormItem[] questions)
            => FormItem.OfGroup(key, questions, Bilingual(en, ar));

        FormItem Discipline(string key, string en, string ar, params FormItem[] categories)
            => FormItem.OfGroup(key, categories, Bilingual(en, ar));

        var fields = new Dictionary<string, FieldOverlay>
        {
            ["electricalWiringRating"] = ConditionRatingField("Wiring condition", "حالة الأسلاك"),
            ["electricalPanelRating"] = ConditionRatingField("Panel condition", "حالة اللوحة"),
            ["electricalOutletsRating"] = ConditionRatingField("Outlets condition", "حالة المقابس"),
            ["electricalLightingRating"] = ConditionRatingField("Lighting condition", "حالة الإضاءة"),
            ["plumbingSupplyRating"] = ConditionRatingField("Supply lines condition", "حالة خطوط الإمداد"),
            ["plumbingDrainageRating"] = ConditionRatingField("Drainage condition", "حالة الصرف"),
            ["plumbingFixturesRating"] = ConditionRatingField("Fixtures condition", "حالة التجهيزات"),
            ["plumbingWaterHeaterRating"] = ConditionRatingField("Water heater condition", "حالة سخان المياه"),
            ["smokeDetectorPass"] = new(
                Bilingual("Smoke detector pass/fail", "نجاح/فشل كاشف الدخان"), ControlHint: "checkbox"),
            ["waterPressureMeasurement"] = new(
                Bilingual("Water pressure (bar)", "ضغط الماء (بار)"), ControlHint: "text"),
        };

        var electrical = Discipline(
            "electrical",
            "Electrical",
            "الكهرباء",
            Category(
                "wiring",
                "Wiring",
                "الأسلاك",
                RatingQuestion("electricalWiringRating", "Wiring condition", "حالة الأسلاك")),
            Category(
                "panel",
                "Panel",
                "اللوحة",
                RatingQuestion("electricalPanelRating", "Panel condition", "حالة اللوحة")),
            Category(
                "fixtures",
                "Outlets & lighting",
                "المقابس والإضاءة",
                RatingQuestion("electricalOutletsRating", "Outlets condition", "حالة المقابس"),
                RatingQuestion("electricalLightingRating", "Lighting condition", "حالة الإضاءة"),
                FormItem.OfField("smokeDetectorPass")));

        var plumbing = Discipline(
            "plumbing",
            "Plumbing",
            "السباكة",
            Category(
                "supply",
                "Supply",
                "الإمداد",
                RatingQuestion("plumbingSupplyRating", "Supply lines condition", "حالة خطوط الإمداد"),
                FormItem.OfField("waterPressureMeasurement")),
            Category(
                "drainage",
                "Drainage",
                "الصرف",
                RatingQuestion("plumbingDrainageRating", "Drainage condition", "حالة الصرف")),
            Category(
                "fixtures",
                "Fixtures & heating",
                "التجهيزات والتسخين",
                RatingQuestion("plumbingFixturesRating", "Fixtures condition", "حالة التجهيزات"),
                RatingQuestion("plumbingWaterHeaterRating", "Water heater condition", "حالة سخان المياه")));

        return new FormDefinition(
            Id: new FormDefinitionId(L4LivingStandardInspectionFormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: fields,
                Sections: new[]
                {
                    new FormSection(
                        Id: "livingStandard",
                        Title: Bilingual("Living-standard inspection", "فحص معيار السكن"),
                        // Flat fallback: every leaf field key, in a sensible reading order.
                        Fields: fields.Keys.ToArray(),
                        Access: FullAccess(),
                        // Nested tree: discipline (depth 1) > category (depth 2) > field / question-group
                        // (depth 3–4) — a deliberate showcase of the structure matrix, under the depth-8 cap.
                        Items: new[] { electrical, plumbing }),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Living-Standard Inspection (mini)", "فحص معيار السكن (مصغّر)"),
                Description: Bilingual(
                    "L4 (deep) — 2 disciplines x 3 categories of condition-rating questions, one safety "
                    + "pass/fail, one measurement, nested groups to depth 4.",
                    "المستوى الرابع (عميق) — تخصصان × ثلاث فئات من أسئلة تقييم الحالة.")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    /// <summary>A 1–5 condition-rating field (Wave 2c <c>ConditionRatingControl</c> control hint — the
    /// same hint the equipment-inspection demo form uses).</summary>
    private static FieldOverlay ConditionRatingField(string en, string ar)
        => new(Bilingual(en, ar), HelpText: Bilingual("1 = failed, 5 = excellent.", "1 = غير صالح، 5 = ممتاز."),
            ControlHint: "condition-rating");

    // Rev-7 nested value shape: the render engine walks Section.Items ⇒ a Group's children nest under the
    // group key (per SubmitValidationGate), so the depth-4 discipline → category → field tree produces a
    // nested candidate ({ electrical: { wiring: { electricalWiringRating } … } … }). The schema mirrors that
    // tree exactly; the rating leaves keep their disambiguated field keys inside their category object. The
    // L4 e2e projects the nested wiring rating via the RFC-6901 pointer /electrical/wiring/electricalWiringRating.
    private const string L4SchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "electrical": {
              "type": "object",
              "properties": {
                "wiring": {
                  "type": "object",
                  "properties": { "electricalWiringRating": { "type": "integer", "minimum": 1, "maximum": 5 } },
                  "required": ["electricalWiringRating"],
                  "additionalProperties": false
                },
                "panel": {
                  "type": "object",
                  "properties": { "electricalPanelRating": { "type": "integer", "minimum": 1, "maximum": 5 } },
                  "additionalProperties": false
                },
                "fixtures": {
                  "type": "object",
                  "properties": {
                    "electricalOutletsRating": { "type": "integer", "minimum": 1, "maximum": 5 },
                    "electricalLightingRating": { "type": "integer", "minimum": 1, "maximum": 5 },
                    "smokeDetectorPass": { "type": "boolean" }
                  },
                  "additionalProperties": false
                }
              },
              "required": ["wiring"],
              "additionalProperties": false
            },
            "plumbing": {
              "type": "object",
              "properties": {
                "supply": {
                  "type": "object",
                  "properties": {
                    "plumbingSupplyRating": { "type": "integer", "minimum": 1, "maximum": 5 },
                    "waterPressureMeasurement": { "type": "string" }
                  },
                  "required": ["plumbingSupplyRating"],
                  "additionalProperties": false
                },
                "drainage": {
                  "type": "object",
                  "properties": { "plumbingDrainageRating": { "type": "integer", "minimum": 1, "maximum": 5 } },
                  "additionalProperties": false
                },
                "fixtures": {
                  "type": "object",
                  "properties": {
                    "plumbingFixturesRating": { "type": "integer", "minimum": 1, "maximum": 5 },
                    "plumbingWaterHeaterRating": { "type": "integer", "minimum": 1, "maximum": 5 }
                  },
                  "additionalProperties": false
                }
              },
              "required": ["supply"],
              "additionalProperties": false
            }
          },
          "required": ["electrical", "plumbing"],
          "additionalProperties": false
        }
        """;
}
