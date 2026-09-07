using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Blocks.Docs.Services;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Docs;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local documents surface (<see cref="DocumentRoutes"/>) — the T4
/// documents node-flip (ADR 0127). Hosts the SAME route handlers
/// <see cref="HostedDocumentApiEndpoint"/> registers, on a real in-process Kestrel listener backed by a
/// temp SQLite store, driven by a real <see cref="HttpClient"/>. The docs services are the production
/// composition (node EF repos + the CONCRETE inline-ceiling policy + AttachmentService + DocumentRefService)
/// — no test/prod drift.
/// </summary>
/// <remarks>
/// Exercises: multipart upload (201), list, detail, content (raw inline bytes), idempotent attach (both
/// Attachment AND DocumentRef), and the SEC-1 / 25&#160;MB inline-ceiling rejection (above-ceiling → 413
/// with the actionable message). No CSRF anywhere (loopback node convention).
/// </remarks>
public sealed class DocumentRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    private const string DocsRoute = "/api/local-node/documents";

    // Minimal valid PNG: 8-byte signature is what MimeSniffer keys on for image/png.
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk header
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
    ];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-doc-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "doc-test.db")};Pooling=False";

        // Attachment + DocumentRef are contributed by DocsEntityModule. Register it so
        // LocalNodeDbContext composes the same model the production host does.
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.Docs.Data.DocsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        // Ticket 216: the composition root owns the only clock; the fixture supplies the test one.
        builder.Services.AddTestKernelClock();
        // Production-faithful docs composition (the SAME slice AddNodeDocsWrites registers).
        builder.Services.AddNodeDocsWrites();

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        var docs = (
            _app.Services.GetRequiredService<IAttachmentRepository>(),
            _app.Services.GetRequiredService<IAttachmentService>(),
            _app.Services.GetRequiredService<IDocumentRefService>());
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        DocumentRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            docs,
            NodeTestActiveTeam.Accessor);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string filename, string contentType, string? sensitivity = null)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", filename);
        if (sensitivity is not null) form.Add(new StringContent(sensitivity), "sensitivity");
        return form;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Upload → list → detail → content round-trips a document through local-node.db")]
    public async Task Upload_List_Detail_Content_RoundTrips()
    {
        // Upload.
        var upload = await _client.PostAsync(DocsRoute, BuildUpload(PngBytes, "scan.png", "image/png", "Internal"));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var created = await ReadJson(upload);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("image/png", created.GetProperty("mimeType").GetString());
        Assert.Equal("Live", created.GetProperty("status").GetString()); // Active → Live wire mapping
        Assert.Equal(PngBytes.Length, created.GetProperty("sizeBytes").GetInt64());

        // List (tenant-wide).
        var list = await _client.GetAsync(DocsRoute);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listJson = await ReadJson(list);
        var docs = listJson.GetProperty("documents");
        Assert.Equal(1, docs.GetArrayLength());
        Assert.Equal(id, docs[0].GetProperty("id").GetString());

        // Detail.
        var detail = await _client.GetAsync($"{DocsRoute}/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailJson = await ReadJson(detail);
        Assert.Equal(id, detailJson.GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(detailJson.GetProperty("contentHash").GetString()));

        // Content — the raw inline bytes come back byte-identical.
        var content = await _client.GetAsync($"{DocsRoute}/{id}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);
        var bytes = await content.Content.ReadAsByteArrayAsync();
        Assert.Equal(PngBytes, bytes);
    }

    [Fact(DisplayName = "Attach links Attachment → parent entity (DocumentRef) and is idempotent")]
    public async Task Attach_CreatesDocumentRef_Idempotent()
    {
        var upload = await _client.PostAsync(DocsRoute, BuildUpload(PngBytes, "lease.png", "image/png"));
        var id = (await ReadJson(upload)).GetProperty("id").GetString()!;

        var body = new { clusterCode = "blocks-leases", parentEntityType = "lease", parentEntityId = "LEASE-0001", attachmentRole = "signed-copy" };

        var first = await _client.PostAsJsonAsync($"{DocsRoute}/{id}/attach", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstJson = await ReadJson(first);
        var refId = firstJson.GetProperty("id").GetString();
        Assert.Equal("blocks-leases", firstJson.GetProperty("clusterCode").GetString());
        Assert.Equal("LEASE-0001", firstJson.GetProperty("parentEntityId").GetString());

        // Idempotent: a second identical attach returns the SAME link id.
        var second = await _client.PostAsJsonAsync($"{DocsRoute}/{id}/attach", body);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondJson = await ReadJson(second);
        Assert.Equal(refId, secondJson.GetProperty("id").GetString());
    }

    [Fact(DisplayName = "Attach to an unknown attachment returns opaque 404")]
    public async Task Attach_UnknownAttachment_NotFound()
    {
        var body = new { clusterCode = "blocks-leases", parentEntityType = "lease", parentEntityId = "LEASE-X" };
        var resp = await _client.PostAsJsonAsync($"{DocsRoute}/att_does_not_exist/attach", body);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Detail / content for an unknown id return opaque 404")]
    public async Task Detail_Content_UnknownId_NotFound()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{DocsRoute}/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{DocsRoute}/nope/content")).StatusCode);
    }

    [Fact(DisplayName = "SEC-1 / ADR 0127: an upload above the 25 MB inline ceiling is REJECTED (413) with the actionable message")]
    public async Task Upload_AboveInlineCeiling_Rejected413()
    {
        // 25 MB + 1, with a leading PNG signature so the MIME gate passes (image/png whitelisted) and
        // the INLINE-size gate is the one that fires.
        var oversized = new byte[NodeDocsWriteComposition.InlineCeilingBytes + 1];
        Array.Copy(PngBytes, oversized, PngBytes.Length);

        var resp = await _client.PostAsync(DocsRoute, BuildUpload(oversized, "huge-scan.png", "image/png"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode); // 413
        var json = await ReadJson(resp);
        Assert.Equal(nameof(PolicyRejection.InlineSize), json.GetProperty("reason").GetString());
        // Actionable, PII-free detail (no filename, no tenant id) mentioning the 25 MB limit.
        var detail = json.GetProperty("detail").GetString()!;
        Assert.Contains("25", detail);
        Assert.DoesNotContain("huge-scan", detail);
    }

    [Fact(DisplayName = "An empty / missing file part returns 400")]
    public async Task Upload_NoFile_BadRequest()
    {
        var form = new MultipartFormDataContent { { new StringContent("Internal"), "sensitivity" } };
        var resp = await _client.PostAsync(DocsRoute, form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
