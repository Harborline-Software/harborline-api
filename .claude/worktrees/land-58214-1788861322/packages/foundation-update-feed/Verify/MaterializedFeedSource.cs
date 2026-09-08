using System.Collections.Generic;

namespace Harborline.Api.Foundation.UpdateFeed.Verify;

/// <summary>
/// An <see cref="IFeedSource"/> over an in-memory map of feed-base-relative path → bytes. This is the
/// seam that lets an ASYNC transport (an HTTP CDN client, U2) do its I/O UP FRONT — fetch the bounded set
/// of feed files into a dictionary — and then run the SYNCHRONOUS <see cref="FeedTreeVerifier"/> over the
/// materialized bytes, without a sync-over-async block per read. It trusts the bytes no more than a
/// <see cref="DirectoryFeedSource"/> does: the verifier still checks every signature + content-address, so
/// a materialized tree assembled from a hostile CDN is caught exactly as an on-disk one is (§2.3 — the
/// node verifies vs the pinned root, never the source that produced the bytes).
/// </summary>
/// <remarks>
/// The map keys are feed-base-relative forward-slash paths — the SAME keys <see cref="FeedTreeVerifier"/>
/// asks for (<c>channel.json</c>, <c>revocations.json</c>, <c>packs/&lt;key&gt;/index.json</c>, …). An
/// absent key reads as "not found" (fail-closed), never an exception, so a partial materialization (a
/// transport that could not fetch one file) surfaces as the verifier's own honest missing-file finding.
/// </remarks>
public sealed class MaterializedFeedSource : IFeedSource
{
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _files;

    /// <summary>Constructs a source over the already-fetched <paramref name="files"/> map (feed-base-relative
    /// forward-slash path → bytes).</summary>
    public MaterializedFeedSource(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public bool TryRead(string path, out ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _files.TryGetValue(path, out bytes);
    }
}
