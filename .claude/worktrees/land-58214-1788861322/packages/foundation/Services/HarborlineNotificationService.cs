using Harborline.Api.Foundation.Enums;
using Harborline.Api.Foundation.Models;

namespace Harborline.Api.Foundation.Services;

/// <summary>
/// Default implementation of <see cref="IHarborlineNotificationService"/>
/// that maintains an in-memory list of active notifications.
/// </summary>
public class HarborlineNotificationService : IHarborlineNotificationService
{
    private readonly List<NotificationModel> _notifications = [];
    private readonly object _lock = new();

    /// <inheritdoc />
    public event Action? OnChange;

    /// <inheritdoc />
    public void Show(NotificationModel notification)
    {
        lock (_lock)
        {
            _notifications.Add(notification);
        }
        OnChange?.Invoke();
    }

    /// <inheritdoc />
    public void ShowToast(string message, ToastSeverity severity = ToastSeverity.Info, int durationMs = 3000)
    {
        var model = new NotificationModel
        {
            Message = message,
            Severity = severity,
            CloseAfterMs = durationMs,
            Closeable = true
        };
        Show(model);
    }

    /// <inheritdoc />
    public void ShowSnackbar(string message, int durationMs = 3000)
    {
        var model = new NotificationModel
        {
            Message = message,
            Severity = ToastSeverity.Info,
            CloseAfterMs = durationMs,
            Closeable = true
        };
        Show(model);
    }

    /// <inheritdoc />
    public void Hide(string id)
    {
        lock (_lock)
        {
            _notifications.RemoveAll(n => n.Id == id);
        }
        OnChange?.Invoke();
    }

    /// <inheritdoc />
    public void HideAll()
    {
        lock (_lock)
        {
            _notifications.Clear();
        }
        OnChange?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<NotificationModel> GetNotifications()
    {
        lock (_lock)
        {
            return _notifications.ToList().AsReadOnly();
        }
    }
}
