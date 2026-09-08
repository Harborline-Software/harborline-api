using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// Opaque identifier for a <see cref="FormSubmissionRecord"/> — the generic "this submission was filled
/// into this record" link (#144 runtime form-fill).
/// </summary>
/// <remarks>Mirrors the deterministic-id convention of <see cref="ConditionAssessmentId"/>.</remarks>
[JsonConverter(typeof(FormSubmissionRecordIdJsonConverter))]
public readonly record struct FormSubmissionRecordId(string Value)
{
    /// <summary>Unit-separator delimiter for the deterministic-id canonical form (never appears in an id).</summary>
    private const char Separator = (char)0x1F;

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator FormSubmissionRecordId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(FormSubmissionRecordId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static FormSubmissionRecordId NewId() => new(Guid.NewGuid().ToString());

    /// <summary>
    /// Derives a <b>deterministic</b> id from the submission's identity — the form instance and the target
    /// entity. The same submission projected twice (an at-least-once outbox replay after a projector crash —
    /// ADR 0101 Rev 3.1 Wave 2b / F-ATOM) yields the SAME id, so the store UPSERTs rather than duplicating:
    /// at-least-once delivery becomes effectively-once. The inputs (opaque ids) carry no PII.
    /// </summary>
    public static FormSubmissionRecordId Derive(string instance, string entity)
    {
        var canonical = string.Join(Separator, instance, entity);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return new FormSubmissionRecordId("fsr-" + Convert.ToHexStringLower(hash)[..32]);
    }
}

internal sealed class FormSubmissionRecordIdJsonConverter : JsonConverter<FormSubmissionRecordId>
{
    public override FormSubmissionRecordId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("FormSubmissionRecordId must be a non-null string.");
        return new FormSubmissionRecordId(str);
    }

    public override void Write(Utf8JsonWriter writer, FormSubmissionRecordId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
