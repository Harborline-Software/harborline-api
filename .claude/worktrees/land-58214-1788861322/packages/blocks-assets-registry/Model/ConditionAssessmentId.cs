using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// Opaque identifier for a <see cref="ConditionAssessment"/> record (annex §3.8 / D-M).
/// </summary>
/// <remarks>Mirrors the opaque-string-backed id convention of <c>EquipmentId</c>.</remarks>
[JsonConverter(typeof(ConditionAssessmentIdJsonConverter))]
public readonly record struct ConditionAssessmentId(string Value)
{
    /// <summary>Unit-separator delimiter for the deterministic-id canonical form (never appears in an id).</summary>
    private const char Separator = (char)0x1F;

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator ConditionAssessmentId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(ConditionAssessmentId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static ConditionAssessmentId NewId() => new(Guid.NewGuid().ToString());

    /// <summary>
    /// Derives a <b>deterministic</b> id from the projecting submission's identity — the form instance,
    /// the rating field pointer, and the target entity. The same submission projected twice (e.g. an
    /// at-least-once outbox replay after a projector crash — ADR 0101 Rev 3.1 Wave 2b / F-ATOM) yields
    /// the SAME id, so the condition store UPSERTs rather than duplicating: at-least-once delivery
    /// becomes effectively-once. The inputs (opaque ids + a schema field pointer) carry no PII.
    /// </summary>
    public static ConditionAssessmentId Derive(string instance, string fieldPointer, string entity)
    {
        var canonical = string.Join(Separator, instance, fieldPointer, entity);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return new ConditionAssessmentId("ca-" + Convert.ToHexStringLower(hash)[..32]);
    }
}

internal sealed class ConditionAssessmentIdJsonConverter : JsonConverter<ConditionAssessmentId>
{
    public override ConditionAssessmentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("ConditionAssessmentId must be a non-null string.");
        return new ConditionAssessmentId(str);
    }

    public override void Write(Utf8JsonWriter writer, ConditionAssessmentId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
