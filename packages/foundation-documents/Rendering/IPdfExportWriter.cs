namespace Harborline.Api.Foundation.Documents.Rendering;

/// <summary>
/// The last-mile render seam (ADR 0021 §1, promoted to Foundation per this pillar's #111 design §3.1 and
/// council F4): a <b>library-neutral, semantic-document-in / bytes-out</b> contract. The render pipeline
/// resolves an <see cref="IPdfExportWriter"/> without knowing which library backs it — the deployer /
/// composition registers one adapter per format, and the swap is a DI registration (§3.4). The default
/// adapter is <c>Harborline.Api.Documents.PdfSharp</c> (MIT, pure-managed, the JIT desktop node); a mobile carve
/// registers the AOT-clean minimal emitter, same contract, different adapter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why net-new (council F4).</b> The DataGrid draft interface of the same name is <c>internal</c> to the
/// Blazor adapter with only the (rejected) Playwright/HTML impl, and renders a flat column+row grid — not a
/// document tree. This is the semantic-content shape ADR 0021 §1 actually specified: it renders a walked
/// <see cref="RenderedDocument"/> (§3), so it is a consumable Foundation contract + a first PDFsharp impl,
/// which are new work — not a reused shipped seam.
/// </para>
/// <para>
/// <b>One authoritative renderer (§3.2 — "no second renderer").</b> The issued document mints through this
/// path; the editor's live preview renders through this SAME path (a server-side preview served back to the
/// UI), never a divergent client-side renderer. Adapters for the same format must produce
/// observably-equivalent output (ADR 0021 §6) — the observable content is the <see cref="RenderedDocument"/>,
/// which is deterministic where PDF bytes are not (council F3).
/// </para>
/// </remarks>
public interface IPdfExportWriter
{
    /// <summary>Renders a semantic <see cref="RenderedDocument"/> to PDF bytes.</summary>
    /// <param name="document">The walked, merge-resolved, locale-formatted semantic document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rendered PDF bytes — the authoritative artifact once stored (§3.6).</returns>
    ValueTask<byte[]> WriteAsync(RenderedDocument document, CancellationToken ct = default);
}
