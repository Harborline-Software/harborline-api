namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Locale-keyed text used for form titles, section titles, field labels,
/// help text, and rule error messages (ADR 0055 §"Form rendering").
/// </summary>
/// <remarks>
/// <para>
/// Keys are RFC 5646 language tags ("en", "en-US", "es-MX", "fr-CA"). The
/// form engine selects a value by the actor's locale chain, falling back
/// through region → language → <see cref="DefaultLocale"/> → first available.
/// </para>
/// <para>
/// Defined locally on the keystone substrate; promotable to a shared
/// location (Foundation.I18n) when a second substrate needs the same type.
/// </para>
/// </remarks>
/// <param name="DefaultLocale">Fallback locale tag (typically "en").</param>
/// <param name="Values">Locale-tag → text map. MUST contain
/// <paramref name="DefaultLocale"/>.</param>
public sealed record InternationalizedText(
    string DefaultLocale,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// Resolves the best-match text for the given locale chain. Falls back
    /// through the chain, then to <see cref="DefaultLocale"/>, then to the
    /// first available value. Returns the empty string only if no values
    /// are present (which the constructor's invariant disallows; defensive
    /// only for round-tripping from malformed JSON).
    /// </summary>
    public string Resolve(IReadOnlyList<string> localeChain)
    {
        ArgumentNullException.ThrowIfNull(localeChain);
        foreach (var locale in localeChain)
        {
            if (Values.TryGetValue(locale, out var hit) && !string.IsNullOrEmpty(hit))
            {
                return hit;
            }
        }

        if (Values.TryGetValue(DefaultLocale, out var fallback) && !string.IsNullOrEmpty(fallback))
        {
            return fallback;
        }

        foreach (var v in Values.Values)
        {
            if (!string.IsNullOrEmpty(v)) return v;
        }

        return string.Empty;
    }

    /// <summary>
    /// Convenience constructor for the common single-locale case (en-only
    /// strings used during early development before localization lands).
    /// </summary>
    public static InternationalizedText FromInvariant(string text)
        => new("en", new Dictionary<string, string> { ["en"] = text });
}
