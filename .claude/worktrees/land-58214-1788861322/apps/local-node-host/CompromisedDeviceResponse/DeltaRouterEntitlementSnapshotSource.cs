using Harborline.Api.Kernel.Sync.Application;

namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

/// <summary>Reads the document set exposed to a team member from the live sync-route registry.</summary>
public sealed class DeltaRouterEntitlementSnapshotSource : IDeviceEntitlementSnapshotSource
{
    private readonly IDeltaRouter _router;

    /// <summary>Creates the inventory over the install's live delta router.</summary>
    public DeltaRouterEntitlementSnapshotSource(IDeltaRouter router) =>
        _router = router ?? throw new ArgumentNullException(nameof(router));

    /// <inheritdoc />
    public IReadOnlyCollection<string> SnapshotDocumentIds(string teamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        return _router.RegisteredDocumentIds.ToArray();
    }
}
