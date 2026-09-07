using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>Opaque identifier for a <see cref="PaymentApplication"/>.</summary>
[JsonConverter(typeof(PaymentApplicationIdJsonConverter))]
public readonly record struct PaymentApplicationId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator PaymentApplicationId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(PaymentApplicationId id) => id.Value;

    /// <summary>Generates a new unique id.</summary>
    public static PaymentApplicationId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class PaymentApplicationIdJsonConverter : JsonConverter<PaymentApplicationId>
{
    public override PaymentApplicationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("PaymentApplicationId must be a non-null string.");
        return new PaymentApplicationId(str);
    }

    public override void Write(Utf8JsonWriter writer, PaymentApplicationId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
