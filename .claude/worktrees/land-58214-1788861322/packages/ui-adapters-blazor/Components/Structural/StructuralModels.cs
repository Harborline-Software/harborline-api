namespace Harborline.Api.UIAdapters.Blazor.Components.Structural;

public enum HarborlineSortDirection
{
    Ascending,
    Descending
}

public sealed record HarborlineSortState(string Field, HarborlineSortDirection Direction);

public sealed record HarborlineSortOption(string Value, string Label);

public sealed record HarborlineColumnVisibilityOption(string Id, string Label, bool Required = false, bool Visible = true);

public sealed record HarborlineSavedView(string Name, HarborlineSavedViewState State);

public sealed class HarborlineSavedViewState
{
    public IReadOnlyDictionary<string, object?> Filters { get; init; } = new Dictionary<string, object?>();
    public HarborlineSortState? Sort { get; init; }
    public IReadOnlyDictionary<string, bool> Columns { get; init; } = new Dictionary<string, bool>();

    public string Summary =>
        $"{Filters.Count} filter{(Filters.Count == 1 ? "" : "s")}, " +
        $"{Columns.Count(c => !c.Value)} hidden column{(Columns.Count(c => !c.Value) == 1 ? "" : "s")}";
}
