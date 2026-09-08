using System.Globalization;

namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// Encodes uniformly derived bytes as zero-padded decimal digits plus a Damm check digit.
/// Display grouping is deliberately outside this pure crypto primitive.
/// </summary>
public static class SasEncode
{
    /// <summary>Ratified default number of security-bearing digits.</summary>
    public const int DefaultDigits = 8;

    /// <summary>Largest supported payload length; the ratified policy currently uses 6, 8, or 10.</summary>
    public const int MaximumDigits = 18;

    private static readonly byte[,] DammQuasigroup =
    {
        { 0, 3, 1, 7, 5, 9, 8, 6, 4, 2 },
        { 7, 0, 9, 2, 1, 5, 4, 8, 6, 3 },
        { 4, 2, 0, 6, 8, 7, 1, 3, 5, 9 },
        { 1, 7, 5, 0, 9, 8, 3, 4, 2, 6 },
        { 6, 1, 2, 3, 0, 4, 5, 9, 7, 8 },
        { 3, 6, 7, 4, 2, 0, 9, 5, 8, 1 },
        { 5, 8, 6, 9, 7, 2, 0, 1, 3, 4 },
        { 8, 9, 4, 5, 3, 6, 2, 0, 1, 7 },
        { 9, 4, 3, 8, 6, 1, 7, 2, 0, 5 },
        { 2, 5, 8, 1, 4, 3, 6, 7, 9, 0 },
    };

    /// <summary>
    /// Treats <paramref name="uniformBytes"/> as an unsigned big-endian integer, reduces it modulo
    /// 10^<paramref name="digits"/>, zero-pads to exactly that length, and appends a Damm digit.
    /// Callers must supply uniformly derived bytes with their own modulo-bias margin.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> uniformBytes, int digits = DefaultDigits)
    {
        if (uniformBytes.IsEmpty)
        {
            throw new ArgumentException("Uniform input must be non-empty.", nameof(uniformBytes));
        }

        ValidateDigitCount(digits);

        UInt128 modulus = 1;
        for (var index = 0; index < digits; index++)
        {
            modulus *= 10;
        }

        UInt128 remainder = 0;
        foreach (var value in uniformBytes)
        {
            remainder = ((remainder * 256) + value) % modulus;
        }

        var payload = remainder.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        var checkDigit = ComputeDamm(payload);
        return string.Concat(payload, (char)('0' + checkDigit));
    }

    /// <summary>
    /// Returns true only for an all-decimal value whose final digit makes the Damm checksum zero.
    /// This is typo detection, not a security comparison.
    /// </summary>
    public static bool HasValidCheckDigit(ReadOnlySpan<char> code)
    {
        if (code.Length < 2)
        {
            return false;
        }

        byte interim = 0;
        foreach (var character in code)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            interim = DammQuasigroup[interim, character - '0'];
        }

        return interim == 0;
    }

    internal static void ValidateDigitCount(int digits)
    {
        if (digits is < 1 or > MaximumDigits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(digits),
                digits,
                $"Digit count must be between 1 and {MaximumDigits}.");
        }
    }

    private static byte ComputeDamm(ReadOnlySpan<char> payload)
    {
        byte interim = 0;
        foreach (var character in payload)
        {
            interim = DammQuasigroup[interim, character - '0'];
        }

        return interim;
    }
}
