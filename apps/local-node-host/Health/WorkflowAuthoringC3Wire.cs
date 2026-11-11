using System.Globalization;
using System.Text.Json;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Locations in the submitted workflow; never definition targets or exception prose.</summary>
internal static class WorkflowAuthoringC3Wire
{
    internal static string Locator(JsonElement body, string locator)
    {
        if (string.IsNullOrEmpty(locator)) return "";
        foreach (var collection in new[] { "states", "transitions", "actions" })
            foreach (var (item, pointer) in Elements(body, collection))
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() == locator) return pointer;
        return "";
    }

    internal static string GateShape(JsonElement body, GateReferenceShapeException exception) =>
        FindLane(body, exception.Field, lane => lane.ValueKind == JsonValueKind.Array
            && lane.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.String
                && entry.GetString() == exception.Value));

    internal static string GateLane(JsonElement body, WorkflowGateLaneException exception) =>
        FindLane(body, exception.Field, lane => exception.Index == -1
            ? lane.ValueKind != JsonValueKind.Array
            : lane.ValueKind == JsonValueKind.Array && lane.GetArrayLength() > exception.Index
                && (lane[exception.Index].ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(lane[exception.Index].GetString())));

    internal static string Gate(JsonElement body, string gate, string rule)
    {
        var separator = gate.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0) return "";
        var collection = gate[..separator] switch
        {
            "transition" => "transitions",
            "action" => "actions",
            _ => "",
        };
        var member = rule == RoleGateAdmissionRules.StandingPlatformOnly ? "requiredStandings" : "requiredRoles";
        foreach (var (item, pointer) in Elements(body, collection))
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() == gate[(separator + 1)..] && item.TryGetProperty(member, out _))
                return $"{pointer}/{member}";
        return "";
    }

    private static string FindLane(JsonElement body, string field, Func<JsonElement, bool> refused)
    {
        var parts = field.Split('.');
        if (parts.Length != 2) return "";
        var collection = parts[0] switch { "transition" => "transitions", "action" => "actions", _ => "" };
        foreach (var (item, pointer) in Elements(body, collection))
            if (item.TryGetProperty(parts[1], out var lane) && refused(lane)) return $"{pointer}/{parts[1]}";
        return "";
    }

    private static IEnumerable<(JsonElement Item, string Pointer)> Elements(JsonElement body, string collection)
    {
        if (!body.TryGetProperty(collection, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                yield return (item, $"/{collection}/{index.ToString(CultureInfo.InvariantCulture)}");
            index++;
        }
    }
}
