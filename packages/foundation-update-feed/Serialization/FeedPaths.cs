using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.UpdateFeed.Serialization;

/// <summary>
/// The canonical relative-path layout of a feed tree (design note §2). All paths are
/// forward-slash, feed-base-relative — the SAME on a CDN origin, an intranet mirror, or a USB
/// sideload (a mirror is a dumb copy, §2.3), so a consumer never needs to know which host served the
/// bytes.
/// </summary>
/// <remarks>
/// URLs inside a signed document are relative to THAT document's directory (matching the design note's
/// examples: the channel index's <c>indexUrl</c> is feed-base-relative <c>packs/&lt;key&gt;/index.json</c>;
/// the per-pack index's <c>manifestUrl</c> is index-dir-relative <c>&lt;version&gt;/manifest.json</c>).
/// The generator builds those doc-relative URLs; the verifier resolves them back to feed-base-relative
/// paths via <see cref="ResolveRelative"/>.
/// </remarks>
public static class FeedPaths
{
    /// <summary>The channel index, at the feed base.</summary>
    public const string ChannelJson = "channel.json";

    /// <summary>The signed revocation list, at the feed base.</summary>
    public const string RevocationsJson = "revocations.json";

    /// <summary>The channel-root-signed, index-coupled compliance policy at the feed base.</summary>
    public const string FeedPolicyJson = "feed-policy.json";

    /// <summary>The manifest document filename inside a version directory.</summary>
    public const string ManifestJson = "manifest.json";

    /// <summary>The per-pack index filename inside a pack directory.</summary>
    public const string IndexJson = "index.json";

    /// <summary>Feed-base-relative directory for one pack.</summary>
    public static string PackDir(string packKey) => $"packs/{packKey}";

    /// <summary>Feed-base-relative path of a pack's version index.</summary>
    public static string PackIndex(string packKey) => $"{PackDir(packKey)}/{IndexJson}";

    /// <summary>Feed-base-relative directory for one version of a pack.</summary>
    public static string VersionDir(string packKey, string version) => $"{PackDir(packKey)}/{version}";

    /// <summary>Feed-base-relative path of a version's manifest document.</summary>
    public static string ManifestPath(string packKey, string version)
        => $"{VersionDir(packKey, version)}/{ManifestJson}";

    /// <summary>The artifact blob filename, embedding its own content-address (§2.3: the path IS the
    /// integrity check).</summary>
    public static string ArtifactFileName(Cid artifactCid) => $"artifact.{artifactCid.Value}.pack";

    /// <summary>Feed-base-relative path of a version's content-addressed artifact blob.</summary>
    public static string ArtifactPath(string packKey, string version, Cid artifactCid)
        => $"{VersionDir(packKey, version)}/{ArtifactFileName(artifactCid)}";

    // ── doc-relative URL builders (what goes INSIDE a signed document) ──────────────────────────

    /// <summary>The channel index's pointer to a per-pack index (feed-base-relative).</summary>
    public static string ChannelIndexUrl(string packKey) => PackIndex(packKey);

    /// <summary>A per-pack index's pointer to a version's manifest (index-dir-relative).</summary>
    public static string PerPackManifestUrl(string version) => $"{version}/{ManifestJson}";

    /// <summary>A per-pack index's pointer to a version's artifact (index-dir-relative).</summary>
    public static string PerPackArtifactUrl(string version, Cid artifactCid)
        => $"{version}/{ArtifactFileName(artifactCid)}";

    // ── resolution (verifier side) ──────────────────────────────────────────────────────────────

    /// <summary>The feed-base-relative directory containing <paramref name="documentPath"/> (the part
    /// before the last <c>/</c>, or empty for a base-level document).</summary>
    public static string DirectoryOf(string documentPath)
    {
        ArgumentNullException.ThrowIfNull(documentPath);
        var slash = documentPath.LastIndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? string.Empty : documentPath[..slash];
    }

    /// <summary>Resolves a document-relative URL against the directory of the document that carried it,
    /// producing a feed-base-relative path. Fail-closed: an absolute URL, a scheme, a backslash, or a
    /// <c>..</c> traversal segment throws (a feed path is a plain forward-slash relative name; anything
    /// else is a malformed / hostile document, never followed).</summary>
    public static string ResolveRelative(string baseDir, string relativeUrl)
    {
        ArgumentNullException.ThrowIfNull(baseDir);
        ArgumentNullException.ThrowIfNull(relativeUrl);

        if (relativeUrl.Length == 0)
            throw new FormatException("Feed URL is empty.");
        if (relativeUrl.Contains('\\', StringComparison.Ordinal))
            throw new FormatException($"Feed URL '{relativeUrl}' contains a backslash.");
        if (relativeUrl.StartsWith('/', StringComparison.Ordinal) || relativeUrl.Contains("://", StringComparison.Ordinal))
            throw new FormatException($"Feed URL '{relativeUrl}' is not a plain relative path.");

        foreach (var segment in relativeUrl.Split('/'))
        {
            if (segment is ".." )
                throw new FormatException($"Feed URL '{relativeUrl}' contains a parent-traversal segment.");
        }

        return baseDir.Length == 0 ? relativeUrl : $"{baseDir}/{relativeUrl}";
    }
}
