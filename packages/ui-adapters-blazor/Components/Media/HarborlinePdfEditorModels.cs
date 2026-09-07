namespace Harborline.Api.UIAdapters.Blazor.Components.Media;

/// <summary>Toolbar tools supported by the PDF editor shell.</summary>
public enum PdfEditorTool
{
    Select,
    Highlight,
    Draw,
    Text,
    Erase,
}

/// <summary>A host-owned annotation descriptor.</summary>
public sealed record PdfEditorAnnotation(string Id, int Page, PdfEditorTool Tool, string Text = "");
