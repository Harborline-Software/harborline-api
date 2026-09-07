namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay;

/// <summary>
/// Interface for components that accept child column registrations via CascadingValue.
/// Implemented by HarborlineDataGrid, HarborlineTreeList, etc.
/// </summary>
public interface IColumnHost
{
    /// <summary>Registers a column with this host during OnInitialized.</summary>
    void RegisterColumn(HarborlineColumnBase column);

    /// <summary>Unregisters a column from this host during Dispose.</summary>
    void UnregisterColumn(HarborlineColumnBase column);
}
