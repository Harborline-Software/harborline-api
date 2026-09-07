using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Documents.Rendering;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.DependencyInjection;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.DependencyInjection;
using Harborline.Api.Kernel.Events.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MTW-2 #3380 — the acting member is the actor recorded as the issued document's legal-hold placer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Both document-issuance requests stamped their placer from a per-install constant, so
/// every signed-in member's issued document recorded the SAME actor. A member whose issued-document placer
/// is a constant has not been correctly identified — an ATTRIBUTION failure, which CIC's 2026-07-29 ruling
/// places inside MTW-2 (permissions remain MTW-3; nothing here enforces anything).
/// </para>
/// <para>
/// <b>Why this test bites.</b> It carries the assertion shape of the wave's two-user acceptance E2E
/// (<c>Mtw2TwoUserAcceptanceE2E</c> — two real members act, the recorded actors must be DISTINCT and must
/// not collapse to the single-operator identity) onto this surface, and it reads the DURABLE record: the
/// appended legal-hold entry the issue mint places, resolved by the hold id the route returns. A 200/201 is
/// returned either way, so a status-code assertion would prove nothing. Against the pre-fix constant both
/// members' holds carry the same placer and the distinct-count assertion fails (observed red before the
/// fix, per FLEET-0007).
/// </para>
/// <para>
/// <b>Real vs substituted.</b> REAL: the production <see cref="DocumentTemplateRoutes"/> handlers on a real
/// in-process Kestrel listener, the real <see cref="DocumentIssuanceService"/> mint, the real ADR 0142
/// <see cref="LegalHoldService"/> over an append-only store, the real per-subject field encryption, and the
/// real <c>HttpContext.Features</c> selected-session seam the production authority binds (set here by one
/// test middleware, exactly as <c>NodeInvoiceRouteBoundPrincipalActorStampTests</c> does). SUBSTITUTED: the
/// PDF byte writer (a deterministic stub — the render library is not the seam under test) and the
/// handle → principal materializer, whose real fence/revalidation path is covered by the identity suite.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3380")]
public sealed class NodeDocumentTemplateRouteActingMemberPlacerTests : IAsyncLifetime
{
    /// <summary>Selects which member the binding middleware puts on the request (absent ⇒ no principal bound).</summary>
    private const string ActingMemberHeader = "X-Test-Acting-Member";

    private const string AliceParty = "party:alice-3380";
    private const string BobParty = "party:bob-3380";

    private const string IssueRoute = "/api/local-node/document-templates/issue";
    private const string TemplateKey = "test.invoice.3380";
    private const string TemplateVersion = "1.0.0";

