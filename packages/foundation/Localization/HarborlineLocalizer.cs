using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Localization;
using SmartFormat;
using SmartFormat.Core.Formatting;

namespace Harborline.Api.Foundation.Localization;

/// <summary>
/// SmartFormat.NET-backed implementation of <see cref="IHarborlineLocalizer{T}"/>.
/// Resolves patterns via the injected <see cref="IStringLocalizer{T}"/>, then renders
/// them through SmartFormat under <see cref="CultureInfo.CurrentCulture"/> so CLDR
/// plural rules apply per-locale (Arabic six-form, Japanese single-form, etc.).
/// </summary>
public sealed class HarborlineLocalizer<T> : IHarborlineLocalizer<T>
{
    private readonly IStringLocalizer<T> _inner;
    private readonly SmartFormatter _formatter;

    public HarborlineLocalizer(IStringLocalizer<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _formatter = Smart.CreateDefaultSmartFormat();
    }

    public string Get(string key) => _inner[key].Value;

    public string Format(string key, object args)
    {
        var pattern = _inner[key].Value;
        return _formatter.Format(CultureInfo.CurrentCulture, pattern, args);
    }

    public string Plural(string key, long count, object? additionalArgs = null)
    {
        var pattern = _inner[key].Value;
        var bag = BuildArgs(count, additionalArgs);
        return _formatter.Format(CultureInfo.CurrentCulture, pattern, bag);
    }

    private static Dictionary<string, object> BuildArgs(long count, object? additionalArgs)
    {
        var bag = new Dictionary<string, object>(StringComparer.Ordinal) { ["count"] = count };
        if (additionalArgs is null) return bag;

        foreach (var prop in additionalArgs.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = prop.GetValue(additionalArgs);
            if (value is not null) bag[prop.Name] = value;
        }
        return bag;
    }
}
