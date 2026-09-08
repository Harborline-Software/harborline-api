namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay;

/// <summary>
/// Interface for PivotGrid components that accept child field registrations via CascadingValue.
/// </summary>
public interface IPivotGridFieldHost
{
    /// <summary>Registers a row field with this host during OnInitialized.</summary>
    void RegisterRowField(HarborlinePivotGridRowField field);

    /// <summary>Unregisters a row field from this host during Dispose.</summary>
    void UnregisterRowField(HarborlinePivotGridRowField field);

    /// <summary>Registers a column field with this host during OnInitialized.</summary>
    void RegisterColumnField(HarborlinePivotGridColumnField field);

    /// <summary>Unregisters a column field from this host during Dispose.</summary>
    void UnregisterColumnField(HarborlinePivotGridColumnField field);

    /// <summary>Registers a measure field with this host during OnInitialized.</summary>
    void RegisterMeasureField(HarborlinePivotGridMeasureField field);

    /// <summary>Unregisters a measure field from this host during Dispose.</summary>
    void UnregisterMeasureField(HarborlinePivotGridMeasureField field);
}