    /// <summary>The install-constant tenant the routes resolve (mirrors the node's active-team projection).</summary>
    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private ILegalHoldStore _holds = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-doc-placer-3380-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "doc-placer.db")};Pooling=False";

        // The same module set the production node composes for the invoice + party read models.
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.People.Foundation.Data.PeopleEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Two REAL invoices — one per acting member, so each mint places its own hold.
        var invoices = new NodeEfInvoiceRepository(factory);
        await invoices.UpsertAsync(LocalTenantId, BuildInvoice(AliceInvoiceId, "INV-2026-07-29-AA-0001"), new Instant(System.TimeProvider.System.GetUtcNow()).Value);
        await invoices.UpsertAsync(LocalTenantId, BuildInvoice(BobInvoiceId, "INV-2026-07-29-AA-0002"), new Instant(System.TimeProvider.System.GetUtcNow()).Value);

        // The real ADR 0142 legal-hold + per-subject-crypto substrate (mirrors the D2 keystone's host).
        var recovery = BuildRecoveryHost();
        _holds = recovery.GetRequiredService<ILegalHoldStore>();

        var registry = new InMemoryDocumentTemplateRegistry();
        registry.Publish(BuildTemplate());

        var issuance = new DocumentIssuanceService(
            new DocumentRenderWalker(TimeProvider.System),
            new StubPdfWriter(),
            new FileSystemBlobStore(Path.Combine(_dir, "blobs")),
            recovery.GetRequiredService<Harborline.Api.Foundation.Recovery.Crypto.ISubjectFieldEncryptor>(),
            new InMemoryIssuedDocumentStore(),
            recovery.GetRequiredService<ILegalHoldService>(), clock: TimeProvider.System);

        // The seam the production selected-session authority uses: a request-scoped principal on
        // HttpContext.Features. Which member (if any) is bound is chosen per request by a header, so ONE
        // host serves two distinct acting members — the two-real-member shape this card is about.
        _app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            if (http.Request.Headers.TryGetValue(ActingMemberHeader, out var party) &&
                !string.IsNullOrWhiteSpace(party))
            {
                http.Features.Set(BoundPrincipal(party!));
            }
            else
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
            }

            await next(http);
        });

        DocumentTemplateRoutes.Map(
            _app.MapSelectedSessionProductGroup(),
            issuance,
            registry,
            new StubPdfWriter(),
            invoices,
            new NodeEfPartyRepository(factory, TimeProvider.System),
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

    [Fact(DisplayName =
        "Document issue: two acting members each record their OWN party as the durable legal-hold placer — never one shared constant")]
    public async Task Issue_ByTwoActingMembers_RecordsDistinctPlacersOnTheDurableHolds()
    {
        var aliceHoldId = await IssueAsAsync(AliceParty, AliceInvoiceId);
        var bobHoldId = await IssueAsAsync(BobParty, BobInvoiceId);

        // Both mints placed a hold, and the two mints are separate durable records (not one reused hold).
        Assert.NotEqual(aliceHoldId, bobHoldId);

        var alicePlacer = await PlacerOfAsync(aliceHoldId);
        var bobPlacer = await PlacerOfAsync(bobHoldId);
        var placers = new[] { alicePlacer.Value, bobPlacer.Value };

        // Scan the WHOLE recorded set for the single-operator identity FIRST, so a collapse to the constant
        // fails here rather than being masked by a narrower per-member comparison downstream.
        Assert.DoesNotContain(ActiveTeamAuthorizationContext.LocalUserId, placers);
        Assert.DoesNotContain(NodeCallerParty.OperatorParty.Value, placers);

        // The positive teeth: each member's OWN party is on their own document's hold. These are asserted
        // as equalities (not merely "not the constant"), so a route that recorded some other non-constant
        // value — or that failed to place a hold at all — cannot pass by a vacuous negative.
        Assert.Equal(AliceParty, alicePlacer.Value);
        Assert.Equal(BobParty, bobPlacer.Value);
        Assert.Equal(2, placers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName =
        "Document issue: an UNBOUND request still records the ruled single-operator fallback — the fix must not attribute someone else")]
    public async Task Issue_WithNoBoundPrincipal_RecordsTheOperatorFallback()
    {
        var holdId = await IssueAsAsync(actingMember: null, invoiceId: AliceInvoiceId);
        var placer = await PlacerOfAsync(holdId);

        Assert.Equal(NodeCallerParty.OperatorParty.Value, placer.Value);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Issues <paramref name="invoiceId"/> as <paramref name="actingMember"/> and returns the placed hold id.</summary>
    private async Task<LegalHoldId> IssueAsAsync(string? actingMember, string invoiceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, IssueRoute)
        {
            Content = JsonContent.Create(new
            {
                templateKey = TemplateKey,
                templateVersion = TemplateVersion,
                invoiceId,
            }),
        };
        if (actingMember is not null)
        {
            request.Headers.Add(ActingMemberHeader, actingMember);
        }

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IssuedDocumentResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Data.LegalHoldId), "the mint must place a retention hold");
        return new LegalHoldId(body.Data.LegalHoldId!);
    }

    /// <summary>Reads the DURABLE hold back from the append-only store and returns its recorded placer.</summary>
    private async Task<ActorId> PlacerOfAsync(LegalHoldId holdId)
    {
        var entry = await _holds.FindHoldAsync(LocalTenantId, holdId);
        Assert.NotNull(entry);
        return entry!.PlacedBy;
    }

    private static SelectedSessionRequestPrincipal BoundPrincipal(string canonicalParty) =>
        new(
            accountId: "account-" + canonicalParty,
            tenantId: LocalTenantId,
            principalUserId: new PrincipalUserId("principal-" + canonicalParty),
            canonicalParty: new CanonicalPartyReference(canonicalParty),
            membershipId: "membership-" + canonicalParty,
            membershipOwnerVersion: 1,
            pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-" + canonicalParty, 1)],
            authorizationEpoch: 1,
            sessionCorrelationId: "session-" + canonicalParty,
            coordinationCorrelationId: "coordination-" + canonicalParty);

    private const string AliceInvoiceId = "inv-3380-alice";
    private const string BobInvoiceId = "inv-3380-bob";

    private static Invoice BuildInvoice(string id, string number)
    {
        var invoiceId = new InvoiceId(id);
        var lines = new List<InvoiceLine>
        {
            new()
            {
                Id = InvoiceLineId.NewId(), InvoiceId = invoiceId, LineNumber = 1,
                Description = "Consulting", Quantity = 1m, UnitPrice = 100.00m, Amount = 100.00m,
                IncomeAccountId = new GLAccountId("4000"),
            },
        };

        var invoice = Invoice.Create(
            tenantId: LocalTenantId,
            chartId: new ChartOfAccountsId("chart-3380"),
            invoiceNumber: number,
            customerId: new PartyId("party:customer-3380"),
            issueDate: new DateOnly(2026, 7, 29),
            dueDate: new DateOnly(2026, 8, 28),
            lines: lines,
            arAccountId: new GLAccountId("1100"),
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            notes: "acting-member placer proof",
            id: invoiceId);

        return invoice with { Total = 100.00m };
    }

    /// <summary>A minimal published template — this card is about the recorded actor, not the render grammar.</summary>
    private static TemplateDefinition BuildTemplate() => new(
        Key: TemplateKey,
        Version: TemplateVersion,
        DocumentType: "invoice",
        RecordType: new RecordTypeBinding("invoice", "1"),
        Locale: DocumentLocalePolicy.Fixed("en-US"),
        Style: new DocumentStyleRef(BrandName: "Harborline Software"),
        Structure: new List<DocumentBlock>
        {
            new()
            {
                Kind = DocumentBlockKind.Header,
                Lines = new List<DocumentLine>
                {
                    new(new[]
                    {
                        DocumentInline.OfLiteral("Invoice "),
                        DocumentInline.OfMerge("field.invoiceNumber"),
                    }),
                },
            },
        });

    /// <summary>The real ADR 0142 legal-hold + recovery-crypto host (mirrors the D2 keystone's composition).</summary>
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

    /// <summary>Deterministic byte writer — the PDF library is not the seam under test.</summary>
    private sealed class StubPdfWriter : IPdfExportWriter
    {
        public ValueTask<byte[]> WriteAsync(RenderedDocument document, CancellationToken ct = default) =>
            ValueTask.FromResult("%PDF-1.7 stub"u8.ToArray());
    }
}
