namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>Resolves the filesystem footprint owned by one durable install identity.</summary>
public interface IInstallFootprintProvider
{
    /// <summary>Gets the paths exclusively owned by this install.</summary>
    /// <param name="ct">Cancellation token for identity and ownership-record access.</param>
    /// <returns>The install's data, database, and keystore paths.</returns>
    ValueTask<InstallFootprint> GetInstallFootprintAsync(CancellationToken ct);
}
