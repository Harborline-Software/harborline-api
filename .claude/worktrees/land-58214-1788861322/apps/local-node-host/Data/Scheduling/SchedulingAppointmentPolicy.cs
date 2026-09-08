using System.Text.Json;

using Harborline.Api.Blocks.Calendar.Models;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>Server-authoritative per-appointment-type timing policy.</summary>
public sealed record SchedulingAppointmentPolicy(
    int DurationMinutes,
    int BufferBeforeMinutes,
    int BufferAfterMinutes,
    int MinimumLeadTimeMinutes)
{
    public const int MaximumPolicyMinutes = 10_080;

    public EventPadding Padding => EventPadding.Of(
        TimeSpan.FromMinutes(BufferBeforeMinutes),
        TimeSpan.FromMinutes(BufferAfterMinutes));

    public DateTimeOffset EndFor(DateTimeOffset startUtc) => startUtc.AddMinutes(DurationMinutes);

    public static bool TryFromDefinition(JsonElement definition, out SchedulingAppointmentPolicy? policy)
    {
        policy = null;
        if (definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty("activities", out var activities)
            || activities.ValueKind != JsonValueKind.Array
            || activities.GetArrayLength() == 0)
            return false;

        var activity = activities[0];
        if (activity.ValueKind != JsonValueKind.Object
            || !TryRequiredMinutes(activity, "durationMinutes", minimum: 1, out var duration)
            || !TryOptionalMinutes(definition, "bufferBeforeMinutes", out var before)
            || !TryOptionalMinutes(definition, "bufferAfterMinutes", out var after)
            || !TryOptionalMinutes(definition, "minimumLeadTimeMinutes", out var lead))
            return false;

        policy = new SchedulingAppointmentPolicy(duration, before, after, lead);
        return true;
    }

    private static bool TryOptionalMinutes(JsonElement root, string name, out int value)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            value = 0;
            return true;
        }
        return TryMinutes(property, minimum: 0, out value);
    }

    private static bool TryRequiredMinutes(JsonElement root, string name, int minimum, out int value)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            value = 0;
            return false;
        }
        return TryMinutes(property, minimum, out value);
    }

    private static bool TryMinutes(JsonElement property, int minimum, out int value)
    {
        value = 0;
        return property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= minimum
            && value <= MaximumPolicyMinutes;
    }
}
