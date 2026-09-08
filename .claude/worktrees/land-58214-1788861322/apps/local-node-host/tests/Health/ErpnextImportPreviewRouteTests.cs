using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>Route-level proof for the read-only ERPNext source-inventory preview.</summary>
public sealed class ErpnextImportPreviewRouteTests : IAsyncLifetime
{
    private const string Route = "/api/local-node/import/erpnext/preview";

    private const string FixtureDump = """
        CREATE TABLE `tabAccount` (
          `name` varchar(140) NOT NULL,
          PRIMARY KEY (`name`)
        ) ENGINE=InnoDB;
        INSERT INTO `tabAccount` VALUES ('ACC-0001'), ('ACC-0002');

        CREATE TABLE `tabDocType` (
          `name` varchar(140) NOT NULL,
          PRIMARY KEY (`name`)
        ) ENGINE=InnoDB;
        INSERT INTO `tabDocType` VALUES ('Account');

        CREATE TABLE `tabProperty` (
          `name` varchar(140) NOT NULL,
          PRIMARY KEY (`name`)
        ) ENGINE=InnoDB;
        INSERT INTO `tabProperty` VALUES ('PROP-0001'), ('PROP-0002'), ('PROP-0003');
        """;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private RecordingLoggerProvider _logs = null!;
    private string _tempRoot = null!;
    private string _directory = null!;
    private string _dumpPath = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "harborline-erpnext-preview-" + Guid.NewGuid().ToString("N"));
        _directory = Path.Combine(_tempRoot, "import");
        Directory.CreateDirectory(_directory);
        _dumpPath = Path.Combine(_directory, "erpnext.sql");
        await File.WriteAllTextAsync(_dumpPath, FixtureDump);
        await File.WriteAllTextAsync(Path.Combine(_directory, "write-sentinel.txt"), "unchanged");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _logs = new RecordingLoggerProvider();
        builder.Logging.AddProvider(_logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ErpnextImportPreviewRoutes.ImportRootConfigurationKey] = _directory,
        });
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ErpnextImportPreviewRoutes.Map(_app.MapSelectedSessionProductGroup());
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup; test assertions already completed.
        }
    }

    [Fact]
    [Trait("PlanCard", "2298")]
    public async Task Preview_accounts_for_every_row_without_writing_any_file()
    {
        var beforeFiles = Directory.GetFiles(_directory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var beforeDump = await File.ReadAllTextAsync(_dumpPath);
        var sentinelPath = Path.Combine(_directory, "write-sentinel.txt");
        var beforeSentinel = await File.ReadAllTextAsync(sentinelPath);

        var response = await _client.PostAsJsonAsync(Route, new { dumpFilePath = _dumpPath });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("previewOnly").GetBoolean());
        Assert.Equal("mariaDbDump", body.GetProperty("sourceMode").GetString());
        Assert.Equal(6, body.GetProperty("accountedRowCount").GetInt64());
        Assert.Equal(2, body.GetProperty("mappedRowCount").GetInt64());
        Assert.Equal(1, body.GetProperty("knownIrrelevantRowCount").GetInt64());
        Assert.Equal(3, body.GetProperty("unmappedRowCount").GetInt64());

        var entries = body.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Collection(entries,
            account => AssertEntry(account, "Account", "mapped", 2),
            docType => AssertEntry(docType, "DocType", "knownIrrelevant", 1),
            property => AssertEntry(property, "Property", "unmapped", 3));

        Assert.Equal(beforeFiles, Directory.GetFiles(_directory).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(beforeDump, await File.ReadAllTextAsync(_dumpPath));
        Assert.Equal(beforeSentinel, await File.ReadAllTextAsync(sentinelPath));
        Assert.Contains(
            _logs.Entries,
            entry =>
                entry.Level == LogLevel.Information &&
                entry.Message.Contains(_dumpPath, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing.sql", HttpStatusCode.NotFound, "dump-file-not-found")]
    [InlineData("dump.json", HttpStatusCode.BadRequest, "dump-file-type-unsupported")]
    [Trait("PlanCard", "2298")]
    public async Task Preview_refuses_invalid_source_paths_without_leaking_the_path(
        string requestedPath,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var path = string.IsNullOrEmpty(requestedPath)
            ? requestedPath
            : Path.Combine(_directory, requestedPath);

        var response = await _client.PostAsJsonAsync(Route, new { dumpFilePath = path });

        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.DoesNotContain(_directory, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "2352")]
    public async Task Preview_refuses_a_dump_outside_the_configured_import_root()
    {
        var outsideDirectory = _directory + "-outside";
        Directory.CreateDirectory(outsideDirectory);
        var outsidePath = Path.Combine(outsideDirectory, "escape.sql");
        await File.WriteAllTextAsync(outsidePath, FixtureDump);

        var response = await _client.PostAsJsonAsync(Route, new { dumpFilePath = outsidePath });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dump-path-outside-import-root", body.GetProperty("code").GetString());
        Assert.DoesNotContain(outsidePath, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Message.Contains(outsidePath, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("PlanCard", "2352")]
    public async Task Preview_preserves_the_stable_invalid_path_refusal()
    {
        var response = await _client.PostAsJsonAsync(Route, new { dumpFilePath = "\0.sql" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dump-path-invalid", body.GetProperty("code").GetString());
        Assert.DoesNotContain(_directory, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [Trait("PlanCard", "2352")]
    public async Task Preview_refuses_a_missing_dump_path_without_disclosing_the_import_root(string requestedPath)
    {
        var response = await _client.PostAsJsonAsync(Route, new { dumpFilePath = requestedPath });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dump-path-required", body.GetProperty("code").GetString());
        Assert.DoesNotContain(_directory, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "2352")]
    public async Task Preview_fails_closed_when_the_import_root_is_not_configured()
    {
        _app.Configuration[ErpnextImportPreviewRoutes.ImportRootConfigurationKey] = null;

        var response = await _client.PostAsJsonAsync(Route, new { dumpFilePath = _dumpPath });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dump-import-root-not-configured", body.GetProperty("code").GetString());
        Assert.DoesNotContain(_dumpPath, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static void AssertEntry(JsonElement entry, string docType, string classification, int sourceRowCount)
    {
        Assert.Equal(docType, entry.GetProperty("docType").GetString());
        Assert.Equal(classification, entry.GetProperty("classification").GetString());
        Assert.Equal(sourceRowCount, entry.GetProperty("sourceRowCount").GetInt32());
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
