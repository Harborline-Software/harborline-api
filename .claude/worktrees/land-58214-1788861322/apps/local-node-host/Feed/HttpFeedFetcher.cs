using System.Collections.Generic;
using System.Net.Http;

using Harborline.Api.Foundation.UpdateFeed.Contract;
using Harborline.Api.Foundation.UpdateFeed.Serialization;
using Harborline.Api.Foundation.UpdateFeed.Verify;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>
/// Fetches an update-feed tree over HTTP(S) into a <see cref="MaterializedFeedSource"/> so the SYNCHRONOUS
/// <see cref="FeedTreeVerifier"/> can then verify it without a per-read sync-over-async block. The node — not
/// the browser — makes the outbound fetch (update-feed §5.1: the browser never needs cross-origin CDN access,
/// and the node holds the trust store). This fetcher TRUSTS NOTHING it downloads: it parses the channel +
/// per-pack indexes ONLY to DISCOVER which bounded set of files to fetch; every byte is then re-verified by
/// the verifier against the pinned root. A hostile CDN can, at worst, make discovery fetch the wrong files —
/// which the verifier reports as a fail-closed missing/mismatch finding, never a trusted install.
/// </summary>
/// <remarks>
/// Path safety travels from <see cref="FeedPaths.ResolveRelative"/>: an absolute URL, a scheme, a backslash,
/// or a <c>..</c> traversal in an (untrusted) document throws and that file is simply skipped — the fetcher
/// never dereferences a hostile URL, and the verifier then reports the missing file. Fetches are bounded to
/// the channel's declared packs (capped) × their single declared latest version, and each response is capped
/// so a hostile CDN cannot exhaust memory.
/// </remarks>
public sealed class HttpFeedFetcher
{
    /// <summary>Defensive cap on how many packs a single (unverified) channel index may cause fetches for.</summary>
    public const int MaxPacks = 512;

    /// <summary>Defensive per-file byte cap (a hostile CDN must not exhaust memory). 64 MiB is far above any
    /// real content pack yet bounds a malicious response.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The named <see cref="HttpClient"/> the fetcher creates per fetch (timeout configured at
    /// registration). A per-fetch client from the factory keeps handler rotation correct without a captive
    /// singleton client.</summary>
    public const string HttpClientName = "update-feed";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FeedFileCodec _codec = new();

    /// <summary>Constructs a fetcher over the host's <see cref="IHttpClientFactory"/> (a fresh, properly
    /// lifetime-managed client is created per fetch).</summary>
    public HttpFeedFetcher(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>Fetches the bounded feed tree at <paramref name="feedBaseUrl"/> into a materialized source.
    /// A file that cannot be fetched is simply absent from the map — the verifier reports it fail-closed.</summary>
    public async Task<MaterializedFeedSource> FetchAsync(string feedBaseUrl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedBaseUrl);
        var baseUri = new Uri(feedBaseUrl.EndsWith('/') ? feedBaseUrl : feedBaseUrl + "/", UriKind.Absolute);

        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        using var http = _httpClientFactory.CreateClient(HttpClientName);

        // (1) channel.json — the entry document. If it is unreachable, return what we have (empty ⇒ the
        //     verifier reports ChannelMissing; the client maps that to an honest "channel unreachable").
        if (!await TryFetchAsync(http, baseUri, FeedPaths.ChannelJson, files, ct).ConfigureAwait(false))
        {
            return new MaterializedFeedSource(files);
        }

        // Decode UNTRUSTED, only to discover the revocation, policy, and pack-index URLs. A decode failure
        // just stops discovery — the verifier will report ChannelMalformed over the bytes already fetched.
        var signedChannel = _codec.TryDecode<ChannelIndex>(files[FeedPaths.ChannelJson].Span);
        if (signedChannel is null)
        {
            return new MaterializedFeedSource(files);
        }
        var channel = signedChannel.Payload;
        var baseDir = FeedPaths.DirectoryOf(FeedPaths.ChannelJson); // "" — channel.json is at the feed base.

        // (2) revocations.json — resolved relative to the channel document (F3 coupling verified later).
        if (TryResolve(baseDir, channel.RevocationsUrl, out var revPath))
        {
            await TryFetchAsync(http, baseUri, revPath, files, ct).ConfigureAwait(false);
        }

        // (3) feed-policy.json — the exact channel-root-signed policy is CID-coupled from channel.json.
        //     This fetch is discovery only; FeedTreeVerifier checks the CID, signature, and root.
        if (TryResolve(baseDir, channel.FeedPolicyUrl, out var policyPath))
        {
            await TryFetchAsync(http, baseUri, policyPath, files, ct).ConfigureAwait(false);
        }

        // (4) per-pack chains — bounded to the declared packs (capped) × their single declared latest.
        foreach (var packRef in channel.Packs.Take(MaxPacks))
        {
            await FetchPackAsync(http, baseUri, baseDir, packRef, files, ct).ConfigureAwait(false);
        }

        return new MaterializedFeedSource(files);
    }

