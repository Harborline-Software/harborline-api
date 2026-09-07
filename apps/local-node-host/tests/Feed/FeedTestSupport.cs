using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.DependencyInjection;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.UpdateFeed.Build;
using Harborline.Api.Foundation.UpdateFeed.Contract;
using Harborline.Api.Foundation.UpdateFeed.Serialization;
using Harborline.Api.Foundation.UpdateFeed.Verify;

namespace Harborline.Api.LocalNodeHost.Tests.Feed;

/// <summary>A <see cref="TimeProvider"/> pinned to a fixed instant (freeze/expiry tests).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Builds REAL signed feed trees (a genuine <see cref="PackExporter"/> artifact + <see cref="FeedBuilder"/>)
/// into a path→bytes map, mirroring the U1 verifier tests' <c>TestFeed</c> — so the node client's adversarial
/// matrix mutates exactly one thing (a byte, the sequence, the validUntil, the pinned root) from a known-good
/// baseline. A fixed key seed keeps signatures reproducible across runs.
/// </summary>
internal static class FeedTestBuilder
{
    private static readonly byte[] RootSeed =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30,
        0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,
    ];

    public static readonly DateTimeOffset Base = new(2026, 7, 7, 14, 0, 0, TimeSpan.Zero);
    public const long PublisherEpoch = 1;
    public const string PackKey = "harborline.node-test-welcome";

    /// <summary>The default <c>channel</c> a <see cref="BuildAsync"/> feed carries — matches the
    /// <c>channel</c> default param, exposed as a stable constant for route-level tests that need it in
    /// the <c>/channels/{id}/check</c> path.</summary>
    public const string ChannelId = "harborline-nodetest";

    internal sealed record Feed(
        Dictionary<string, byte[]> Files, KeyPair KeyPair, FeedPinnedRoot Root, long Sequence, DateTimeOffset ValidUntil);

    /// <summary>Builds a one-pack feed with the given knobs. Defaults are a fresh, valid, non-revoked feed.</summary>
    public static async Task<Feed> BuildAsync(
        long sequence = 1,
        DateTimeOffset? validUntil = null,
        string channel = "harborline-nodetest",
        bool revokePublisher = false,
        long minNodeEpoch = 1)
    {
        var keyPair = KeyPair.FromSeed(RootSeed);
        var signer = new Ed25519Signer(keyPair);
        var feedCodec = new FeedFileCodec();
        var vu = validUntil ?? Base.AddDays(30);

        var exporter = new ServiceCollection()
            .AddTestKernelClock()
            .AddPackComposerExportVerify()
            .BuildServiceProvider()
            .GetRequiredService<IPackExporter>();

        var content = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Welcome",
            ["sections"] = new System.Text.Json.Nodes.JsonArray("home", "records", "apps"),
        };
        var request = new PackExportRequest(
            Key: PackKey,
            Version: "0.1.0",
            Name: "Node Test Welcome",
            Description: "A tiny nav config for the node update-feed client tests.",
            ScopeTier: PackScopeTier.Horizontal,
            Contents: [new PackContentSource("welcome-nav", PackContentKind.NavWorkspaceConfig, "0.1.0", content)],
            Dependencies: Array.Empty<PackDependencyRef>(),
            CapabilityRequirements: Array.Empty<string>(),
            Epoch: PublisherEpoch,
            RenamedFrom: null,
            Dcp: DomainComplianceProfile.General("Harborline-Software"));

        var outcome = await exporter.ExportAsync(request, signer);
        if (!outcome.Succeeded || outcome.File?.Envelope is null || outcome.FileBytes is null)
        {
            throw new InvalidOperationException(
                "test pack export failed: " +
                string.Join("; ", outcome.Validation.Errors.Select(e => $"{e.Code}: {e.Message}")));
        }

        var revoked = revokePublisher
            ? new[] { new PackRevokedKey(keyPair.PrincipalId, PublisherEpoch) }
            : Array.Empty<PackRevokedKey>();

        var buildRequest = new FeedBuildRequest(
            Channel: channel,
            GeneratedAt: Base,
            ValidUntil: vu,
            Sequence: sequence,
            TtlSeconds: 3600,
            RevocationSequence: 1,
            Revoked: revoked,
            RevocationIssuedAt: Base,
            Policy: FeedPolicy.HarborlinePublic("dcp-counsel-cleared-classes/v1"),
            Packs:
            [
                new FeedPackInput(PackKey, "0.1.0",
                [
                    new FeedPackVersionInput(
                        Version: "0.1.0",
                        ArtifactBytes: outcome.FileBytes,
                        ManifestEnvelope: outcome.File.Envelope,
                        PublishedAt: Base,
                        MinNodeEpoch: minNodeEpoch,
                        Supersedes: null,
                        YankedAt: null),
                ]),
            ]);

        var tree = await new FeedBuilder(feedCodec).BuildAsync(buildRequest, signer);
        var files = tree.Files.ToDictionary(f => f.Path, f => f.Bytes.ToArray(), StringComparer.Ordinal);
        var root = new FeedPinnedRoot(keyPair.PrincipalId, PublisherEpoch);
        return new Feed(files, keyPair, root, sequence, vu);
    }

    /// <summary>The feed-base-relative path of the single artifact blob.</summary>
    public static string ArtifactPath(IReadOnlyDictionary<string, byte[]> files)
        => files.Keys.Single(k => k.Contains("/artifact.", StringComparison.Ordinal));

    /// <summary>A pinned root for a DIFFERENT key than the feed was signed with (the wrong-root case).</summary>
    public static FeedPinnedRoot WrongRoot()
    {
        var other = new byte[32];
        for (var i = 0; i < other.Length; i++) other[i] = (byte)(0xA0 + i);
        return new FeedPinnedRoot(KeyPair.FromSeed(other).PrincipalId, PublisherEpoch);
    }
}

