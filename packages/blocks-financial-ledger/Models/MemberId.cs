using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Opaque identifier for a member / partner of a multi-member LLC — the
/// per-member allocation dimensional tag on
/// <see cref="JournalEntryLine.MemberId"/> (ADR 0104 §4.3, financial F-4).
/// </summary>
/// <remarks>
/// The member's allocation-bearing identity rides this DIMENSION, not the GL
/// account number, so 1065 special allocations stay expressible and a
/// mis-posted member is detectable. Stubbed locally here (mirroring
/// <see cref="PropertyId"/> / <see cref="LegalEntityId"/>) so
/// blocks-financial-ledger does not take a reverse cluster dependency on a
/// party / identity cluster; relocate when a shared cross-cluster party FK
/// type lands in foundation-identity (same migration path as
/// <see cref="LegalEntityId"/>).
/// TODO: relocate to <c>foundation-identity</c> when that package lands.
/// </remarks>
[JsonConverter(typeof(MemberIdJsonConverter))]
public readonly record struct MemberId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator MemberId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(MemberId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static MemberId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class MemberIdJsonConverter : JsonConverter<MemberId>
{
    public override MemberId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("MemberId must be a non-null string.");
        return new MemberId(str);
    }

    public override void Write(Utf8JsonWriter writer, MemberId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
