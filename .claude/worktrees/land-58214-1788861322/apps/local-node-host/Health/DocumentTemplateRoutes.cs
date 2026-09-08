using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Documents.Merge;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Documents.Rendering;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Documents;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The Documents pillar D3 render/issue surface (#111 §6 D3 — "the render/issue HTTP endpoint... a thin
/// accessor-closure wrapping <see cref="DocumentIssuanceService"/>"). Two routes, one authoritative
/// pipeline (§3.2 — "no second renderer"):
/// <list type="bullet">
///   <item><c>POST /api/local-node/document-templates/render</c> — NON-MINTING preview (F9): the editor's
///     "Preview" calls <see cref="DocumentIssuanceService.Render"/> against either a real invoice (
///     <c>invoiceId</c>) or the labelled SAMPLE fixture (<see cref="DocumentMergeMapping.BuildSampleInvoice"/>
///     — honest interim until #127 starter-sample-data lands). Returns <c>application/pdf</c> bytes. Mints
///     nothing; writes nothing.</item>
///   <item><c>POST /api/local-node/document-templates/issue</c> — CP mint: requires a REAL <c>invoiceId</c>
///     (never the sample), resolves/accepts a template, and calls
///     <see cref="DocumentIssuanceService.IssueAsync"/> to mint the immutable
///     <see cref="IssuedDocumentRecord"/> (§3.6). Never Pilot-callable by design (§5.6) — this is a
///     human-committed HTTP action the Harborline App's publish/issue guard fires.</item>
/// </list>
/// A template is resolved two ways: <c>templateKey</c> (+ optional <c>templateVersion</c>) against the
/// published <see cref="IDocumentTemplateRegistry"/> (the pack-shipped General invoice template), OR an
/// inline <c>template</c> body parsed via the SAME pinned canonical-JSON contract the pack projector uses
/// (<see cref="PackTemplateContent.TryParse"/>) — this is what lets the editor preview an UNPUBLISHED
/// draft through the real pipeline (design §2.2/§3.2) without a second parser or a second DSL.
/// </summary>
/// <remarks>
/// Wiring: the render pipeline (<see cref="DocumentIssuanceService"/> + <see cref="IDocumentTemplateRegistry"/>)
/// and the invoice/party repositories are resolved from the composition root and closed over here.
/// <para>
/// Attribution (MTW-2 #3380): the actor recorded as the issued document's legal-hold placer is the
/// request's acting member, resolved through <see cref="NodeCallerParty.Resolve"/> from the
/// selected-session principal on <c>HttpContext.Features</c>. It falls back to the single-operator
/// identity only when no principal is bound (the bootstrap/desktop path), so two signed-in members
/// no longer share one recorded placer.
/// </para>
/// </remarks>
public static class DocumentTemplateRoutes
{
    /// <summary>Canonical route base for the document-templates render/issue surface.</summary>
    public const string RouteBase = "/api/local-node/document-templates";

    public static void Map(
        IEndpointRouteBuilder app,
        DocumentIssuanceService issuance,
        IDocumentTemplateRegistry registry,
        IPdfExportWriter writer,
        NodeEfInvoiceRepository invoices,
        NodeEfPartyRepository parties,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(parties);

        MapRender(app, issuance, registry, writer, invoices, parties, activeTeam, timeProvider);
        MapIssue(app, issuance, registry, invoices, parties, activeTeam, timeProvider);
    }

    // ── POST /api/local-node/document-templates/render — non-minting preview (F9) ──────────────────────
    private static void MapRender(
        IEndpointRouteBuilder app,
        DocumentIssuanceService issuance,
        IDocumentTemplateRegistry registry,
        IPdfExportWriter writer,
        NodeEfInvoiceRepository invoices,
        NodeEfPartyRepository parties,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/render", async (RenderTemplateRequest body, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            if (body is null)
            {
                return Results.BadRequest(new { error = "request_null" });
            }

            if (!TryResolveTemplate(registry, body.TemplateKey, body.TemplateVersion, body.Template, out var template, out var templateError))
            {
                return Results.BadRequest(new { error = templateError });
            }

            Invoice invoice;
            string customerName;
            string sampleSource;

            if (!string.IsNullOrWhiteSpace(body.InvoiceId))
            {
                var tenantId = NodeTenant.Resolve(activeTeam);
                var found = await invoices.GetAsync(tenantId, new InvoiceId(body.InvoiceId!), admittedAt, ct).ConfigureAwait(false);
                if (found is null)
                {
                    return Results.NotFound();
                }

                invoice = found;
                var party = await parties.GetByIdAsync(invoice.CustomerId, ct).ConfigureAwait(false);
                customerName = party?.DisplayName ?? invoice.CustomerId.Value;
                sampleSource = "real";
            }
            else
            {
                // Honest-interim sample (design §6 — ties #127 starter sample data, a named follow-up).
                (invoice, customerName) = DocumentMergeMapping.BuildSampleInvoice();
                sampleSource = "sample";
            }

            var model = DocumentMergeMapping.MapInvoiceToMergeModel(invoice, customerName);
            var format = new DocumentFormatContext(ResolveLocaleTag(template.Locale), invoice.Currency);

            var request = new DocumentIssuanceRequest
            {
                Template = template,
                Model = model,
                Format = format,
                Tenant = NodeTenant.Resolve(activeTeam),
                Subject = new Harborline.Api.Foundation.Recovery.Erasure.SubjectId(invoice.CustomerId.Value),
                RecordType = "invoice",
                RecordId = invoice.Id.Value,
                RecordNumber = invoice.InvoiceNumber,
                // The acting member, not a per-install constant (MTW-2 #3380). Render is non-minting, so
                // this value places nothing; it is resolved the same way as the issue path so the two
                // requests carry identical provenance inputs and cannot drift apart.
                PlacedBy = new ActorId(NodeCallerParty.Resolve(http).Value),
            };

            var rendered = issuance.Render(request);
            var bytes = await writer.WriteAsync(rendered, ct).ConfigureAwait(false);

            return Results.File(bytes, "application/pdf", enableRangeProcessing: false)
                .WithPreviewSourceHeader(sampleSource);
        });
    }

    // ── POST /api/local-node/document-templates/issue — CP mint (§3.6/§5.6, never Pilot) ───────────────
    private static void MapIssue(
        IEndpointRouteBuilder app,
        DocumentIssuanceService issuance,
        IDocumentTemplateRegistry registry,
        NodeEfInvoiceRepository invoices,
        NodeEfPartyRepository parties,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/issue", async (IssueTemplateRequest body, HttpContext http, CancellationToken ct) =>
        {
            var admittedAt = timeProvider.GetUtcNow();
            if (body is null || string.IsNullOrWhiteSpace(body.InvoiceId))
            {
                return Results.BadRequest(new { error = "invoice_id_required" });
            }

            if (!TryResolveTemplate(registry, body.TemplateKey, body.TemplateVersion, body.Template, out var template, out var templateError))
            {
                return Results.BadRequest(new { error = templateError });
            }

            var tenantId = NodeTenant.Resolve(activeTeam);
            var invoice = await invoices.GetAsync(tenantId, new InvoiceId(body.InvoiceId!), admittedAt, ct).ConfigureAwait(false);
            if (invoice is null)
            {
                return Results.NotFound();
            }

            var party = await parties.GetByIdAsync(invoice.CustomerId, ct).ConfigureAwait(false);
            var customerName = party?.DisplayName ?? invoice.CustomerId.Value;
            var model = DocumentMergeMapping.MapInvoiceToMergeModel(invoice, customerName);
            var format = new DocumentFormatContext(ResolveLocaleTag(template.Locale), invoice.Currency);

            var request = new DocumentIssuanceRequest
            {
                Template = template,
                Model = model,
                Format = format,
                Tenant = tenantId,
                Subject = new Harborline.Api.Foundation.Recovery.Erasure.SubjectId(invoice.CustomerId.Value),
                RecordType = "invoice",
                RecordId = invoice.Id.Value,
                RecordNumber = invoice.InvoiceNumber,
                // The acting member is the recorded legal-hold placer on the minted document (MTW-2 #3380).
                PlacedBy = new ActorId(NodeCallerParty.Resolve(http).Value),
                SubjectSnapshot = new Dictionary<string, string> { ["customer.name"] = customerName },
                // Record-scoped (not Subject-scoped): holds THIS issued document retained without
                // extending to a blanket future-erasure block on the whole subject — the wider Subject
                // scope is the counsel-adjacent jurisdictional policy call (design §4.1 flag), out of
                // this route's scope to assume.
                LegalHoldScope = DocumentLegalHoldScope.Record,
            };

            IssuedDocumentRecord issued;
            try
            {
                issued = await issuance.IssueAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Created(
                $"{RouteBase}/issued/{issued.DocumentId}",
                new IssuedDocumentResponse(IssuedDocumentWire.From(issued)));
        });
    }

    /// <summary>
    /// Resolves the template either from the inline canonical-JSON <paramref name="inlineTemplateJson"/>
    /// (the editor's current draft — parsed via the SAME pinned contract the pack projector uses, §3.2 "no
    /// second DSL") or from the published <see cref="IDocumentTemplateRegistry"/> by
    /// <paramref name="templateKey"/> (+ optional <paramref name="templateVersion"/>).
    /// </summary>
    private static bool TryResolveTemplate(
        IDocumentTemplateRegistry registry,
        string? templateKey,
        string? templateVersion,
        string? inlineTemplateJson,
        out TemplateDefinition template,
        out string error)
    {
        template = null!;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(inlineTemplateJson))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(inlineTemplateJson);
            }
            catch (System.Text.Json.JsonException)
            {
                error = "template_malformed_json";
                return false;
            }

            if (!PackTemplateContent.TryParse(node, out template, out var parseError))
            {
                error = $"template_malformed: {parseError}";
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(templateKey))
        {
            error = "template_or_template_key_required";
            return false;
        }

        var resolved = string.IsNullOrWhiteSpace(templateVersion)
            ? registry.Resolve(templateKey)
            : registry.Resolve(templateKey, templateVersion!);

        if (resolved is null)
        {
            error = "template_not_found";
            return false;
        }

        template = resolved;
        return true;
    }

    /// <summary>
    /// Resolves the DOCUMENT's render locale (§1.5) from its policy. <c>Fixed</c> is exact; <c>FromRecord</c>
    /// / <c>FromInstance</c> fall back to <c>en-US</c> today — the customer/org preferred-locale lookups are
    /// a named follow-up (no recipient-locale or org-settings seam is wired at the node yet), never
    /// invented here. This mirrors the D1/D2 keystone's own scope (Fixed("en-US") only).
    /// </summary>
    private static string ResolveLocaleTag(DocumentLocalePolicy locale) => locale.Kind switch
    {
        LocalePolicyKind.Fixed => locale.Tag ?? "en-US",
        _ => "en-US",
    };
}

