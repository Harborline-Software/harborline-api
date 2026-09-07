namespace Harborline.Api.Foundation.Services;

public interface IHarborlineOverlayService
{
    event EventHandler? OverlayRequested;
    event EventHandler? OverlayDismissed;
    void RequestOverlay();
    void DismissOverlay();
}
