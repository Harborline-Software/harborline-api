using Harborline.Api.Foundation.Enums;
using Harborline.Api.Foundation.Models;

namespace Harborline.Api.Foundation.Services;

public interface IHarborlineBreakpointService
{
    Breakpoint Current { get; }
    event EventHandler<BreakpointChangedEventArgs>? BreakpointChanged;
    Task InitializeAsync();
}