internal static class ResultsExtensions
{
    /// <summary>Stamps the honest sample-vs-real preview-source signal on a render response (never silent).</summary>
    public static IResult WithPreviewSourceHeader(this IResult result, string source) =>
        new PreviewSourceResult(result, source);

    private sealed class PreviewSourceResult(IResult inner, string source) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["X-Documents-Preview-Source"] = source;
            return inner.ExecuteAsync(httpContext);
        }
    }
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>POST body for <c>/document-templates/render</c>.</summary>
public sealed record RenderTemplateRequest(
    [property: JsonPropertyName("templateKey")] string? TemplateKey,
    [property: JsonPropertyName("templateVersion")] string? TemplateVersion,
    [property: JsonPropertyName("template")] string? Template,
    [property: JsonPropertyName("invoiceId")] string? InvoiceId);

/// <summary>POST body for <c>/document-templates/issue</c>.</summary>
public sealed record IssueTemplateRequest(
    [property: JsonPropertyName("templateKey")] string? TemplateKey,
    [property: JsonPropertyName("templateVersion")] string? TemplateVersion,
    [property: JsonPropertyName("template")] string? Template,
    [property: JsonPropertyName("invoiceId")] string InvoiceId);

/// <summary>The minted issued-document wire projection (never carries the raw snapshot plaintext — §5.5).</summary>
public sealed record IssuedDocumentWire(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("documentType")] string DocumentType,
    [property: JsonPropertyName("recordType")] string RecordType,
    [property: JsonPropertyName("recordId")] string RecordId,
    [property: JsonPropertyName("recordNumber")] string RecordNumber,
    [property: JsonPropertyName("templateKey")] string TemplateKey,
    [property: JsonPropertyName("templateVersion")] string TemplateVersion,
    [property: JsonPropertyName("localeTag")] string LocaleTag,
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("legalHoldId")] string? LegalHoldId)
{
    public static IssuedDocumentWire From(IssuedDocumentRecord r) => new(
        DocumentId: r.DocumentId,
        DocumentType: r.DocumentType,
        RecordType: r.RecordType,
        RecordId: r.RecordId,
        RecordNumber: r.RecordNumber,
        TemplateKey: r.TemplateKey,
        TemplateVersion: r.TemplateVersion,
        LocaleTag: r.LocaleTag,
        ContentHash: r.ContentHash,
        IssuedAtUtc: r.IssuedAtUtc,
        LegalHoldId: r.LegalHoldId);
}

/// <summary>Single-item response envelope for issue.</summary>
public sealed record IssuedDocumentResponse(
    [property: JsonPropertyName("data")] IssuedDocumentWire Data);
