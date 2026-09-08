using System.Globalization;
using System.Text;

using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// Publisher-card display-text contract shared by export and feed consumers.
/// </summary>
/// <remarks>
/// <para>
/// Publisher strings are untrusted display text even after their signature verifies. Export strips
/// C0/C1 controls and Unicode bidi controls, then visibly ellipsizes over-limit values
/// on Unicode text-element boundaries before signing. Verification deliberately does not re-apply
/// these rules, so older signed manifests remain verifiable byte-for-byte.
/// </para>
/// <para>
/// Consumers MUST insert these values through their platform's text-escaping API, MUST NOT interpret
/// them as HTML, Markdown, URLs, templates, or commands. Consumers MUST project signed values through
/// <see cref="PrepareNameOrTitleForRender"/> or <see cref="PrepareFreeTextOrDescriptionForRender"/>;
/// those boundaries remove hostile formatting controls without mutating the verified model and wrap
/// the result in <see cref="FirstStrongIsolate"/> / <see cref="PopDirectionalIsolate"/>.
/// </para>
/// </remarks>
public static class PackCardDisplayText
{
    /// <summary>Maximum Unicode text elements in a name, title, or category label.</summary>
    public const int NameOrTitleCharacterLimit = 120;

    /// <summary>Maximum Unicode text elements in free text such as a tagline or description.</summary>
    public const int FreeTextOrDescriptionCharacterLimit = 500;

    /// <summary>Unicode FIRST STRONG ISOLATE (U+2068), placed before escaped display text.</summary>
    public const string FirstStrongIsolate = "\u2068";

    /// <summary>Unicode POP DIRECTIONAL ISOLATE (U+2069), placed after escaped display text.</summary>
    public const string PopDirectionalIsolate = "\u2069";

    /// <summary>The visible truncation marker, U+2026 HORIZONTAL ELLIPSIS.</summary>
    public const string Ellipsis = "\u2026";

    internal static string? NormalizeNameOrTitleAtExport(string? value)
        => NormalizeAtExport(value, NameOrTitleCharacterLimit);

    internal static string? NormalizeFreeTextOrDescriptionAtExport(string? value)
        => NormalizeAtExport(value, FreeTextOrDescriptionCharacterLimit);

    /// <summary>
    /// Produces a bounded, escaped-text-ready name/title projection inside a fresh bidi isolate.
    /// The input string is not mutated and no Unicode normalization is performed.
    /// </summary>
    /// <remarks>
    /// This is the mandatory boundary for rendering both current and legacy signed name/title text.
    /// The caller must still use its platform's ordinary text-escaping API when inserting the result.
    /// </remarks>
    public static string? PrepareNameOrTitleForRender(string? value)
        => PrepareForRender(value, NameOrTitleCharacterLimit);

    /// <summary>
    /// Produces a bounded, escaped-text-ready free-text projection inside a fresh bidi isolate.
    /// The input string is not mutated and no Unicode normalization is performed.
    /// </summary>
    /// <remarks>
    /// This is the mandatory boundary for rendering both current and legacy signed free text. The
    /// caller must still use its platform's ordinary text-escaping API when inserting the result.
    /// </remarks>
    public static string? PrepareFreeTextOrDescriptionForRender(string? value)
        => PrepareForRender(value, FreeTextOrDescriptionCharacterLimit);

    private static string? NormalizeAtExport(string? value, int limit)
    {
        if (value is null)
        {
            return null;
        }

        // Preserve the signing boundary's existing fail-closed behavior. Enumerating or serializing
        // malformed UTF-16 can substitute U+FFFD; reject it before any normalization instead.
        CanonicalJson.EnsureWellFormedUtf16(value);

        var sanitized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!IsUnsafeFormattingControl(character))
            {
                sanitized.Append(character);
            }
        }

        var sanitizedValue = sanitized.ToString();
        var textElementStarts = StringInfo.ParseCombiningCharacters(sanitizedValue);
        if (textElementStarts.Length <= limit)
        {
            return sanitizedValue;
        }

        // Reserve the final visible element for the ellipsis. ParseCombiningCharacters returns the
        // original UTF-16 index of each text element, so slicing here preserves the original code-point
        // sequence without normalization and cannot split combining, astral, or ZWJ clusters.
        var prefixLength = textElementStarts[limit - 1];
        return sanitizedValue[..prefixLength] + Ellipsis;
    }

    private static string? PrepareForRender(string? value, int limit)
    {
        var sanitized = NormalizeAtExport(value, limit);
        return sanitized is null ? null : FirstStrongIsolate + sanitized + PopDirectionalIsolate;
    }

    private static bool IsUnsafeFormattingControl(char character)
        => character <= '\u001F' ||
            character is >= '\u007F' and <= '\u009F' ||
            character is '\u061C' or '\u200E' or '\u200F' || // ALM, LRM, RLM
            character is >= '\u202A' and <= '\u202E' || // LRE, RLE, PDF, LRO, RLO
            character is >= '\u2066' and <= '\u2069';   // LRI, RLI, FSI, PDI
}
