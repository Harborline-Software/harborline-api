using Microsoft.AspNetCore.Components.Web;
using Harborline.Api.Foundation.Models;

namespace Harborline.Api.UIAdapters.Blazor.Internal;

/// <summary>
/// Converts Blazor event args types into their framework-agnostic Harborline equivalents.
/// Foundation defines the agnostic shapes (e.g., <see cref="HarborlineMouseEventArgs"/>);
/// Blazor adapter code calls these extensions at the boundary where Blazor events
/// leak into framework-neutral event handlers.
/// </summary>
public static class BlazorEventArgsExtensions
{
    public static HarborlineMouseEventArgs ToHarborline(this MouseEventArgs args) => new()
    {
        ClientX = args.ClientX,
        ClientY = args.ClientY,
        ScreenX = args.ScreenX,
        ScreenY = args.ScreenY,
        OffsetX = args.OffsetX,
        OffsetY = args.OffsetY,
        AltKey = args.AltKey,
        CtrlKey = args.CtrlKey,
        ShiftKey = args.ShiftKey,
        MetaKey = args.MetaKey,
        Button = (int)args.Button,
    };
}
