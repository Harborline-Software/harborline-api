using System.Globalization;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Diagnostics;

/// <summary>
/// Bounds caller-controlled text before it becomes a log template argument (CodeQL <c>cs/log-forging</c>).
/// The host's console and Windows Event Log sinks do not escape line breaks, so a structured argument alone
/// does not stop a forged line. Control characters are made visible as <c>\uXXXX</c>, not dropped, so the
/// log still shows what was sent.
/// </summary>
/// <remarks>
/// This bounds text a caller chose; it is not a redaction helper. A value that must not be logged at all (a
/// password, a token, an attempted username) is left out of the line, not passed through here.
/// </remarks>
internal static partial class LogText
{
    /// <summary>The longest caller text kept, in UTF-16 code units; a longer value is cut and ends in an ellipsis.</summary>
    internal const int MaxLength = 256;

    /// <summary>
    /// <paramref name="value"/> cut to <see cref="MaxLength"/>, with every C0/C1 control character (CR, LF,
    /// NEL, ESC, TAB ...) and the Unicode line and paragraph separators replaced by a visible <c>\uXXXX</c>.
    /// Null stays null so call sites keep their own <c>(none)</c> / <c>&lt;none&gt;</c> placeholder.
    /// </summary>
    internal static string? Of(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var bounded = value;
        if (value.Length > MaxLength)
        {
            // Never split a surrogate pair at the cut.
            var cut = char.IsHighSurrogate(value[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            bounded = string.Concat(value.AsSpan(0, cut), "…");
        }

        // Always end in Regex.Replace: CodeQL's log-forging model treats a Regex/String Replace as the
        // sanitizer, and a branch that returned `bounded` unreplaced would keep the alert open.
        return Unsafe().Replace(bounded, static match => Escape(match.Value[0]));
    }

    private static string Escape(char c) =>
        string.Concat("\\u", ((int)c).ToString("x4", CultureInfo.InvariantCulture));

    // \p{Cc} is U+0000-U+001F and U+007F-U+009F (C0, DEL and C1); U+2028/U+2029 are the line and paragraph
    // separators some viewers break lines on.
    [GeneratedRegex(@"[\p{Cc}\u2028\u2029]", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();
}
