namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The canonical installation-account username identity transform.</summary>
internal static class WebUsernameNormalizer
{
    internal const int MaxUsernameLength = 320;

    /// <summary>Returns the account lookup identity, or null for malformed input.</summary>
    internal static string? TryNormalize(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > MaxUsernameLength)
        {
            return null;
        }

        try
        {
            var normalized = NormalizeCore(username);
            return normalized.Length is > 0 and <= MaxUsernameLength ? normalized : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Returns the account lookup identity and preserves the minter's validation failures.</summary>
    internal static string NormalizeRequired(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (username.Length > MaxUsernameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(username),
                $"Username exceeds {MaxUsernameLength} characters.");
        }

        var normalized = NormalizeCore(username);
        if (normalized.Length > MaxUsernameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(username),
                $"Username exceeds {MaxUsernameLength} normalized characters.");
        }

        return normalized;
    }

    private static string NormalizeCore(string username) =>
        username.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant();
}
