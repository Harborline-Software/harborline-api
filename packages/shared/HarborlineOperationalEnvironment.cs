namespace Harborline.OperationalEnvironment;

internal static class HarborlineOperationalEnvironment
{
    private static readonly string[] LegacyPrefixes = ["SUNFISH" + "_", "CARRIER" + "_"];
    private const string ReplacementPrefix = "HARBORLINE_";

    public static string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var legacyName = Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(candidate => LegacyPrefixes.Any(prefix =>
                candidate.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (legacyName is not null)
        {
            var separator = legacyName.IndexOf('_');
            var replacement = ReplacementPrefix + legacyName[(separator + 1)..];
            throw new InvalidOperationException(
                $"Legacy Harborline environment variable {legacyName} is not supported; use {replacement}.");
        }

        return Environment.GetEnvironmentVariable(name);
    }
}
