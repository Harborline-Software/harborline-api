namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>
/// Supplies the stable random identity shared by every namespace owned by one install.
/// </summary>
public interface IInstallIdentityProvider
{
    /// <summary>Gets or creates the durable identity for this install.</summary>
    /// <param name="ct">Cancellation token for persistent storage access.</param>
    /// <returns>The install's stable identity.</returns>
    ValueTask<InstallIdentity> GetInstallIdentityAsync(CancellationToken ct);
}
