namespace Harborline.Api.UIAdapters.Blazor.Components.Navigation;

/// <summary>A single result row for <c>HarborlineGlobalSearch</c>.</summary>
public sealed class GlobalSearchResult
{
    /// <summary>Stable unique id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Primary label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional secondary text.</summary>
    public string? SubLabel { get; set; }

    /// <summary>Optional category the result is grouped under.</summary>
    public string? Category { get; set; }
}

/// <summary>A top-level trigger in <c>HarborlineMegaMenu</c> that reveals a multi-column panel.</summary>
public sealed class MegaMenuTrigger
{
    /// <summary>Stable unique id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Visible trigger label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The link columns shown in the panel.</summary>
    public IReadOnlyList<MegaMenuColumn> Columns { get; set; } = [];
}

/// <summary>A titled column of links in a mega-menu panel.</summary>
public sealed class MegaMenuColumn
{
    /// <summary>Column title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Links listed in this column.</summary>
    public IReadOnlyList<MegaMenuLink> Links { get; set; } = [];
}

/// <summary>A single link in a mega-menu column.</summary>
public sealed class MegaMenuLink
{
    /// <summary>Visible link label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional secondary description.</summary>
    public string? Description { get; set; }

    /// <summary>Optional badge text (for example "New").</summary>
    public string? Badge { get; set; }

    /// <summary>Optional navigation target; rendered as an anchor when set.</summary>
    public string? Href { get; set; }
}

/// <summary>Kinds of <c>HarborlineMenubar</c> entries.</summary>
public enum MenubarItemType
{
    /// <summary>A plain command item.</summary>
    Command,

    /// <summary>A checkable item rendered as <c>menuitemcheckbox</c>.</summary>
    Checkbox,

    /// <summary>A radio-group member rendered as <c>menuitemradio</c>.</summary>
    Radio,

    /// <summary>A visual separator.</summary>
    Separator,

    /// <summary>An item that opens a nested sub-menu.</summary>
    Sub,
}

/// <summary>A menubar menu or menu entry for <c>HarborlineMenubar</c>.</summary>
public sealed class MenubarItem
{
    /// <summary>Stable unique id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Visible label. Ignored for separators.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The entry kind.</summary>
    public MenubarItemType Type { get; set; } = MenubarItemType.Command;

    /// <summary>Whether a checkbox or radio entry is checked.</summary>
    public bool Checked { get; set; }

    /// <summary>Whether the entry is disabled.</summary>
    public bool Disabled { get; set; }

    /// <summary>Radio-group discriminator for radio entries.</summary>
    public string? GroupId { get; set; }

    /// <summary>Child entries for top-level menus and <see cref="MenubarItemType.Sub"/> entries.</summary>
    public IReadOnlyList<MenubarItem> Items { get; set; } = [];
}

/// <summary>A single entry of <c>HarborlineNavigationMenu</c>.</summary>
public sealed class NavigationMenuItem
{
    /// <summary>Stable unique id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Visible label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Navigation target; rendered as a plain link when no children exist.</summary>
    public string? Href { get; set; }

    /// <summary>Child links revealed in a dropdown panel.</summary>
    public IReadOnlyList<NavigationMenuItem> Children { get; set; } = [];

    /// <summary>Optional secondary description (used inside dropdown panels).</summary>
    public string? Description { get; set; }
}
