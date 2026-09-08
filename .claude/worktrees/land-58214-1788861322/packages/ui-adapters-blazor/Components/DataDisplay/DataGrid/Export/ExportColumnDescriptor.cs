namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay;

/// <summary>Internal column metadata shared by optional export implementations.</summary>
internal sealed record ExportColumnDescriptor(string? Field, string Title, string? Format);
