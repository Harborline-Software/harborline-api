using System.Globalization;

namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// Faithful ECMAScript <c>ToNumber(String)</c> (ECMA-262 §7.1.4.1.1) — the exact
/// semantics the TS tier gets from <c>Number(trimmed)</c> after a non-empty trim
/// (SPINE-1 cross-tier guarantee; finding F2). Replaces the prior
/// <c>double.TryParse(s, NumberStyles.Any, …)</c>, which diverged from JS by
/// <b>accepting</b> thousands separators (<c>"1,000"</c> → 1000), parenthesised
/// negatives (<c>"(5)"</c> → -5) and currency symbols, and by <b>rejecting</b>
/// JS-valid radix literals (<c>"0x1F"</c> → 31). String field values are user data,
/// so a benign arithmetic/compare rule over them must coerce identically on both tiers.
/// </summary>
internal static class JsNumber
{
    /// <summary>
    /// Parses <paramref name="input"/> as the TS tier's <c>toNumber</c>/<c>coerceNumber</c>
    /// would: trim ECMAScript whitespace; an empty/whitespace-only result is NOT a number;
    /// otherwise apply the StringNumericLiteral grammar; a NaN or non-finite result is NOT a
    /// number. Returns <see langword="false"/> for every input the TS tier rejects.
    /// </summary>
    public static bool TryParse(string? input, out double value)
    {
        value = 0;
        if (input is null) return false;
        int start = 0, end = input.Length;
        while (start < end && IsJsSpace(input[start])) start++;
        while (end > start && IsJsSpace(input[end - 1])) end--;
        if (start == end) return false;                 // empty / whitespace-only → not a number
        if (!ParseLiteral(input[start..end], out value)) return false;
        return double.IsFinite(value);                  // Infinity / NaN → not a number (TS isFinite guard)
    }

    private static bool ParseLiteral(string s, out double value)
    {
        value = 0;

        // Non-decimal radix literals carry NO sign in ECMAScript Number(string).
        if (s.Length > 2 && s[0] == '0')
        {
            int radix = s[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0) return TryRadix(s[2..], radix, out value);
        }

        int i = 0;
        double sign = 1;
        if (s[0] == '+') i = 1;
        else if (s[0] == '-') { sign = -1; i = 1; }
        string rest = s[i..];
        if (rest.Length == 0) return false;
        if (rest == "Infinity") { value = sign * double.PositiveInfinity; return true; }

        // Decimal literal only — NO thousands separators, parentheses, currency, or whitespace.
        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        if (double.TryParse(rest, styles, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d))
        {
            value = sign * d;
            return true;
        }
        return false;
    }

    private static bool TryRadix(string digits, int radix, out double value)
    {
        value = 0;
        if (digits.Length == 0) return false;
        double acc = 0;
        foreach (char c in digits)
        {
            int dv = DigitValue(c);
            if (dv < 0 || dv >= radix) return false;
            acc = acc * radix + dv;
        }
        value = acc;
        return true;
    }

    private static int DigitValue(char c)
        => c is >= '0' and <= '9' ? c - '0'
         : c is >= 'a' and <= 'f' ? c - 'a' + 10
         : c is >= 'A' and <= 'F' ? c - 'A' + 10
         : -1;

    private static bool IsJsSpace(char c) => char.IsWhiteSpace(c) || c == '\uFEFF';
}
