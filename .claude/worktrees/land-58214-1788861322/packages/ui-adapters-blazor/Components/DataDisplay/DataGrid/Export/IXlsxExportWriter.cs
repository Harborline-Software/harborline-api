namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay;

/// <summary>Internal seam for optional XLSX export implementations.</summary>
internal interface IXlsxExportWriter
{
    byte[] Write<TItem>(
        IReadOnlyList<ExportColumnDescriptor> columns,
        IReadOnlyList<TItem> items,
        XlsxExportOptions options);
}
