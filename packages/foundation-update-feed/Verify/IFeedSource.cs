namespace Harborline.Api.Foundation.UpdateFeed.Verify;

/// <summary>
/// A read-only view of a feed tree by feed-base-relative path. Abstracting the source is what makes the
/// verifier a "mirror is a dumb copy" proof (design note §2.3): the SAME verify logic runs over an
/// on-disk directory, an in-memory tree, an HTTP-backed CDN client (U2), or a USB sideload — it never
/// trusts the source, only the signatures + content-addresses of the bytes it reads.
/// </summary>
public interface IFeedSource
{
    /// <summary>Reads the bytes at <paramref name="path"/> (forward-slash, feed-base-relative).
    /// Returns <c>false</c> if the path is absent (a missing file is a verify failure the caller reports,
    /// never an exception across the boundary).</summary>
    bool TryRead(string path, out ReadOnlyMemory<byte> bytes);
}
