namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Inputs;

/// <summary>A node in a <see cref="HarborlineMultiSelectTree"/>.</summary>
public sealed record MultiSelectTreeNode(
    string Value,
    string Text,
    IReadOnlyList<MultiSelectTreeNode>? Items = null,
    bool Disabled = false,
    bool Expanded = true);
