using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Documents.Rendering;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.DependencyInjection;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit.DependencyInjection;
using Harborline.Api.Kernel.Events.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Blocks.BuilderDefinitions;

using Xunit;

using DefinitionContractVersion = Harborline.Foundation.Definitions.DefinitionContractVersion;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-572 slice 5 (DES-0029 kernel-core-ck-8; DES-0006 §10; T-724 ruling 87): rendering a stored template
/// whose envelope contract is outside the platform seed's window is a 409 refusal at stage <c>render</c>,
/// reporting the window in DES-0006 §1's <c>contract</c> shape. The seed window is app contract 1.0 over
/// majors [1, 1] (T-572 owner ruling Q5), so the rows are both window edges. Real: the production
/// <see cref="DocumentTemplateRoutes"/> on Kestrel, the real render walker and issuance service. Substituted:
/// the PDF byte writer.
/// </summary>
public sealed class DocumentTemplateRenderContractWindowTests : IAsyncLifetime
{
    private const string RenderRoute = "/api/local-node/document-templates/render";
    private const string TemplateVersion = "1.0.0";
    private const string OutOfWindow = "definition.contract.out_of_window";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-render-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "render-contract.db")};Pooling=False";
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.People.Foundation.Data.PeopleEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        _app = builder.Build();
        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();

        var registry = new InMemoryDocumentTemplateRegistry();
        foreach (var (major, minor) in new[] { (0, 0), (0, 9), (1, 0), (1, 1), (2, 0) })
        {
            registry.Publish(Template(KeyFor(major, minor), new DefinitionContractVersion(major, minor)));
        }

        registry.Publish(Template(UndeclaredKey, contract: null));

        var recovery = BuildRecoveryHost();
        var issuance = new DocumentIssuanceService(
            new DocumentRenderWalker(TimeProvider.System),
            new StubPdfWriter(),
            new FileSystemBlobStore(Path.Combine(_dir, "blobs")),
            recovery.GetRequiredService<Harborline.Api.Foundation.Recovery.Crypto.ISubjectFieldEncryptor>(),
            new InMemoryIssuedDocumentStore(),
            recovery.GetRequiredService<ILegalHoldService>(), clock: TimeProvider.System);

        _app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        DocumentTemplateRoutes.Map(
            _app.MapSelectedSessionProductGroup(),
            issuance,
            registry,
            new StubPdfWriter(),
            new NodeEfInvoiceRepository(factory),
            new Harborline.Api.LocalNodeHost.Data.People.NodeEfPartyRepository(factory, TimeProvider.System),
            NodeTestActiveTeam.Accessor,
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Theory(DisplayName = "T-572 S5: render refuses a stored template whose contract is outside the window with 409 at stage render")]
    [Trait("Holds", "kernel-core-ck-8")]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 9)]
    public async Task Render_refuses_a_stored_template_whose_contract_is_outside_the_window(int major, int minor)
    {
        using var response = await _client.PostAsJsonAsync(RenderRoute,
            new { templateKey = KeyFor(major, minor), templateVersion = TemplateVersion });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("refused").GetBoolean());
        Assert.Equal("render", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(OutOfWindow, refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal($"{KeyFor(major, minor)}@{TemplateVersion}", refusal.GetProperty("target").GetString());

        var window = PlatformPackageSeed.ContractWindow;
        var contract = body.GetProperty("contract");
        Assert.Equal(window.Major, contract.GetProperty("app_major").GetInt32());
        Assert.Equal(window.Minor, contract.GetProperty("app_minor").GetInt32());
        Assert.Equal([window.OldestMajor, window.Major],
            contract.GetProperty("window").EnumerateArray().Select(edge => edge.GetInt32()));
    }

    [Fact(DisplayName = "T-572 S5: render admits a stored template declaring the current app contract, the window's newest edge")]
    [Trait("Holds", "kernel-core-ck-8")]
    public async Task Render_admits_a_stored_template_declaring_the_current_app_contract()
    {
        var window = PlatformPackageSeed.ContractWindow;

        using var response = await _client.PostAsJsonAsync(RenderRoute,
            new { templateKey = KeyFor(window.Major, window.Minor), templateVersion = TemplateVersion });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Pins today's interim behaviour: a stored template with no <c>envelope.contract</c> still renders. This is a
    /// staged rollout, not a tolerated-missing policy (T-572 Q4 and Q6 keep "no grace period"), and the
    /// missing-declaration half of ck-8 is not Holds until the follow-up lands. That follow-up refuses
    /// <c>definition.contract.missing</c> at install and render once the authoring surfaces stamp the
    /// contract and the shipped templates declare <c>{major: 1, minor: 0}</c>, and it deletes this test.
    /// </summary>
    [Fact(DisplayName = "T-572 S5: interim, render of a stored template with no declared contract still succeeds (deleted by the definition.contract.missing follow-up)")]
    public async Task Interim_render_admits_a_template_with_no_declared_contract()
    {
        using var response = await _client.PostAsJsonAsync(RenderRoute,
            new { templateKey = UndeclaredKey, templateVersion = TemplateVersion });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private const string UndeclaredKey = "test.contract.undeclared";

    private static string KeyFor(int major, int minor) => $"test.contract.{major}.{minor}";

    private static TemplateDefinition Template(string key, DefinitionContractVersion? contract) => new(
        Envelope: new DefinitionEnvelope<string, string, TenantId, string?>(
            key,
            TemplateVersion,
            TenantId.System,
            CascadeLayer.Pack,
            Provenance: null,
            Array.Empty<DefinitionRequirement>(),
            Contract: contract),
        DocumentType: "invoice",
        RecordType: new RecordTypeBinding("invoice", "1"),
        Locale: DocumentLocalePolicy.Fixed("en-US"),
        Style: new DocumentStyleRef(BrandName: "Harborline Software"),
        Structure:
        [
            new()
            {
                Kind = DocumentBlockKind.Header,
                Lines = new List<DocumentLine> { new(new[] { DocumentInline.OfLiteral("Invoice") }) },
            },
        ]);

    private static ServiceProvider BuildRecoveryHost()
    {
        var services = new ServiceCollection();
        services.UseInMemoryEventLog();
        services.AddSingleton<IOperationVerifier, Ed25519Verifier>();
        services.AddSingleton<IOperationSigner>(new Ed25519Signer(KeyPair.Generate()));
        services.AddSingleton<IRecoveryClock>(new SystemRecoveryClock(TimeProvider.System));
        services.AddHarborlineKernelAudit();
        services.AddInMemoryTenantKeyProvider();
        services.AddHarborlineRecoveryCoordinator();
        services.AddHarborlineLegalHold();
        return services.BuildServiceProvider();
    }

    private sealed class StubPdfWriter : IPdfExportWriter
    {
        public ValueTask<byte[]> WriteAsync(RenderedDocument document, CancellationToken ct = default)
            => ValueTask.FromResult("%PDF-1.7 stub"u8.ToArray());
    }
}