/// <summary>
/// An in-process HTTP server that serves a path→bytes feed map at <c>/feed/&lt;path&gt;</c> — so the node
/// client's <see cref="HttpFeedFetcher"/> is exercised over REAL HTTP (not a stub). Also exposes an
/// <see cref="IHttpClientFactory"/> so a client under test fetches through the standard factory.
/// </summary>
internal sealed class FeedTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IHttpClientFactory _httpClientFactory;

    private FeedTestServer(WebApplication app, string baseUrl, IHttpClientFactory httpClientFactory)
    {
        _app = app;
        FeedBaseUrl = baseUrl;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>The feed base URL (ends with <c>/feed/</c>).</summary>
    public string FeedBaseUrl { get; }

    /// <summary>An <see cref="IHttpClientFactory"/> the fetcher can use.</summary>
    public IHttpClientFactory HttpClientFactory => _httpClientFactory;

    /// <summary>Starts a server that serves <paramref name="files"/> (a snapshot — later mutations to the
    /// passed dictionary are NOT reflected; pass the final bytes).</summary>
    public static async Task<FeedTestServer> StartAsync(IReadOnlyDictionary<string, byte[]> files)
    {
        var snapshot = files.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/feed/{**path}", (string path) =>
            snapshot.TryGetValue(path, out var bytes)
                ? Results.Bytes(bytes, "application/octet-stream")
                : Results.NotFound());

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var httpClientFactory = new ServiceCollection().AddHttpClient()
            .BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        return new FeedTestServer(app, $"{address}/feed/", httpClientFactory);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Loads U1's ACTUAL published dogfood feed from the copied golden fixtures (
/// <c>Feed/dogfood-feed/</c>, byte-for-byte from harborline-www) plus its pinned root — the "live end-to-end
/// against U1's published dogfood feed" the U2 gate requires runs against these exact bytes.
/// </summary>
internal static class GoldenDogfoodFeed
{
    /// <summary>The on-disk fixture directory (copied to the test output).</summary>
    public static string Directory =>
        Path.Combine(AppContext.BaseDirectory, "Feed", "dogfood-feed");

    /// <summary>The dogfood channel id / pack key / version (matches <c>channel.json</c>).</summary>
    public const string ChannelId = "harborline-dogfood";
    public const string PackKey = "harborline.dogfood-welcome";
    public const string Version = "0.1.0";

    /// <summary>Every feed file as a feed-base-relative path→bytes map.</summary>
    public static Dictionary<string, byte[]> LoadFiles()
    {
        var root = Directory;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var full in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            // The pinned-root pub + the README are OUT-OF-BAND anchors, not part of the served feed tree.
            if (rel is "channel-root.pub.json" or "FEED-README.md") continue;
            files[rel] = File.ReadAllBytes(full);
        }
        return files;
    }

    /// <summary>The pinned dogfood root, read from the committed <c>channel-root.pub.json</c> (the same key the
    /// node binary pins as <see cref="Harborline.Api.LocalNodeHost.Feed.DogfoodChannel"/>).</summary>
    public static FeedPinnedRoot PinnedRoot()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "channel-root.pub.json")));
        var keyId = doc.RootElement.GetProperty("keyIdBase64Url").GetString()!;
        var epoch = doc.RootElement.GetProperty("publisherEpoch").GetInt64();
        return new FeedPinnedRoot(PrincipalId.FromBase64Url(keyId), epoch);
    }
}
