namespace Harborline.Api.Foundation.UpdateFeed.Verify;

/// <summary>
/// An <see cref="IFeedSource"/> backed by a local directory (the feed base). Used by the offline verify
/// script and any consumer that has copied the tree to disk (an intranet cache, a USB sideload). Path
/// resolution is confined to the root: a resolved path that escapes the root, or that is absent, reads
/// as "not found" (fail-closed) rather than reaching outside the feed.
/// </summary>
public sealed class DirectoryFeedSource : IFeedSource
{
    private readonly string _root;

    /// <summary>Constructs a source rooted at <paramref name="feedBaseDirectory"/>.</summary>
    public DirectoryFeedSource(string feedBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedBaseDirectory);
        _root = Path.GetFullPath(feedBaseDirectory);
    }

    /// <inheritdoc />
    public bool TryRead(string path, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        ArgumentNullException.ThrowIfNull(path);

        // Feed paths are forward-slash relative; map to a local path and confine to the root.
        var relative = path.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, relative));

        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            !string.Equals(full, _root, StringComparison.Ordinal))
        {
            return false; // escaped the feed root — refuse (never read outside the tree).
        }

        if (!File.Exists(full))
        {
            return false;
        }

        bytes = File.ReadAllBytes(full);
        return true;
    }
}
