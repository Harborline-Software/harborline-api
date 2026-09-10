using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Harborline.Api.NodeOperatorCli;

/// <summary>Runs operator commands against a local node's HTTP listener.</summary>
public static class OperatorCli
{
    /// <summary>Parses and executes one operator command.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="client">HTTP transport used to reach the node.</param>
    /// <param name="stdout">Successful command output.</param>
    /// <param name="stderr">Validation and HTTP error output.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP exchange.</param>
    /// <returns>Zero on success; non-zero on validation or HTTP failure.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        HttpClient client,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var parsed = ParsedArguments.Parse(args);
        if (parsed.Error is not null)
        {
            await WriteErrorAsync(stderr, parsed.Json, "invalid_arguments", parsed.Error).ConfigureAwait(false);
            return 2;
        }

        string? exportOutPath = null;

        HttpRequestMessage? pendingRequest = parsed.Command switch
        {
            ["health"] => new HttpRequestMessage(HttpMethod.Get, new Uri(parsed.BaseUri, "/ready")),
            ["tenant", "list"] => new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(parsed.BaseUri, "/api/local-node/teams")),
            ["export"] => JsonPost(
                parsed.BaseUri,
                "/api/local-node/data-exports",
                new { format = "application/json", includeScopes = Array.Empty<string>() }),
            _ => null,
        };
        if (parsed.Command is ["pack", "install" or "verify", "--file", var packPath])
        {
            if (!File.Exists(packPath))
            {
                await WriteErrorAsync(
                    stderr,
                    parsed.Json,
                    "pack_file_not_found",
                    $"Pack file not found: {packPath}").ConfigureAwait(false);
                return 2;
            }

            // Byte-exact upload for both: the CLI must not be able to quietly reshape a signed pack,
            // and verify must see exactly the bytes install would.
            pendingRequest = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(
                    parsed.BaseUri,
                    parsed.Command[1] == "install"
                        ? "/api/local-node/packs/install"
                        : "/api/local-node/packs/verify"))
            {
                Content = new ByteArrayContent(
                    await File.ReadAllBytesAsync(packPath, cancellationToken).ConfigureAwait(false)),
            };
            pendingRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        else if (parsed.Command is
                 ["pack", "activate", "--pack-key", var packKey, "--version", var version])
        {
            pendingRequest = JsonPost(
                parsed.BaseUri,
                "/api/local-node/packs/activate",
                new { packKey, version });
        }
        else if (parsed.Command is
                 ["pack", "deactivate", "--pack-key", var deactivateKey, "--version", var deactivateVersion])
        {
            pendingRequest = JsonPost(
                parsed.BaseUri,
                "/api/local-node/packs/deactivate",
                new { packKey = deactivateKey, version = deactivateVersion });
        }
        else if (parsed.Command is ["pack", "export", "--request", var requestPath, "--out", var outPath])
        {
            if (!File.Exists(requestPath))
            {
                await WriteErrorAsync(
                    stderr,
                    parsed.Json,
                    "export_request_not_found",
                    $"Export request file not found: {requestPath}").ConfigureAwait(false);
                return 2;
            }

            // The request document is posted verbatim: the node's export route owns validation, and
            // the CLI reshaping a composition request would be a second authoring surface.
            exportOutPath = outPath;
            pendingRequest = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(parsed.BaseUri, "/api/local-node/packs/export"))
            {
                Content = new ByteArrayContent(
                    await File.ReadAllBytesAsync(requestPath, cancellationToken).ConfigureAwait(false)),
            };
            pendingRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        else if (parsed.Command is ["export", "--scope", var scope])
        {
            pendingRequest = JsonPost(
                parsed.BaseUri,
                "/api/local-node/data-exports",
                new { format = "application/json", includeScopes = new[] { scope } });
        }

        using var request = pendingRequest;
        if (request is null)
        {
            await WriteErrorAsync(stderr, parsed.Json, "unknown_command", "Unknown command.")
                .ConfigureAwait(false);
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(parsed.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", parsed.Token);
        }

        HttpResponseMessage response;
        var content = string.Empty;
        byte[]? downloadedFile = null;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && exportOutPath is not null)
            {
                downloadedFile = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException exception)
        {
            await WriteErrorAsync(stderr, parsed.Json, "connection_failed", exception.Message).ConfigureAwait(false);
            return 3;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync(stderr, parsed.Json, "request_timeout", exception.Message).ConfigureAwait(false);
            return 3;
        }

        using (response)
        {
            if (downloadedFile is not null && exportOutPath is not null)
            {
                await File.WriteAllBytesAsync(exportOutPath, downloadedFile, cancellationToken).ConfigureAwait(false);
                await stdout.WriteLineAsync(
                    parsed.Json
                        ? JsonSerializer.Serialize(new { file = exportOutPath, bytes = downloadedFile.Length })
                        : $"Wrote {exportOutPath} ({downloadedFile.Length} bytes)", cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (parsed.Json && !IsJson(content))
            {
                // A node JSON error body passes through verbatim; anything else becomes machine-readable
                // here, carrying the status on failure because the body alone no longer identifies it.
                content = response.IsSuccessStatusCode
                    ? parsed.Command is ["health"]
                        ? JsonSerializer.Serialize(new { status = content.Trim() })
                        : JsonSerializer.Serialize(new { result = content.Trim() })
                    : JsonSerializer.Serialize(new { error = "http_error", status = (int)response.StatusCode, message = content.Trim() });
            }
            var output = response.IsSuccessStatusCode ? stdout : stderr;
            await output.WriteLineAsync(content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
    }

    private static HttpRequestMessage JsonPost(Uri baseUri, string path, object body) => new(
        HttpMethod.Post,
        new Uri(baseUri, path))
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static bool IsJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Task WriteErrorAsync(TextWriter stderr, bool json, string code, string message) =>
        stderr.WriteLineAsync(
            json
                ? JsonSerializer.Serialize(new { error = code, message })
                : message);

    private sealed record ParsedArguments(
        Uri BaseUri,
        string? Token,
        bool Json,
        IReadOnlyList<string> Command,
        string? Error)
    {
        internal static ParsedArguments Parse(IReadOnlyList<string> args)
        {
            string? url = Environment.GetEnvironmentVariable("HARBORLINE_NODE_URL");
            string? token = Environment.GetEnvironmentVariable("HARBORLINE_NODE_TOKEN");
            var json = false;
            var command = new List<string>();

            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--url" when index + 1 < args.Count:
                        url = args[++index];
                        break;
                    case "--token" when index + 1 < args.Count:
                        token = args[++index];
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "--url" or "--token":
                        return ParseError("An option value is missing.", json);
                    default:
                        command.Add(args[index]);
                        break;
                }
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme is not ("http" or "https"))
            {
                return ParseError("--url <http(s)://node> or HARBORLINE_NODE_URL is required.", json);
            }

            return new ParsedArguments(baseUri, token, json, command, null);
        }

        private static ParsedArguments ParseError(string error, bool json) =>
            new(new Uri("http://127.0.0.1"), null, json, Array.Empty<string>(), error);
    }
}
