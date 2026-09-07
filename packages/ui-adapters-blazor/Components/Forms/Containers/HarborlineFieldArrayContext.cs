using Microsoft.AspNetCore.Components;

namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Containers;

/// <summary>
/// The operations a <c>HarborlineFieldArray</c> exposes to its child content —
/// the Blazor analogue of the React FieldArray render-prop ops.
/// </summary>
public sealed class HarborlineFieldArrayContext<TItem>
{
    private readonly IList<TItem> _items;
    private readonly Func<Task> _notify;
    private readonly Func<TItem> _factory;

    internal HarborlineFieldArrayContext(IList<TItem> items, Func<TItem> factory, Func<Task> notify)
    {
        _items = items;
        _factory = factory;
        _notify = notify;
    }

    /// <summary>The current rows, in order.</summary>
    public IReadOnlyList<TItem> Items => _items as IReadOnlyList<TItem> ?? _items.ToList();

    /// <summary>Appends a new row created by the item factory.</summary>
    public Task Append() => Append(_factory());

    /// <summary>Appends the given row.</summary>
    public Task Append(TItem item)
    {
        _items.Add(item);
        return _notify();
    }

    /// <summary>Inserts a row at the given index.</summary>
    public Task Insert(int index, TItem item)
    {
        _items.Insert(index, item);
        return _notify();
    }

    /// <summary>Removes the row at the given index.</summary>
    public Task Remove(int index)
    {
        _items.RemoveAt(index);
        return _notify();
    }

    /// <summary>Moves a row from one index to another.</summary>
    public Task Move(int from, int to)
    {
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        return _notify();
    }

    /// <summary>Replaces the row at the given index.</summary>
    public Task Replace(int index, TItem item)
    {
        _items[index] = item;
        return _notify();
    }
}
