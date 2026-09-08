namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>
/// Stable, randomly generated identity for one product installation.
/// </summary>
/// <param name="Value">Canonical lower-case, 32-character GUID value.</param>
public readonly record struct InstallIdentity(string Value)
{
    /// <summary>Creates a new cryptographically random install identity.</summary>
    public static InstallIdentity New() => new(Guid.NewGuid().ToString("N"));

    /// <summary>Parses a persisted install identity.</summary>
    /// <param name="value">Persisted 32-character GUID value.</param>
    /// <returns>The canonical install identity.</returns>
    /// <exception cref="FormatException"><paramref name="value"/> is not a GUID in N format.</exception>
    public static InstallIdentity Parse(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var guid))
        {
            throw new FormatException("Install identity must be a 32-character GUID in N format.");
        }

        return new InstallIdentity(guid.ToString("N"));
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
