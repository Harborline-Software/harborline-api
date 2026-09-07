using System.Text.Json;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>Non-mutating advisory validation for the dogfood scheduling authoring contract.</summary>
public sealed class SchedulingDraftValidator
{
    public const string DraftSchema = "harborline.scheduling-definition-draft/v0";

    public IReadOnlyList<SchedulingValidationIssue> Validate(JsonElement definition)
    {
        var issues = new List<SchedulingValidationIssue>();
        if (definition.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("scheduling.validation.object_required", "$"));
            return issues;
        }
        RequiredString(definition, "title", issues);
        RequiredString(definition, "timezone", issues);
        RequiredArray(definition, "activities", issues);
        RequiredArray(definition, "resourceRequirements", issues);
        OptionalPolicyDuration(definition, "bufferBeforeMinutes", issues);
        OptionalPolicyDuration(definition, "bufferAfterMinutes", issues);
        OptionalPolicyDuration(definition, "minimumLeadTimeMinutes", issues);
        if (definition.TryGetProperty("schema", out var schema)
            && (schema.ValueKind != JsonValueKind.String || schema.GetString() != DraftSchema))
            issues.Add(new("scheduling.validation.schema_unsupported", "schema"));
        else if (!definition.TryGetProperty("schema", out _))
            issues.Add(new("scheduling.validation.string_required", "schema"));

        if (definition.TryGetProperty("timezone", out var timezone)
            && timezone.ValueKind == JsonValueKind.String)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(timezone.GetString()!); }
            catch (TimeZoneNotFoundException) { issues.Add(new("scheduling.validation.timezone_unknown", "timezone")); }
            catch (InvalidTimeZoneException) { issues.Add(new("scheduling.validation.timezone_unknown", "timezone")); }
        }

        if (definition.TryGetProperty("activities", out var activities)
            && activities.ValueKind == JsonValueKind.Array)
        {
            if (activities.GetArrayLength() is 0 or > 100)
                issues.Add(new("scheduling.validation.activities_count_invalid", "activities"));
            var index = 0;
            foreach (var activity in activities.EnumerateArray())
            {
                if (activity.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new("scheduling.validation.object_required", $"activities.{index}"));
                }
                else
                {
                    RequiredString(activity, "id", issues, $"activities.{index}.id");
                    if (!activity.TryGetProperty("durationMinutes", out var duration)
                        || duration.ValueKind != JsonValueKind.Number
                        || !duration.TryGetInt32(out var minutes)
                        || minutes is < 1 or > 10_080)
                        issues.Add(new("scheduling.validation.duration_invalid", $"activities.{index}.durationMinutes"));
                }
                index++;
            }
        }
        if (definition.TryGetProperty("resourceRequirements", out var requirements)
            && requirements.ValueKind == JsonValueKind.Array)
        {
            if (requirements.GetArrayLength() > 20)
                issues.Add(new("scheduling.validation.resource_count_invalid", "resourceRequirements"));
            var index = 0;
            foreach (var requirement in requirements.EnumerateArray())
            {
                if (requirement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(requirement.GetString())
                    || requirement.GetString()!.Length > 100)
                    issues.Add(new("scheduling.validation.resource_invalid", $"resourceRequirements.{index}"));
                index++;
            }
        }
        return issues;
    }

    private static void RequiredString(
        JsonElement root,
        string name,
        List<SchedulingValidationIssue> issues,
        string? path = null)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            issues.Add(new("scheduling.validation.string_required", path ?? name));
    }

    private static void RequiredArray(JsonElement root, string name, List<SchedulingValidationIssue> issues)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            issues.Add(new("scheduling.validation.array_required", name));
    }

    private static void OptionalPolicyDuration(
        JsonElement root,
        string name,
        List<SchedulingValidationIssue> issues)
    {
        if (!root.TryGetProperty(name, out var value)) return;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var minutes)
            || minutes is < 0 or > SchedulingAppointmentPolicy.MaximumPolicyMinutes)
            issues.Add(new("scheduling.validation.policy_duration_invalid", name));
    }
}

public sealed record SchedulingValidationIssue(string Code, string Path);
