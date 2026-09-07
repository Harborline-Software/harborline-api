using System.Text;

namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>
/// Assigns the legacy filesystem footprint to one install identity and namespaces every other install.
/// </summary>
/// <remarks>
/// The first install to create the ownership record retains the historical paths. This preserves an
/// existing single-install deployment in place while ensuring every later co-resident install resolves
/// beneath <c>installs/{installIdentity}</c>. The ownership record is created atomically so concurrent
/// first launches cannot both adopt the legacy footprint.
/// </remarks>
public sealed class FileInstallFootprintProvider : IInstallFootprintProvider
{
    private const string OwnershipFileName = "install-footprint.identity";
    private readonly IInstallIdentityProvider _identityProvider;
    private readonly string _productDataDirectory;
    private readonly string? _legacyDataDirectory;
    private readonly string? _legacyDatabasePath;
    private readonly string? _legacyKeystoreDirectory;

    /// <summary>Creates a provider rooted at the current user's product-data directory.</summary>
    /// <param name="identityProvider">The single durable identity shared by all install namespaces.</param>
    /// <param name="productDataDirectory">
    /// Current user's product-data directory, such as <c>%LOCALAPPDATA%/Sunfish</c>.
    /// </param>
    /// <param name="legacyDataDirectory">Optional exact pre-namespacing node-data directory.</param>
    /// <param name="legacyDatabasePath">Optional exact pre-namespacing database path.</param>
    /// <param name="legacyKeystoreDirectory">Optional exact pre-namespacing keystore directory.</param>
    public FileInstallFootprintProvider(
        IInstallIdentityProvider identityProvider,
        string productDataDirectory,
        string? legacyDataDirectory = null,
        string? legacyDatabasePath = null,
        string? legacyKeystoreDirectory = null)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        ArgumentException.ThrowIfNullOrWhiteSpace(productDataDirectory);
        _productDataDirectory = Path.GetFullPath(productDataDirectory);
        _legacyDataDirectory = NormalizeOptionalPath(legacyDataDirectory);
        _legacyDatabasePath = NormalizeOptionalPath(legacyDatabasePath);
        _legacyKeystoreDirectory = NormalizeOptionalPath(legacyKeystoreDirectory);
    }

    /// <inheritdoc />
    public async ValueTask<InstallFootprint> GetInstallFootprintAsync(CancellationToken ct)
    {
        var identity = await _identityProvider.GetInstallIdentityAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(_productDataDirectory);

        var usesLegacyPaths = await OwnsLegacyFootprintAsync(identity, ct).ConfigureAwait(false);
        var root = usesLegacyPaths
            ? _productDataDirectory
            : Path.Combine(_productDataDirectory, "installs", identity.Value);
        var dataDirectoryName = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? "LocalNode"
            : "local-node";

        return usesLegacyPaths
            ? new InstallFootprint(
                identity,
                _legacyDataDirectory ?? Path.Combine(root, dataDirectoryName),
                _legacyDatabasePath ?? Path.Combine(root, "data", "sunfish.db"),
                _legacyKeystoreDirectory ?? Path.Combine(root, "keys"),
                UsesLegacyPaths: true)
            : new InstallFootprint(
                identity,
                Path.Combine(root, dataDirectoryName),
                Path.Combine(root, "data", "sunfish.db"),
                Path.Combine(root, "keys"),
                UsesLegacyPaths: false);
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private async Task<bool> OwnsLegacyFootprintAsync(InstallIdentity identity, CancellationToken ct)
    {
        var ownershipPath = Path.Combine(_productDataDirectory, OwnershipFileName);
        if (File.Exists(ownershipPath))
        {
            return await ReadOwnerAsync(ownershipPath, ct).ConfigureAwait(false) == identity;
        }

        try
        {
            await using var stream = new FileStream(
                ownershipPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(identity.Value), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException) when (File.Exists(ownershipPath))
        {
            return await ReadOwnerAsync(ownershipPath, ct).ConfigureAwait(false) == identity;
        }
    }

    private static async Task<InstallIdentity> ReadOwnerAsync(string ownershipPath, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var value = await File.ReadAllTextAsync(ownershipPath, Encoding.ASCII, ct)
                    .ConfigureAwait(false);
                try
                {
                    return InstallIdentity.Parse(value);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"Install-footprint ownership record '{ownershipPath}' is invalid; refusing to reassign existing data.",
                        ex);
                }
            }
            catch (IOException) when (File.Exists(ownershipPath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
            }
        }
    }
}
