using System.Globalization;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Documents.Model;

namespace Harborline.Api.Foundation.Documents.Rendering;

/// <summary>
/// Formats a canonical merge value in the <i>document's</i> locale (#111 design §1.4/§1.5): currency, date,
/// number, text. Format is a property of the rendering context — the cascade — never the template; the same
/// invoice template renders <c>1.234,56 €</c> for de-DE and <c>$1,234.56</c> for en-US because the value is
/// canonical (ISO-8601 date, decimal money, raw number) and the format is resolved here.
/// </summary>
/// <remarks>
/// This is the <b>base (culture) layer</b> of the format cascade, keyed by the document locale + currency
/// code. It is sufficient for the en-US keystone (§6 D1/D2). The pack/tenant cascade overrides
/// (a jurisdiction's preferred date pattern, an org's number grouping) layer on top later — the callers
/// bind a canonical value; this resolves the locale default. Deterministic: the same value + locale always
/// formats identically (the content-equivalence anchor, F3).
/// </remarks>
public sealed class DocumentFormatContext
{
    private readonly CultureInfo _culture;

    /// <summary>The document locale tag (BCP-47) values are formatted in.</summary>
    public string LocaleTag { get; }

    /// <summary>The ISO-4217 currency code money values are formatted with (null if the document has none).</summary>
    public string? CurrencyCode { get; }

    /// <summary>Constructs a format context for a document locale + optional currency.</summary>
    public DocumentFormatContext(string localeTag, string? currencyCode)
    {
        LocaleTag = string.IsNullOrWhiteSpace(localeTag) ? "en-US" : localeTag;
        CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? null : currencyCode;
        _culture = ResolveCulture(LocaleTag);
    }

    /// <summary>Formats a resolved canonical value per <paramref name="format"/> in the document locale.</summary>
    public string Format(JsonNode? value, MergeFormat format)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return format switch
        {
            MergeFormat.Currency => FormatCurrency(value),
            MergeFormat.Date => FormatDate(value),
            MergeFormat.Integer => FormatNumber(value, decimals: 0),
            MergeFormat.Decimal => FormatNumber(value, decimals: null),
            _ => AsText(value),
        };
    }

    /// <summary>Formats a decimal money value (canonical: a decimal-string or a JSON number) with the currency.</summary>
    public string FormatCurrency(JsonNode? value)
    {
        if (!TryDecimal(value, out var amount))
        {
            return AsText(value);
        }

        // Culture number/grouping/symbol placement + the document's ISO currency symbol. The canonical
        // value carries no symbol; the currency comes from the rendering context (§1.4).
        var nfi = (NumberFormatInfo)_culture.NumberFormat.Clone();
        nfi.CurrencySymbol = CurrencySymbol(CurrencyCode) ?? nfi.CurrencySymbol;
        return amount.ToString("C", nfi);
    }

    /// <summary>Formats an ISO-8601 date/date-time value as the locale's short date.</summary>
    public string FormatDate(JsonNode? value)
    {
        var raw = AsText(value);
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d.ToString("d", _culture);
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.ToString("d", _culture);
        }

        return raw;
    }

    private string FormatNumber(JsonNode? value, int? decimals)
    {
        if (!TryDecimal(value, out var n))
        {
            return AsText(value);
        }

        return decimals is { } d
            ? n.ToString("N" + d.ToString(CultureInfo.InvariantCulture), _culture)
            : n.ToString("N", _culture);
    }

    private static bool TryDecimal(JsonNode? value, out decimal result)
    {
        result = 0m;
        if (value is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue<decimal>(out result))
        {
            return true;
        }

        if (v.TryGetValue<double>(out var dbl))
        {
            result = (decimal)dbl;
            return true;
        }

        if (v.TryGetValue<long>(out var l))
        {
            result = l;
            return true;
        }

        if (v.TryGetValue<int>(out var i))
        {
            result = i;
            return true;
        }

        // Canonical money is a decimal-string; parse it invariantly (never the culture) to preserve the value.
        return v.TryGetValue<string>(out var s)
            && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static string AsText(JsonNode? value)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (v.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }
        }

        return value?.ToString() ?? string.Empty;
    }

    private static CultureInfo ResolveCulture(string tag)
    {
        try
        {
            // predefinedOnly:true rejects the synthetic cultures ICU fabricates for unknown-but-well-formed
            // tags (e.g. "zz-ZZ", LCID 4096 / LOCALE_CUSTOM_UNSPECIFIED). Without it, host ICU disagrees on a
            // fabricated culture's currency pattern: this build's ICU renders "$10.00", the CI Linux runner's
            // renders "$ 10.00" (an ambient NBSP/space between symbol and amount). Financial rendering must be
            // host-ICU-deterministic (F3, the content-equivalence anchor), so an unresolvable locale falls
            // back to the true en-US keystone on every host — never a fabricated culture, and never
            // InvariantCulture (whose currency pattern likewise inserts a space).
            return CultureInfo.GetCultureInfo(tag, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US");
        }
    }

    /// <summary>
    /// Maps an ISO-4217 code to its display symbol without depending on a <c>RegionInfo</c> per code
    /// (the platform ships the neutral base layer; a pack cascade supplies jurisdiction refinements). The
    /// keystone set covers the dogfood currency; an unknown code falls back to the code itself (honest).
    /// </summary>
    private static string? CurrencySymbol(string? code) => code?.ToUpperInvariant() switch
    {
        null => null,
        "USD" => "$",
        "EUR" => "€",
        "GBP" => "£",
        "JPY" => "¥",
        "CAD" => "CA$",
        "AUD" => "A$",
        var other => other,
    };
}
