using System.Collections.Generic;

using Harborline.Api.Blocks.Assets.Registry.Catalogs;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// The item-catalog FORM for the residential living-standard slice (ADR 0101 Rev 3.1 Wave 3a) — the
/// server-side <see cref="FormDefinition"/> whose fields ARE the standard's items. The field NAMES are the
/// single source shared with <see cref="ResidentialLivingStandardCatalog"/> (which carries the scoring
/// overlay + bindings for the SAME fields), so a submission of this form projects a typed condition record
/// per rated item onto the inspected unit.
/// </summary>
/// <remarks>
/// The six rating items render via the Harborline App <c>condition-rating</c> control (1..5 grade picker); the four
/// pass/fail safety items render as checkboxes. <c>unit_ref</c> is the inspected unit's entity id — the
/// bindings' submission-field ref source. Bilingual en/ar labels (the Dubai first-customer path).
/// </remarks>
public static class LivingStandardCatalogForm
{
    /// <summary>The catalog form id (matches <see cref="ResidentialLivingStandardCatalog.FormId"/>).</summary>
    public const string FormId = ResidentialLivingStandardCatalog.FormId;

    private static readonly string[] SectionRoles =
        new[] { FormsRoutes.NodeOperatorRole, "Admin", "Member", "Viewer" };

    /// <summary>Builds the catalog <see cref="FormDefinition"/> (Draft) for the tenant.</summary>
    public static FormDefinition BuildDefinition(TenantId tenant, SchemaId schemaRef, DateTimeOffset now)
    {
        var fields = new Dictionary<string, FieldOverlay>
        {
            [ResidentialLivingStandardCatalog.UnitRefField] = new(
                Bilingual("Unit reference", "معرّف الوحدة"),
                HelpText: Bilingual("The inspected unit's asset id.", "معرّف أصل الوحدة الخاضعة للفحص."),
                ControlHint: "text"),
        };

        foreach (var (name, en, ar) in RatingLabels)
        {
            fields[name] = new(Bilingual(en, ar), ControlHint: "condition-rating");
        }
        foreach (var (name, en, ar) in SafetyLabels)
        {
            fields[name] = new(Bilingual(en, ar), ControlHint: "checkbox");
        }

        var order = new List<string> { ResidentialLivingStandardCatalog.UnitRefField };
        order.AddRange(ResidentialLivingStandardCatalog.RatingItems);
        order.AddRange(ResidentialLivingStandardCatalog.SafetyItems);

        return new FormDefinition(
            Id: new FormDefinitionId(FormId),
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
                        Id: "residential-standard",
                        Title: Bilingual("Residential living-standard inspection", "فحص معيار السكن للوحدة السكنية"),
                        Fields: order.ToArray(),
                        Access: new SectionAccess(ReadRoles: SectionRoles, WriteRoles: SectionRoles)),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: Bilingual("Residential living-standard inspection", "فحص معيار السكن للوحدة السكنية"),
                Description: Bilingual(
                    "A curated residential living-standard slice (electrical + plumbing).",
                    "شريحة منسّقة لمعيار السكن (الكهرباء والسباكة).")),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    /// <summary>
    /// The form's JSON Schema (2020-12): <c>unit_ref</c> (required string), the six rating items (integer
    /// 1..5, matching the binding scale), and the four pass/fail safety items (boolean).
    /// </summary>
    public static string SchemaJson()
    {
        var props = new List<string>
        {
            "\"unit_ref\": { \"type\": \"string\", \"minLength\": 1 }",
        };
        foreach (var item in ResidentialLivingStandardCatalog.RatingItems)
        {
            props.Add($"\"{item}\": {{ \"type\": \"integer\", \"minimum\": 1, \"maximum\": 5 }}");
        }
        foreach (var item in ResidentialLivingStandardCatalog.SafetyItems)
        {
            props.Add($"\"{item}\": {{ \"type\": \"boolean\" }}");
        }

        return "{\n"
            + "  \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",\n"
            + "  \"type\": \"object\",\n"
            + "  \"properties\": {\n    " + string.Join(",\n    ", props) + "\n  },\n"
            + "  \"required\": [\"unit_ref\"],\n"
            + "  \"additionalProperties\": false\n"
            + "}";
    }

    private static InternationalizedText Bilingual(string en, string ar)
        => new("en", new Dictionary<string, string> { ["en"] = en, ["ar"] = ar });

    private static readonly (string Name, string En, string Ar)[] RatingLabels =
    {
        (ResidentialLivingStandardCatalog.PanelCondition, "Electrical panel condition", "حالة اللوحة الكهربائية"),
        (ResidentialLivingStandardCatalog.OutletFunction, "Outlets functional", "عمل المنافذ الكهربائية"),
        (ResidentialLivingStandardCatalog.KitchenSink, "Kitchen sink condition", "حالة حوض المطبخ"),
        (ResidentialLivingStandardCatalog.RangeCondition, "Cooking range condition", "حالة موقد الطبخ"),
        (ResidentialLivingStandardCatalog.ToiletCondition, "Toilet condition", "حالة المرحاض"),
        (ResidentialLivingStandardCatalog.BathDrainage, "Bathroom drainage", "تصريف الحمام"),
    };

    private static readonly (string Name, string En, string Ar)[] SafetyLabels =
    {
        (ResidentialLivingStandardCatalog.ExposedWiring, "No exposed live wiring", "لا توجد أسلاك كهربائية مكشوفة"),
        (ResidentialLivingStandardCatalog.GasLeak, "No fuel-gas leak", "لا يوجد تسرّب للغاز"),
        (ResidentialLivingStandardCatalog.KitchenGfci, "Kitchen GFCI protection", "حماية قاطع التسرّب الأرضي في المطبخ"),
        (ResidentialLivingStandardCatalog.BathGfci, "Bathroom GFCI protection", "حماية قاطع التسرّب الأرضي في الحمام"),
    };
}
