namespace Harborline.Api.LocalNodeHost.Capabilities;

/// <summary>
/// Refusal raised when a consequential route is missing its required route-fence marker or carries
/// a marker for a different fence.
/// </summary>
internal sealed class RouteFenceViolationException : InvalidOperationException
{
    internal RouteFenceViolationException(string message)
        : base(message)
    {
    }
}
