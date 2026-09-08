using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Aging bucket classification shared across all financial sub-systems that
/// produce aging reports (AR, AP, sub-ledger projection).
///
/// <para>
/// Five non-overlapping buckets cover the open-balance population — items in
/// terminal status (Paid/Voided/WrittenOff for AR invoices; Paid/Voided for AP
/// bills) do not appear in any bucket.
/// </para>
///
/// <para>
/// <b>Consolidated here (ADR 0120 PR-E2):</b> previously each of
/// <c>blocks-financial-ar</c>, <c>blocks-financial-ap</c>, and
/// <c>blocks-financial-subledger</c> carried an identical copy of this enum,
/// causing <c>CS0104</c> ambiguity when all three are referenced in the same
/// compilation unit (witnessed in <c>blocks-migration-erpnext</c> tests and
/// <c>blocks-financial-subledger-projection</c>). Moving the canonical definition
/// here (the shared low tier that AR, AP, and the sub-ledger identity assembly
/// all already depend on) eliminates the triplication with no new project references.
/// </para>
/// </summary>
[JsonConverter(typeof(AgingBucketJsonConverter))]
public enum AgingBucket
{
    /// <summary>Open items not yet past due date.</summary>
    Current,

    /// <summary>1–30 days past due.</summary>
    Days0To30,

    /// <summary>31–60 days past due.</summary>
    Days31To60,

    /// <summary>61–90 days past due.</summary>
    Days61To90,

    /// <summary>91+ days past due.</summary>
    Days90Plus,
}

/// <summary>
/// JSON converter for <see cref="AgingBucket"/> — serialises to/from
/// the canonical lower-kebab-case strings used across all aging API surfaces.
/// </summary>
internal sealed class AgingBucketJsonConverter : JsonConverter<AgingBucket>
{
    public override AgingBucket Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "current" => AgingBucket.Current,
            "0-30"    => AgingBucket.Days0To30,
            "31-60"   => AgingBucket.Days31To60,
            "61-90"   => AgingBucket.Days61To90,
            "90+"     => AgingBucket.Days90Plus,
            var other => throw new JsonException($"Unknown AgingBucket '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, AgingBucket value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            AgingBucket.Current     => "current",
            AgingBucket.Days0To30   => "0-30",
            AgingBucket.Days31To60  => "31-60",
            AgingBucket.Days61To90  => "61-90",
            AgingBucket.Days90Plus  => "90+",
            _ => throw new JsonException($"Unknown AgingBucket '{value}'."),
        });
    }
}
