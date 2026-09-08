namespace Harborline.Api.UIAdapters.Blazor.Components.Navigation;

public enum ActivityLogLayout
{
    List,
    Table,
}

public sealed record ActivityLogEntry(DateTimeOffset Timestamp, string Actor, string Action, string Target);

public sealed record ActionSheetItem(string Label, bool Disabled = false, bool Destructive = false);

public sealed record BottomNavigationItem(string Label, string Href, string? Icon = null, bool IsActive = false);

public sealed record SideNavItem(string Label, string Href, string? Icon = null, bool IsActive = false);

public sealed record SideNavGroup(string Label, IReadOnlyList<SideNavItem> Items);
