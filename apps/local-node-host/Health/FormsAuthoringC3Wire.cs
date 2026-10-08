using System.Globalization;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;

using Harborline.Blocks.BuilderDefinitions;
using Harborline.Api.Foundation.Forms;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Pure Forms boundary projection of C3. Locations never become definition targets.</summary>
internal static class FormsAuthoringC3Wire
{
    internal static IResult Refuse(
        DefinitionAdmissionPhase stage, string code, string pointer, object? detail = null)
    {
        // These producers concern a prospective revision or an inline field/node, not a verified
        // fetchable definition. Deliberately expose no target parameter or definition lookup.
        var refusal = new DefinitionRefusal(code, pointer);
        return Results.UnprocessableEntity(new
        {
            stage = stage.ToString().ToLowerInvariant(),
            refusals = new[] { new WireRefusal(refusal.Code, refusal.Pointer, detail) },
        });
    }

    internal static string Field(string name, string member) =>
        $"/overlay/fields/{Escape(name)}/{member}";

    internal static string Metadata(string name, string member) =>
        $"/fieldsMeta/{Escape(name)}/{member}";

    internal static string ValidationPointer(string? code, string? field, bool schemaSynthesis = false)
    {
        if (field is not null && schemaSynthesis) return Metadata(field, "validations");
        if (field is not null && code is FormDefinitionCodes.LabelMissing or FormDefinitionCodes.LabelPlaceholder)
            return Field(field, "label");
        // Older validators supply only a code, so locate the affected aggregate rather than
        // inventing a leaf from exception prose or treating a node id as a JSON pointer.
        if (code?.StartsWith("form.pages.", StringComparison.Ordinal) == true
            || code == FormDefinitionCodes.RulesGuardUncompilable)
            return "/overlay/pages";
        if (code?.StartsWith("form.rules.", StringComparison.Ordinal) == true)
            return "/overlay/rules";
        if (code?.StartsWith("form.tree.", StringComparison.Ordinal) == true
            || code?.StartsWith("form.layout.", StringComparison.Ordinal) == true)
            return "/overlay/sections";
        if (code?.StartsWith("form.checks.", StringComparison.Ordinal) == true)
            return "/overlay/asyncChecks";
        if (code == "aspect.sensitive_input_unacknowledged") return "/overlay/asyncChecks";
        return "/overlay";
    }

    internal static string GatePointer(OverlayDto overlay, string gate, bool standing)
    {
        var lane = standing ? "Standings" : "Roles";
        var read = gate.EndsWith(".read", StringComparison.Ordinal);
        var write = gate.EndsWith(".write", StringComparison.Ordinal);
        if (read || write)
        {
            var prefix = gate[..gate.LastIndexOf('.')];
            var member = (read ? "read" : "write") + lane;
            if (prefix == "form") return $"/overlay/aspects/access/{member}";
            if (prefix.StartsWith("field:", StringComparison.Ordinal))
            {
                var name = prefix[6..];
                if (overlay.Fields.TryGetValue(name, out var field))
                {
                    var direct = standing ? (read ? field.ReadStandings : field.WriteStandings)
                        : (read ? field.ReadRoles : field.WriteRoles);
                    var aspect = standing ? (read ? field.Aspects?.Access?.ReadStandings : field.Aspects?.Access?.WriteStandings)
                        : (read ? field.Aspects?.Access?.ReadRoles : field.Aspects?.Access?.WriteRoles);
                    if (direct is { Count: > 0 } && aspect is { Count: > 0 })
                        return $"/overlay/fields/{Escape(name)}";
                    return Field(name, direct is { Count: > 0 } ? member : $"aspects/access/{member}");
                }
            }
            if (prefix.StartsWith("section:", StringComparison.Ordinal))
            {
                if (overlay.Sections.Count(section => section.Id == prefix[8..]) > 1)
                    return "/overlay/sections";
                for (var i = 0; i < overlay.Sections.Count; i++)
                    if (overlay.Sections[i].Id == prefix[8..])
                        return $"/overlay/sections/{i.ToString(CultureInfo.InvariantCulture)}/access/{member}";
            }
        }
        if (gate.StartsWith("fields.", StringComparison.Ordinal)) return "/overlay/fields";
        if (gate.StartsWith("sections.", StringComparison.Ordinal)) return "/overlay/sections";
        return "/overlay";
    }

    private static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private sealed record WireRefusal(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("pointer")] string Pointer,
        [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Detail);
}
