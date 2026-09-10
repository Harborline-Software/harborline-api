using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Xunit;

using NodeOperatorCommand = global::Harborline.Api.NodeOperatorCli.OperatorCli;

namespace Harborline.Api.LocalNodeHost.Tests.OperatorCli;

/// <summary>
/// Drives every verb in cli-coverage.json through <see cref="NodeOperatorCommand.RunAsync"/> and
/// proves that it reaches exactly the method and route claimed by its manifest key. A manifest verb
/// without an argv-table entry fails by name, forcing this table to grow with the manifest.
/// </summary>
[Collection("Harborline process environment")]
public sealed class CliVerbRouteBindingTests
{
    [Fact]
    public async Task Every_manifest_verb_drives_exactly_the_route_pair_its_key_claims()
    {
        var verbs = LoadManifestVerbs();
        var previousUrl = Environment.GetEnvironmentVariable("HARBORLINE_NODE_URL");
        var previousToken = Environment.GetEnvironmentVariable("HARBORLINE_NODE_TOKEN");
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", null);
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", null);
        try
        {
            var table = new Dictionary<string, Func<VerbFixture>>(StringComparer.Ordinal)
            {
                ["health"] = () => VerbFixture.Create("health"),
                ["tenant list"] = () => VerbFixture.Create("tenant", "list"),
                ["pack install"] = () => VerbFixture.WithPackFile("install"),
                ["pack verify"] = () => VerbFixture.WithPackFile("verify"),
                ["pack activate"] = () => VerbFixture.Create(
                    "pack", "activate", "--pack-key", "general", "--version", "1.2.3"),
                ["pack deactivate"] = () => VerbFixture.Create(
                    "pack", "deactivate", "--pack-key", "general", "--version", "1.2.3"),
                ["export"] = () => VerbFixture.Create("export"),
                ["pack export"] = VerbFixture.WithExportRequest,
                ["record create"] = VerbFixture.WithRecordFile,
            };

            foreach (var (pair, verb) in verbs)
            {
                Assert.True(
                    table.ContainsKey(verb),
                    $"cli-coverage.json verb '{verb}' (for '{pair}') has no argv entry in this test's table; add one so the binding stays proven.");

                using var fixture = table[verb]();
                HttpRequestMessage? observed = null;
                using var client = new HttpClient(new RecordingHandler(request =>
                {
                    observed = request;
                    if (verb.Equals("pack export", StringComparison.Ordinal))
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent([0x48, 0x4c]),
                        };
                        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        return response;
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    };
                }));
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();

                var exitCode = await NodeOperatorCommand.RunAsync(
                    fixture.Arguments,
                    client,
                    stdout,
                    stderr);

                Assert.Equal(0, exitCode);
                Assert.NotNull(observed);
                var separator = pair.IndexOf(' ');
                var expectedMethod = pair[..separator];
                var expectedPath = pair[(separator + 1)..];
                var observedPair = $"{observed.Method.Method} {observed.RequestUri!.AbsolutePath}";

                // "*" is the manifest's any-method marker, so only its path is contractual.
                if (expectedMethod.Equals("*", StringComparison.Ordinal))
                {
                    Assert.True(
                        expectedPath.Equals(observed.RequestUri!.AbsolutePath, StringComparison.Ordinal),
                        $"Verb '{verb}' expected manifest pair '{pair}' but observed '{observedPair}'.");
                }
                else
                {
                    Assert.True(
                        pair.Equals(observedPair, StringComparison.Ordinal),
                        $"Verb '{verb}' expected manifest pair '{pair}' but observed '{observedPair}'.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", previousUrl);
            Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", previousToken);
        }
    }

    private static Dictionary<string, string> LoadManifestVerbs()
    {
        var path = Path.Combine(
            LocateHostSourceRoot(), "..", "node-operator-cli", "cli-coverage.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("verbs").EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);
    }

    private static string LocateHostSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Harborline.LocalNodeHost.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the local-node host source root.");
    }

    private sealed class VerbFixture(IReadOnlyList<string> arguments, IReadOnlyList<string> tempFiles) : IDisposable
    {
        internal IReadOnlyList<string> Arguments { get; } = arguments;

        internal static VerbFixture Create(params string[] command) =>
            new(["--url", "http://127.0.0.1:7312", "--json", .. command], []);

        internal static VerbFixture WithPackFile(string operation)
        {
            var packPath = Path.Combine(
                Path.GetTempPath(),
                $"harborline-cli-binding-{Guid.NewGuid():N}.pack");
            File.WriteAllBytes(packPath, [0x48, 0x4c, 0x50]);
            return new VerbFixture(
                ["--url", "http://127.0.0.1:7312", "--json", "pack", operation, "--file", packPath],
                [packPath]);
        }

        internal static VerbFixture WithRecordFile()
        {
            var recordPath = Path.Combine(
                Path.GetTempPath(),
                $"harborline-cli-binding-{Guid.NewGuid():N}.json");
            File.WriteAllText(recordPath, """{"legalName":"Binding LLC"}""");
            return new VerbFixture(
                ["--url", "http://127.0.0.1:7312", "--json", "record", "create", "--file", recordPath],
                [recordPath]);
        }

        internal static VerbFixture WithExportRequest()
        {
            var requestPath = Path.Combine(
                Path.GetTempPath(),
                $"harborline-cli-binding-{Guid.NewGuid():N}.json");
            var outPath = Path.Combine(
                Path.GetTempPath(),
                $"harborline-cli-binding-{Guid.NewGuid():N}.pack");
            File.WriteAllText(requestPath, "{}");
            return new VerbFixture(
                ["--url", "http://127.0.0.1:7312", "--json", "pack", "export", "--request", requestPath, "--out", outPath],
                [requestPath, outPath]);
        }

        public void Dispose()
        {
            foreach (var path in tempFiles)
            {
                File.Delete(path);
            }
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
