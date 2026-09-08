using Harborline.Api.Foundation.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

/// <summary>
/// Registers shared interop services on the <see cref="HarborlineBuilder"/>.
/// </summary>
public static class InteropServiceExtensions
{
    /// <summary>
    /// Adds the shared JS interop infrastructure used by complex Harborline components
    /// (Window, Popover, Splitter, DataGrid, Chart, Editor, etc.).
    /// Call after <c>AddHarborline()</c>.
    /// </summary>
    public static HarborlineBuilder AddHarborlineInteropServices(this HarborlineBuilder builder)
    {
        var services = builder.Services;

        // Module loader (scoped — one per circuit/connection)
        services.AddScoped<IHarborlineJsModuleLoader, HarborlineJsModuleLoader>();

        // Measurement & observation
        services.AddScoped<IElementMeasurementService, ElementMeasurementService>();
        services.AddScoped<IResizeObserverService, ResizeObserverService>();
        services.AddScoped<IIntersectionObserverService, IntersectionObserverService>();

        // Positioning
        services.AddScoped<IPopupPositionService, PopupPositionService>();

        // Drag & resize interactions
        services.AddScoped<IDragService, DragService>();
        services.AddScoped<IResizeInteractionService, ResizeInteractionService>();

        // Clipboard & download
        services.AddScoped<IClipboardService, ClipboardService>();
        services.AddScoped<IDownloadService, DownloadService>();

        // Graphics (charts, diagrams, maps)
        services.AddScoped<IGraphicsInteropService, GraphicsInteropService>();

        // Drop zones (FileUpload, Upload)
        services.AddScoped<IDropZoneService, DropZoneService>();

        return builder;
    }
}
