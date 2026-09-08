using System.Globalization;
using System.Text;

namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// Canonical <see cref="double"/> → string formatting that is byte-identical to
/// ECMAScript <c>Number::toString</c> (ECMA-262 §6.1.6.1.20) — i.e. the TS tier's
/// <c>String(n)</c> (SPINE-1 cross-tier guarantee; finding F1). .NET's own shortest
/// round-trip ("R") yields the same significant digits as V8/JS (the shortest
/// representation of a double is unique) but lays them out differently: uppercase
/// <c>E</c>, a zero-padded signed exponent, and a different decimal-vs-exponential
/// threshold (e.g. .NET <c>"1E+16"</c> / <c>"1E-07"</c> vs JS
/// <c>"10000000000000000"</c> / <c>"1e-7"</c>). This routine reformats those shortest
/// digits per the ECMAScript algorithm so both engines serialize a given number to
/// the identical bytes.
/// </summary>
internal static class CanonicalNumber
{
    private const long JsMaxSafeInteger = 9007199254740991L; // 2^53 - 1

    /// <summary>The ECMAScript <c>Number::toString</c> of an integral value.</summary>
    public static string ToJsonString(long l)
        => l >= -JsMaxSafeInteger && l <= JsMaxSafeInteger
            ? l.ToString(CultureInfo.InvariantCulture)
            : ToJsonString((double)l); // beyond the JS safe range, JSON.parse would round to a double

    /// <summary>The ECMAScript <c>Number::toString</c> of a finite double.</summary>
    public static string ToJsonString(double d)
    {
        if (d == 0.0) return "0";                 // also -0 → "0" (ECMAScript String(-0) === "0")
        bool neg = d < 0;
        double abs = neg ? -d : d;

        // .NET Core's shortest round-trippable formatting — the same shortest decimal
        // digits ECMAScript uses (the shortest representation of a double is unique).
        string r = abs.ToString("R", CultureInfo.InvariantCulture);

        // Decompose `r` into significant digits `s` (k of them) and the ECMA exponent `n`,
        // where value = s × 10^(n - k).
        int ePos = r.IndexOfAny(new[] { 'E', 'e' });
        int exp = 0;
        string mant = r;
        if (ePos >= 0)
        {
            exp = int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture);
            mant = r[..ePos];
        }
        int dot = mant.IndexOf('.');
        int pointPos;          // count of digits left of the decimal point within `mant`
        string allDigits;
        if (dot >= 0) { allDigits = mant.Remove(dot, 1); pointPos = dot; }
        else { allDigits = mant; pointPos = mant.Length; }
        int fracLen = allDigits.Length - pointPos;

        // Strip leading zeros (value-neutral), then trailing zeros (each adds one to the power).
        int lead = 0;
        while (lead < allDigits.Length - 1 && allDigits[lead] == '0') lead++;
        string sig = allDigits[lead..];
        int trail = 0;
        int sEnd = sig.Length;
        while (sEnd > 1 && sig[sEnd - 1] == '0') { sEnd--; trail++; }
        string s = sig[..sEnd];
        int k = s.Length;
        int n = k + trail + exp - fracLen;

        string body = Format(s, k, n);
        return neg ? "-" + body : body;
    }

    // ECMA-262 §6.1.6.1.20 — format the (positive) significant digits `s` (k of them)
    // and exponent `n` such that value = s × 10^(n - k).
    private static string Format(string s, int k, int n)
    {
        var sb = new StringBuilder();
        if (k <= n && n <= 21)
        {
            sb.Append(s);
            sb.Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(s, 0, n);
            sb.Append('.');
            sb.Append(s, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -n);
            sb.Append(s);
        }
        else
        {
            sb.Append(s[0]);
            if (k > 1) { sb.Append('.'); sb.Append(s, 1, k - 1); }
            sb.Append('e');
            int e = n - 1;
            sb.Append(e >= 0 ? '+' : '-');
            sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
