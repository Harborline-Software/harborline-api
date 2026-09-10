using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Xunit;

using NodeOperatorCommand = global::Harborline.Api.NodeOperatorCli.OperatorCli;

namespace Harborline.Api.LocalNodeHost.Tests.OperatorCli;

[Collection("Harborline process environment")]
public sealed class OperatorCliTests
{
    [Fact]
    public async Task Health_json_uses_ready_probe_and_existing_caller_auth()
    {
        HttpRequestMessage? observed = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Healthy", Encoding.UTF8, "text/plain"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "--json", "health"],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Equal(HttpMethod.Get, observed.Method);
        Assert.Equal("http://127.0.0.1:7312/ready", observed.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", observed.Headers.Authorization!.Scheme);
        Assert.Equal("caller-secret", observed.Headers.Authorization.Parameter);
        Assert.Equal("{\"status\":\"Healthy\"}" + Environment.NewLine, stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Tenant_list_json_uses_existing_teams_route()
    {
        HttpRequestMessage? observed = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"teamId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"name\":\"Operations\",\"isActive\":true,\"memberCount\":1}]",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--json", "tenant", "list"],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Equal(HttpMethod.Get, observed.Method);
        Assert.Equal("http://127.0.0.1:7312/api/local-node/teams", observed.RequestUri!.AbsoluteUri);
        Assert.StartsWith("[{\"teamId\":", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Entity_create_posts_the_headless_record_write_and_preserves_a_validation_refusal()
    {
        HttpRequestMessage? observed = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"code\":\"entity.validation.body_invalid\",\"pointers\":[\"/legalName\"]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--json", "entity", "create", "--legal-name", "Invalid LLC"],
            client,
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(HttpMethod.Post, observed!.Method);
        Assert.Equal("http://127.0.0.1:7312/api/local-node/entities", observed.RequestUri!.AbsoluteUri);
        Assert.Equal("{\"legalName\":\"Invalid LLC\"}", await observed.Content!.ReadAsStringAsync());
        Assert.Empty(stdout.ToString());
        Assert.Contains("entity.validation.body_invalid", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("/legalName", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pack_install_json_posts_pack_bytes_to_existing_route()
    {
        var packPath = Path.Combine(Path.GetTempPath(), $"harborline-cli-{Guid.NewGuid():N}.pack");
        var expectedBytes = new byte[] { 0x48, 0x4c, 0x50 };
        await File.WriteAllBytesAsync(packPath, expectedBytes);
        try
        {
            HttpRequestMessage? observed = null;
            byte[]? observedBytes = null;
            using var client = new HttpClient(new RecordingHandler(request =>
            {
                observed = request;
                observedBytes = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"installed\":true}", Encoding.UTF8, "application/json"),
                };
            }));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await NodeOperatorCommand.RunAsync(
                ["--url", "http://127.0.0.1:7312", "--json", "pack", "install", "--file", packPath],
                client,
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.NotNull(observed);
            Assert.Equal(HttpMethod.Post, observed.Method);
            Assert.Equal(
                "http://127.0.0.1:7312/api/local-node/packs/install",
                observed.RequestUri!.AbsoluteUri);
            Assert.Equal("application/octet-stream", observed.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(expectedBytes, observedBytes);
            Assert.Equal("{\"installed\":true}" + Environment.NewLine, stdout.ToString());
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task Pack_activate_json_posts_pinned_identity_to_existing_route()
    {
        HttpRequestMessage? observed = null;
        string? observedBody = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"activated\":true}", Encoding.UTF8, "application/json"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            [
                "--url", "http://127.0.0.1:7312", "--json", "pack", "activate",
                "--pack-key", "general", "--version", "1.2.3",
            ],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Equal(HttpMethod.Post, observed.Method);
        Assert.Equal(
            "http://127.0.0.1:7312/api/local-node/packs/activate",
            observed.RequestUri!.AbsoluteUri);
        Assert.Equal("{\"packKey\":\"general\",\"version\":\"1.2.3\"}", observedBody);
        Assert.Equal("{\"activated\":true}" + Environment.NewLine, stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Pack_deactivate_json_posts_pinned_identity_to_existing_route()
    {
        HttpRequestMessage? observed = null;
        string? observedBody = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"deactivated\":true}", Encoding.UTF8, "application/json"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            [
                "--url", "http://127.0.0.1:7312", "--json", "pack", "deactivate",
                "--pack-key", "general", "--version", "1.2.3",
            ],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Equal(HttpMethod.Post, observed.Method);
        Assert.Equal(
            "http://127.0.0.1:7312/api/local-node/packs/deactivate",
            observed.RequestUri!.AbsoluteUri);
        Assert.Equal("{\"packKey\":\"general\",\"version\":\"1.2.3\"}", observedBody);
        Assert.Equal("{\"deactivated\":true}" + Environment.NewLine, stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Export_json_starts_active_tenant_export_on_existing_route()
    {
        HttpRequestMessage? observed = null;
        string? observedBody = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    "{\"exportId\":\"62a26df9-1726-49c1-82ec-aa2330237831\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--json", "export"],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Equal(HttpMethod.Post, observed.Method);
        Assert.Equal(
            "http://127.0.0.1:7312/api/local-node/data-exports",
            observed.RequestUri!.AbsoluteUri);
        Assert.Equal("{\"format\":\"application/json\",\"includeScopes\":[]}", observedBody);
        Assert.StartsWith("{\"exportId\":", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Unknown_command_json_is_machine_readable_without_http()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            throw new InvalidOperationException("Validation failures must not reach HTTP.")));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--json", "unknown"],
            client,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal(
            "{\"error\":\"unknown_command\",\"message\":\"Unknown command.\"}" + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public async Task Pack_verify_json_posts_pack_bytes_to_existing_route()
    {
        var packPath = Path.Combine(Path.GetTempPath(), $"cli-verify-{Guid.NewGuid():N}.pack");
        byte[] packBytes = [0x50, 0x4B, 0x07, 0x08, 0xFF, 0x00];
        await File.WriteAllBytesAsync(packPath, packBytes);
        try
        {
            HttpRequestMessage? observed = null;
            byte[]? observedBody = null;
            using var client = new HttpClient(new RecordingHandler(request =>
            {
                observed = request;
                observedBody = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"verdict\":\"Verified\",\"epoch\":1}", Encoding.UTF8, "application/json"),
                };
            }));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await NodeOperatorCommand.RunAsync(
                ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "--json", "pack", "verify", "--file", packPath],
                client,
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(HttpMethod.Post, observed!.Method);
            Assert.Equal("http://127.0.0.1:7312/api/local-node/packs/verify", observed.RequestUri!.AbsoluteUri);
            Assert.Equal("application/octet-stream", observed.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(packBytes, observedBody);
            Assert.Equal("caller-secret", observed.Headers.Authorization!.Parameter);
            Assert.Equal("{\"verdict\":\"Verified\",\"epoch\":1}" + Environment.NewLine, stdout.ToString());
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task Pack_export_json_posts_request_verbatim_and_writes_the_pack_file()
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"cli-export-{Guid.NewGuid():N}.json");
        var outPath = Path.Combine(Path.GetTempPath(), $"cli-export-{Guid.NewGuid():N}.pack");
        const string requestJson = "{\"key\":\"general\",\"version\":\"1.0.0\",\"scopeTier\":\"General\",\"contents\":[]}";
        await File.WriteAllTextAsync(requestPath, requestJson);
        byte[] packBytes = [0x50, 0x41, 0x43, 0x4B, 0x01];
        try
        {
            HttpRequestMessage? observed = null;
            string? observedBody = null;
            using var client = new HttpClient(new RecordingHandler(request =>
            {
                observed = request;
                observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packBytes),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return response;
            }));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await NodeOperatorCommand.RunAsync(
                ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "--json", "pack", "export", "--request", requestPath, "--out", outPath],
                client,
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(HttpMethod.Post, observed!.Method);
            Assert.Equal("http://127.0.0.1:7312/api/local-node/packs/export", observed.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", observed.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(requestJson, observedBody);
            Assert.Equal("caller-secret", observed.Headers.Authorization!.Parameter);
            Assert.Equal(packBytes, await File.ReadAllBytesAsync(outPath));
            using var summary = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(outPath, summary.RootElement.GetProperty("file").GetString());
            Assert.Equal(packBytes.Length, summary.RootElement.GetProperty("bytes").GetInt32());
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            File.Delete(requestPath);
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
        }
    }

    [Fact]
    public async Task Environment_variables_supply_url_and_token_and_flags_override_them()
    {
        var previousUrl = Environment.GetEnvironmentVariable("HARBORLINE_NODE_URL");
        var previousToken = Environment.GetEnvironmentVariable("HARBORLINE_NODE_TOKEN");
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", "http://127.0.0.1:9001");
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", "env-secret");
        try
        {
            HttpRequestMessage? observed = null;
            using var client = new HttpClient(new RecordingHandler(request =>
            {
                observed = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Healthy", Encoding.UTF8, "text/plain"),
                };
            }));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await NodeOperatorCommand.RunAsync(["--json", "health"], client, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal("http://127.0.0.1:9001/ready", observed!.RequestUri!.AbsoluteUri);
            Assert.Equal("env-secret", observed.Headers.Authorization!.Parameter);

            var overrideExit = await NodeOperatorCommand.RunAsync(
                ["--url", "http://127.0.0.1:9002", "--token", "flag-secret", "--json", "health"],
                client,
                stdout,
                stderr);

            Assert.Equal(0, overrideExit);
            Assert.Equal("http://127.0.0.1:9002/ready", observed!.RequestUri!.AbsoluteUri);
            Assert.Equal("flag-secret", observed.Headers.Authorization!.Parameter);
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", previousUrl);
            Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", previousToken);
        }
    }

    [Fact]
    public async Task Unreachable_node_reports_connection_failed_json_without_a_stack_trace()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.")));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "--json", "health"],
            client,
            stdout,
            stderr);

        Assert.Equal(3, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal(
            "{\"error\":\"connection_failed\",\"message\":\"No connection could be made because the target machine actively refused it.\"}" + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public async Task Timed_out_request_reports_request_timeout_json()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.")));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "--json", "health"],
            client,
            stdout,
            stderr);

        Assert.Equal(3, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal(
            "{\"error\":\"request_timeout\",\"message\":\"The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.\"}" + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public async Task Auth_rejection_with_non_json_body_reports_status_carrying_json_error()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Unauthorized", Encoding.UTF8, "text/plain"),
            }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--token", "wrong-secret", "--json", "tenant", "list"],
            client,
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal(
            "{\"error\":\"http_error\",\"status\":401,\"message\":\"Unauthorized\"}" + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public async Task Http_failure_with_json_body_passes_the_node_error_through_verbatim()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"pack_rejected\",\"reason\":\"signature\"}", Encoding.UTF8, "application/json"),
            }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "--json", "pack", "activate", "--pack-key", "general", "--version", "1.0.0"],
            client,
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal(
            "{\"error\":\"pack_rejected\",\"reason\":\"signature\"}" + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public async Task Without_json_flag_output_is_the_raw_body_and_errors_are_plain_text()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Healthy", Encoding.UTF8, "text/plain"),
            }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--token", "caller-secret", "health"],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal("Healthy" + Environment.NewLine, stdout.ToString());

        using var unknownOut = new StringWriter();
        using var unknownErr = new StringWriter();
        var unknownExit = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "unknown"], client, unknownOut, unknownErr);

        Assert.Equal(2, unknownExit);
        Assert.Equal("Unknown command." + Environment.NewLine, unknownErr.ToString());
    }



[Fact]
public async Task Pack_install_with_missing_file_reports_pack_file_not_found_without_http()
{
    var missingPath = Path.Combine(Path.GetTempPath(), $"harborline-cli-{Guid.NewGuid():N}.pack");
    using var client = new HttpClient(new RecordingHandler(_ =>
        throw new InvalidOperationException("Validation failures must not reach HTTP.")));
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await NodeOperatorCommand.RunAsync(
        ["--url", "http://127.0.0.1:7312", "--json", "pack", "install", "--file", missingPath],
        client,
        stdout,
        stderr);

    Assert.Equal(2, exitCode);
    Assert.Empty(stdout.ToString());
    Assert.Equal(
        JsonSerializer.Serialize(new { error = "pack_file_not_found", message = $"Pack file not found: {missingPath}" }) + Environment.NewLine,
        stderr.ToString());
}

[Fact]
public async Task Pack_export_with_missing_request_reports_export_request_not_found_without_http()
{
    var requestPath = Path.Combine(Path.GetTempPath(), $"harborline-cli-{Guid.NewGuid():N}.json");
    var outPath = Path.Combine(Path.GetTempPath(), $"harborline-cli-{Guid.NewGuid():N}.pack");
    using var client = new HttpClient(new RecordingHandler(_ =>
        throw new InvalidOperationException("Validation failures must not reach HTTP.")));
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await NodeOperatorCommand.RunAsync(
        ["--url", "http://127.0.0.1:7312", "--json", "pack", "export", "--request", requestPath, "--out", outPath],
        client,
        stdout,
        stderr);

    Assert.Equal(2, exitCode);
    Assert.Empty(stdout.ToString());
    Assert.Equal(
        JsonSerializer.Serialize(new { error = "export_request_not_found", message = $"Export request file not found: {requestPath}" }) + Environment.NewLine,
        stderr.ToString());
    Assert.False(File.Exists(outPath));
}

[Fact]
public async Task Missing_url_reports_invalid_arguments_without_http()
{
    var previousUrl = Environment.GetEnvironmentVariable("HARBORLINE_NODE_URL");
    var previousToken = Environment.GetEnvironmentVariable("HARBORLINE_NODE_TOKEN");
    Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", null);
    Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", null);
    try
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            throw new InvalidOperationException("Validation failures must not reach HTTP.")));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(["--json", "health"], client, stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout.ToString());
        // Serialized (not hand-written) because System.Text.Json escapes '<' and '>' to unicode escape sequences.
        Assert.Equal(
            JsonSerializer.Serialize(new { error = "invalid_arguments", message = "--url <http(s)://node> or HARBORLINE_NODE_URL is required." }) + Environment.NewLine,
            stderr.ToString());
    }
    finally
    {
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", previousUrl);
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", previousToken);
    }
}

[Fact]
public async Task Non_http_url_reports_invalid_arguments_without_http()
{
    using var client = new HttpClient(new RecordingHandler(_ =>
        throw new InvalidOperationException("Validation failures must not reach HTTP.")));
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await NodeOperatorCommand.RunAsync(
        ["--url", "ftp://127.0.0.1:7312", "--json", "health"],
        client,
        stdout,
        stderr);

    Assert.Equal(2, exitCode);
    Assert.Empty(stdout.ToString());
    // Serialized (not hand-written) because System.Text.Json escapes '<' and '>' to unicode escape sequences.
    Assert.Equal(
        JsonSerializer.Serialize(new { error = "invalid_arguments", message = "--url <http(s)://node> or HARBORLINE_NODE_URL is required." }) + Environment.NewLine,
        stderr.ToString());
}

[Fact]
public async Task Token_without_value_reports_invalid_arguments_without_http()
{
    using var client = new HttpClient(new RecordingHandler(_ =>
        throw new InvalidOperationException("Validation failures must not reach HTTP.")));
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await NodeOperatorCommand.RunAsync(
        ["--url", "http://127.0.0.1:7312", "--json", "health", "--token"],
        client,
        stdout,
        stderr);

    Assert.Equal(2, exitCode);
    Assert.Empty(stdout.ToString());
    Assert.Equal(
        "{\"error\":\"invalid_arguments\",\"message\":\"An option value is missing.\"}" + Environment.NewLine,
        stderr.ToString());
}

[Fact]
public async Task Caller_requested_cancellation_rethrows_without_writing_output()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    using var client = new HttpClient(new RecordingHandler(_ =>
        throw new TaskCanceledException("The caller canceled the request.")));
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    await Assert.ThrowsAsync<TaskCanceledException>(() => NodeOperatorCommand.RunAsync(
        ["--url", "http://127.0.0.1:7312", "--json", "health"],
        client,
        stdout,
        stderr,
        cts.Token));

    Assert.Empty(stdout.ToString());
    Assert.Empty(stderr.ToString());
}

[Fact]
public async Task Request_without_token_leaves_authorization_null()
{
    var previousUrl = Environment.GetEnvironmentVariable("HARBORLINE_NODE_URL");
    var previousToken = Environment.GetEnvironmentVariable("HARBORLINE_NODE_TOKEN");
    Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", null);
    Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", null);
    try
    {
        HttpRequestMessage? observed = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Healthy", Encoding.UTF8, "text/plain"),
            };
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--json", "health"],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Null(observed.Headers.Authorization);
    }
    finally
    {
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_URL", previousUrl);
        Environment.SetEnvironmentVariable("HARBORLINE_NODE_TOKEN", previousToken);
    }
}

[Fact]
public async Task Non_health_json_success_wraps_non_json_body_as_result()
{
    using var client = new HttpClient(new RecordingHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("OK", Encoding.UTF8, "text/plain"),
        }));
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await NodeOperatorCommand.RunAsync(
        ["--url", "http://127.0.0.1:7312", "--json", "tenant", "list"],
        client,
        stdout,
        stderr);

    Assert.Equal(0, exitCode);
    Assert.Equal("{\"result\":\"OK\"}" + Environment.NewLine, stdout.ToString());
    Assert.Empty(stderr.ToString());
}

[Fact]
public async Task Pack_export_http_failure_writes_no_file()
{
    var requestPath = Path.Combine(Path.GetTempPath(), $"harborline-cli-{Guid.NewGuid():N}.json");
    var outPath = Path.Combine(Path.GetTempPath(), $"harborline-cli-{Guid.NewGuid():N}.pack");
    await File.WriteAllTextAsync(requestPath, "{}");
    try
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"export_rejected\"}", Encoding.UTF8, "application/json"),
            }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            ["--url", "http://127.0.0.1:7312", "--json", "pack", "export", "--request", requestPath, "--out", outPath],
            client,
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal("{\"error\":\"export_rejected\"}" + Environment.NewLine, stderr.ToString());
        Assert.False(File.Exists(outPath));
    }
    finally
    {
        File.Delete(requestPath);
        File.Delete(outPath);
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
