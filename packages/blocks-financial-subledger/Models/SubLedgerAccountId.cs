using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>Opaque identifier for a <see cref="SubLedgerAccount"/>.</summary>
[JsonConverter(typeof(SubLedgerAccountIdJsonConverter))]
public readonly record struct SubLedgerAccountId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator SubLedgerAccountId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(SubLedgerAccountId id) => id.Value;

    /// <summary>Generates a new unique id.</summary>
    public static SubLedgerAccountId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class SubLedgerAccountIdJsonConverter : JsonConverter<SubLedgerAccountId>
{
    public override SubLedgerAccountId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("SubLedgerAccountId must be a non-null string.");
        return new SubLedgerAccountId(str);
    }

    public override void Write(Utf8JsonWriter writer, SubLedgerAccountId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
