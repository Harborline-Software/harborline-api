using System.Text;

namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The [A14] single chokepoint for <c>frameCode</c> lexical form: lowercase kebab-case (the CRDT
/// conventions §5 stable-code rule), normalized on write BEFORE signing, so <c>Hull-Datum</c> and
/// <c>hull-datum</c> are one frame independent of provider collation (ADR 0101 Rev 3.2 [A14]).
/// </summary>
public static class SpatialFrameCodes
{
    /// <summary>Maximum normalized length (matches the EF <c>HasMaxLength</c> on the key column).</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Normalizes <paramref name="frameCode"/> to lowercase kebab-case: trims, lowercases
    /// (invariant), maps whitespace and underscore runs to a single hyphen, collapses hyphen runs,
    /// and strips leading/trailing hyphens. Any character outside <c>[a-z0-9-]</c> after that is a
    /// fail-closed <see cref="ArgumentException"/> — normalization maps lexical variants of one
    /// name onto one key; it never invents a name from arbitrary input.
    /// </summary>
    public static string Normalize(string frameCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameCode);

        var builder = new StringBuilder(frameCode.Length);
        var pendingHyphen = false;
        foreach (var raw in frameCode.Trim())
        {
            var c = char.ToLowerInvariant(raw);
            if (char.IsWhiteSpace(c) || c is '_' or '-')
            {
                pendingHyphen = builder.Length > 0;
                continue;
            }

            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                throw new ArgumentException(
                    $"frameCode '{frameCode}' contains a character outside the lowercase-kebab-case "
                    + "stable-code alphabet [a-z0-9-] (ADR 0101 Rev 3.2 [A14] / CRDT conventions §5).",
                    nameof(frameCode));
            }

            if (pendingHyphen)
            {
                builder.Append('-');
                pendingHyphen = false;
            }
            builder.Append(c);
        }

        if (builder.Length == 0)
        {
            throw new ArgumentException(
                $"frameCode '{frameCode}' normalizes to the empty string.", nameof(frameCode));
        }

        if (builder.Length > MaxLength)
        {
            throw new ArgumentException(
                $"frameCode '{frameCode}' exceeds the {MaxLength}-character key bound.", nameof(frameCode));
        }

        return builder.ToString();
    }
}
