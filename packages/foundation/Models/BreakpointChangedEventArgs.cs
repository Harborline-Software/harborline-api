using Harborline.Api.Foundation.Enums;

namespace Harborline.Api.Foundation.Models;

public class BreakpointChangedEventArgs : EventArgs
{
    public required Breakpoint OldBreakpoint { get; init; }
    public required Breakpoint NewBreakpoint { get; init; }
}
