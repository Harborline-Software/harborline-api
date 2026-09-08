using System.Globalization;
using System.Numerics;

namespace Harborline.Api.Foundation.RuleEngine.Evaluation;

/// <summary>
/// Exact fixed-point decimal arithmetic for the <c>money.*</c> operators
/// (SPINE-1 design §1.4). Implemented over <see cref="BigInteger"/> mantissa +
/// base-10 scale — NOT IEEE-754 — so money math is decimal-correct and the
/// canonical string form is <b>byte-identical</b> to the TS tier's
/// <c>BigInt</c>-based port (the conformance corpus pins this).
/// </summary>
/// <remarks>
/// Operands are decimal <b>strings</b> (or integers); a non-integer JSON number
/// operand is rejected (<see cref="FormatException"/>) because JSON float parsing
/// cannot recover the exact decimal cross-tier. The canonical result trims
/// trailing fractional zeros and the trailing point, normalizing <c>-0</c> to
/// <c>0</c>.
/// </remarks>
internal readonly struct MoneyDecimal
{
    private readonly BigInteger _mantissa;
    private readonly int _scale; // value = _mantissa * 10^-_scale ; _scale >= 0

    /// <summary>Runtime bound on mantissa significant digits + scale (finding F4): a single
    /// large/super-linear money op (BigInteger multiply / <c>BigInteger.Pow</c> alignment) over a
    /// crafted or instance-supplied operand fails closed before it can run uncharged. Both tiers
    /// share the identical constant so the bound is cross-tier-deterministic.</summary>
    private const int MaxSignificantDigits = 4096;

    /// <summary>Count of significant digits in the mantissa (the BigInteger work-size proxy).</summary>
    internal int Size => _mantissa.IsZero ? 1 : BigInteger.Abs(_mantissa).ToString(CultureInfo.InvariantCulture).Length;

    private MoneyDecimal(BigInteger mantissa, int scale)
    {
        _mantissa = mantissa;
        _scale = scale;
    }

    /// <summary>Parses an exact decimal from its string form (e.g. "10", "-3.50", "0.005").</summary>
    public static MoneyDecimal Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new FormatException("empty money literal");
        var s = text.Trim();
        bool neg = false;
        int i = 0;
        if (s[0] == '+' || s[0] == '-') { neg = s[0] == '-'; i = 1; }

        var digits = new System.Text.StringBuilder();
        int scale = 0;
        bool seenDot = false;
        bool any = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.')
            {
                if (seenDot) throw new FormatException("multiple decimal points");
                seenDot = true;
                continue;
            }
            if (c < '0' || c > '9') throw new FormatException($"invalid money literal '{text}'");
            digits.Append(c);
            any = true;
            if (seenDot) scale++;
        }
        if (!any) throw new FormatException($"invalid money literal '{text}'");
        if (digits.Length > MaxSignificantDigits)
            throw new FormatException($"money literal exceeds {MaxSignificantDigits} significant digits");

        var mantissa = BigInteger.Parse(digits.Length == 0 ? "0" : digits.ToString(), CultureInfo.InvariantCulture);
        if (neg) mantissa = -mantissa;
        return new MoneyDecimal(mantissa, scale);
    }

    /// <summary>Parses an exact decimal from an integer.</summary>
    public static MoneyDecimal FromInteger(long value) => new(new BigInteger(value), 0);

    private static (BigInteger A, BigInteger B, int Scale) Align(MoneyDecimal a, MoneyDecimal b)
    {
        int scale = Math.Max(a._scale, b._scale);
        if (scale - a._scale > MaxSignificantDigits || scale - b._scale > MaxSignificantDigits)
            throw new FormatException("money alignment exceeds the size bound");
        var am = a._mantissa * BigInteger.Pow(10, scale - a._scale);
        var bm = b._mantissa * BigInteger.Pow(10, scale - b._scale);
        return (am, bm, scale);
    }

    public static MoneyDecimal operator +(MoneyDecimal a, MoneyDecimal b)
    {
        var (am, bm, scale) = Align(a, b);
        return new MoneyDecimal(am + bm, scale);
    }

    public static MoneyDecimal operator -(MoneyDecimal a, MoneyDecimal b)
    {
        var (am, bm, scale) = Align(a, b);
        return new MoneyDecimal(am - bm, scale);
    }

    public static MoneyDecimal operator *(MoneyDecimal a, MoneyDecimal b)
    {
        if (a.Size + b.Size > MaxSignificantDigits || a._scale + b._scale > MaxSignificantDigits)
            throw new FormatException("money multiplication exceeds the size bound");
        return new(a._mantissa * b._mantissa, a._scale + b._scale);
    }

    /// <summary>Exact decimal ordering (aligned mantissa compare) — for decimal <c>min</c>/<c>max</c>
    /// aggregates over a money column (finding F7).</summary>
    public int Compare(MoneyDecimal o)
    {
        var (am, bm, _) = Align(this, o);
        return am.CompareTo(bm);
    }

    /// <summary>The canonical, cross-tier-identical decimal string (trailing zeros trimmed).</summary>
    public string ToCanonicalString()
    {
        if (_mantissa.IsZero) return "0";
        bool neg = _mantissa.Sign < 0;
        string digits = BigInteger.Abs(_mantissa).ToString(CultureInfo.InvariantCulture);

        if (_scale == 0)
        {
            return neg ? "-" + digits : digits;
        }

        if (digits.Length <= _scale)
        {
            digits = new string('0', _scale - digits.Length + 1) + digits;
        }
        int dot = digits.Length - _scale;
        string intPart = digits[..dot];
        string fracPart = digits[dot..].TrimEnd('0');
        string body = fracPart.Length == 0 ? intPart : intPart + "." + fracPart;
        return neg && body != "0" ? "-" + body : body;
    }
}
