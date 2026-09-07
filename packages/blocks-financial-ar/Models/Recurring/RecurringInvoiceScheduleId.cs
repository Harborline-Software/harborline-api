using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.FinancialAr.Models;

/// <summary>Opaque identifier for a <see cref="RecurringInvoiceSchedule"/>.</summary>
[JsonConverter(typeof(RecurringInvoiceScheduleIdJsonConverter))]
public readonly record struct RecurringInvoiceScheduleId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator RecurringInvoiceScheduleId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(RecurringInvoiceScheduleId id) => id.Value;

    /// <summary>Generates a new unique id.</summary>
    public static RecurringInvoiceScheduleId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class RecurringInvoiceScheduleIdJsonConverter : JsonConverter<RecurringInvoiceScheduleId>
{
    public override RecurringInvoiceScheduleId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("RecurringInvoiceScheduleId must be a non-null string.");
        return new RecurringInvoiceScheduleId(str);
    }

    public override void Write(Utf8JsonWriter writer, RecurringInvoiceScheduleId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