    private async Task FetchPackAsync(
        HttpClient http,
        Uri baseUri,
        string baseDir,
        ChannelPackRef packRef,
        Dictionary<string, ReadOnlyMemory<byte>> files,
        CancellationToken ct)
    {
        if (!TryResolve(baseDir, packRef.IndexUrl, out var indexPath) ||
            !await TryFetchAsync(http, baseUri, indexPath, files, ct).ConfigureAwait(false))
        {
            return;
        }

        var signedIndex = _codec.TryDecode<PerPackIndex>(files[indexPath].Span);
        if (signedIndex is null)
        {
            return; // undecodable index — verifier reports it; nothing more to discover.
        }

        // Only the channel's declared latest (non-yanked) version is needed to acquire the update.
        var entry = signedIndex.Payload.Versions.FirstOrDefault(
            v => string.Equals(v.Version, packRef.Latest, StringComparison.Ordinal) && v.YankedAt is null);
        if (entry is null)
        {
            return; // verifier reports LatestMissing.
        }

        var indexDir = FeedPaths.DirectoryOf(indexPath);
        if (TryResolve(indexDir, entry.ManifestUrl, out var manifestPath))
        {
            await TryFetchAsync(http, baseUri, manifestPath, files, ct).ConfigureAwait(false);
        }
        if (TryResolve(indexDir, entry.ArtifactUrl, out var artifactPath))
        {
            await TryFetchAsync(http, baseUri, artifactPath, files, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Resolve an (untrusted) document-relative URL to a feed-base-relative path, mapping a
    /// malformed / hostile path to "skip" instead of following it.</summary>
    private static bool TryResolve(string baseDir, string relativeUrl, out string resolved)
    {
        try
        {
            resolved = FeedPaths.ResolveRelative(baseDir, relativeUrl);
            return true;
        }
        catch (FormatException)
        {
            resolved = string.Empty;
            return false;
        }
        catch (ArgumentNullException)
        {
            resolved = string.Empty;
            return false;
        }
    }

    /// <summary>GET one feed-base-relative <paramref name="path"/> into <paramref name="files"/>. Returns
    /// false (and adds nothing) on any non-success / oversize / transport error — fail-closed, the verifier
    /// reports the resulting missing file.</summary>
    private static async Task<bool> TryFetchAsync(
        HttpClient http,
        Uri baseUri,
        string path,
        Dictionary<string, ReadOnlyMemory<byte>> files,
        CancellationToken ct)
    {
        try
        {
            var uri = new Uri(baseUri, path);
            using var response = await http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            if (response.Content.Headers.ContentLength is { } len && len > MaxFileBytes)
            {
                return false; // oversize per the declared length — refuse before reading.
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.LongLength > MaxFileBytes)
            {
                return false; // oversize after read (no/short declared length) — refuse.
            }

            files[path] = bytes;
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // a per-request timeout is a transport failure, not a caller cancellation.
        }
    }
}
