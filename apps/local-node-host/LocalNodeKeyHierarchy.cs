namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Resolves the two custody roots that remain during the SC-4 compatibility period.
/// </summary>
/// <remarks>
/// Identity, agreement, and tenant-content keys remain rooted in the install seed because changing
/// them requires a roster-authorized public-key transition. When SC-4 supplies a recoverable Store
/// DEK, that DEK is the at-rest custody root for both the relational store and every per-team store.
/// Without SC-4, the install seed remains the at-rest derivation root.
/// </remarks>
public sealed class LocalNodeKeyHierarchy
{
    private const int RootKeyLength = 32;

    private readonly byte[] _identityRootKey;
    private readonly byte[] _atRestRootKey;

    private LocalNodeKeyHierarchy(
        ReadOnlySpan<byte> identityRootKey,
        ReadOnlySpan<byte> atRestRootKey,
        bool perTeamKvStoreIsEnvelopeExtended)
    {
        _identityRootKey = identityRootKey.ToArray();
        _atRestRootKey = atRestRootKey.ToArray();
        PerTeamKvStoreIsEnvelopeExtended = perTeamKvStoreIsEnvelopeExtended;
    }

    /// <summary>Install seed from which identity, agreement, and tenant-content keys derive.</summary>
    public ReadOnlyMemory<byte> IdentityRootKey => _identityRootKey;

    /// <summary>Recoverable custody root from which encrypted-store keys derive.</summary>
    public ReadOnlyMemory<byte> AtRestRootKey => _atRestRootKey;

    /// <summary>
    /// Whether every per-team encrypted store uses the same recoverable custody root as the
    /// relational store.
    /// </summary>
    public bool PerTeamKvStoreIsEnvelopeExtended { get; }

    /// <summary>Resolve the hierarchy for the configured deployment profile.</summary>
    /// <param name="options">Resolved local-node options.</param>
    /// <param name="rootSeed">Install or Bridge-tenant root seed.</param>
    /// <returns>The validated hierarchy roots.</returns>
    public static LocalNodeKeyHierarchy Resolve(
        LocalNodeOptions options,
        ReadOnlySpan<byte> rootSeed)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (rootSeed.Length != RootKeyLength)
        {
            throw new ArgumentException(
                $"Root seed must be {RootKeyLength} bytes (was {rootSeed.Length}).",
                nameof(rootSeed));
        }

        if (string.IsNullOrWhiteSpace(options.StoreDekHex))
        {
            return new LocalNodeKeyHierarchy(rootSeed, rootSeed, false);
        }

        byte[] storeDek;
        try
        {
            storeDek = Convert.FromHexString(options.StoreDekHex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "LocalNode:StoreDekHex is set but is not valid hex. Expected a 64-character hex string (32 bytes).",
                ex);
        }

        if (storeDek.Length != RootKeyLength)
        {
            throw new InvalidOperationException(
                $"LocalNode:StoreDekHex decoded to {storeDek.Length} bytes; expected exactly {RootKeyLength}.");
        }

        return new LocalNodeKeyHierarchy(rootSeed, storeDek, true);
    }
}
