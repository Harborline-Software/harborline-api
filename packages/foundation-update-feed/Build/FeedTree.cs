using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.UpdateFeed.Verify;

namespace Harborline.Api.Foundation.UpdateFeed.Build;

/// <summary>One file in a feed tree: a feed-base-relative path and its exact bytes.</summary>
/// <param name="Path">Forward-slash, feed-base-relative path (see <c>FeedPaths</c>).</param>
/// <param name="Bytes">The exact bytes to write; the file's content-address is computed over these.</param>
public sealed record FeedFile(string Path, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// The in-memory result of <see cref="FeedBuilder"/> — the complete feed tree as an ORDERED list of
/// files. The order is the F3 publish order (design note §7.1): <c>revocations.json</c> FIRST, then all
/// pack files (indexes + manifests + artifacts), then <c>channel.json</c> LAST — so the channel index
/// only ever points at files already in place. A generator that writes/uploads in <see cref="Files"/>
/// order inherits the anti-inconsistency discipline for free.
/// </summary>
public sealed class FeedTree
{
    /// <summary>The ordered files (F3 publish order).</summary>
    public IReadOnlyList<FeedFile> Files { get; }

    /// <summary>The channel root that signed the index documents (its public key-id).</summary>
    public PrincipalId ChannelRootKeyId { get; }

    /// <summary>The channel identifier this tree serves.</summary>
    public string Channel { get; }

    internal FeedTree(IReadOnlyList<FeedFile> files, PrincipalId channelRootKeyId, string channel)
    {
        Files = files;
        ChannelRootKeyId = channelRootKeyId;
        Channel = channel;
    }

    /// <summary>A read-only <see cref="IFeedSource"/> over these in-memory bytes — so a caller can
    /// verify the tree WITHOUT writing it to disk, and prove that a byte-for-byte copy (a mirror)
    /// verifies identically (a mirror is a dumb copy, §2.3).</summary>
    public IFeedSource AsSource() => new InMemoryFeedSource(Files);

    private sealed class InMemoryFeedSource(IReadOnlyList<FeedFile> files) : IFeedSource
    {
        private readonly Dictionary<string, ReadOnlyMemory<byte>> _byPath =
            files.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.Ordinal);

        public bool TryRead(string path, out ReadOnlyMemory<byte> bytes)
            => _byPath.TryGetValue(path, out bytes);
    }
}
