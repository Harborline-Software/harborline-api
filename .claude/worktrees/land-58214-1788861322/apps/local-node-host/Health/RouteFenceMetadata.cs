namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>The route-fence policy installed on an endpoint group.</summary>
internal enum RouteFenceKind
{
    DesktopPlaneOnly,
    FounderWebAdmission,
    SelectedSessionProduct,
    DeviceReachableProductData,
    PreAuthOperational,
}

/// <summary>Queryable evidence that an endpoint belongs to a specific route-fence group.</summary>
internal sealed partial record RouteFenceMetadata
{
    private RouteFenceMetadata(RouteFenceKind kind)
    {
        Kind = kind;
    }

    internal RouteFenceKind Kind { get; }
}
