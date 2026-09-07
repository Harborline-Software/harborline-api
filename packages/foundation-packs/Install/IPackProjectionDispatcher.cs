namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>Host-internal one-shot projection seam. The authority never crosses a public surface.</summary>
public interface IPackProjectionDispatcher
{
    object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default);
}

/// <summary>
/// A projection result that can report its own refusals. A refusal is a VALUE, not an exception, so
/// without this the installer would mark an admission complete for a pass that refused the pack's
/// definitions and nothing would ever re-run it. Implemented by the host's projection summary; results
/// that do not implement it are treated as admitted, as before.
/// </summary>
public interface IPackProjectionRefusalReport
{
    /// <summary>True when the pass refused at least one item, so the admission is NOT complete.</summary>
    bool ProjectionRefused { get; }
}

/// <summary>Internal reconciliation surface used only by the host startup service.</summary>
public interface IPackProjectionReconciler
{
    void AttachProjector(IPackProjectionDispatcher projector);
    void ReconcilePending(CancellationToken cancellationToken = default);
}
