using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Documents.Rendering;

namespace Harborline.Api.Documents.PdfSharp;

/// <summary>
/// DI registration for the PDFsharp document render adapter (ADR 0021 §4 — <c>services.AddXxxExport()</c>).
/// The deployer/composition registers one adapter per format; the render pipeline resolves
/// <see cref="IPdfExportWriter"/> without knowing which library backs it (§3.4).
/// </summary>
public static class PdfSharpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PdfSharpDocumentWriter"/> as the <see cref="IPdfExportWriter"/> implementation
    /// (the desktop / dogfood JIT node default). Idempotent (<c>TryAddSingleton</c>) — a host that has
    /// registered a different adapter (e.g. a mobile AOT emitter) wins.
    /// </summary>
    public static IServiceCollection AddPdfSharpDocumentRenderer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPdfExportWriter, PdfSharpDocumentWriter>();
        return services;
    }
}
