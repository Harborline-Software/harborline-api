using System.Collections.Generic;
using Harborline.Api.Federation.Common;

namespace Harborline.Api.Foundation.Transport;

/// <summary>
/// Payload for <see cref="IMeshVpnAdapter.RegisterDeviceAsync"/>: registers
/// the local Harborline peer with the mesh control plane (Headscale,
/// Tailscale, NetBird).
/// </summary>
/// <remarks>
/// Per ADR 0061 A1, the mesh control plane issues its own opaque
/// <see cref="DeviceId"/> at registration time and does not see Harborline's
/// Ed25519 peer keys. The two-field <see cref="DeviceId"/> + <see cref="Peer"/>
/// shape makes the (mesh-device-id ↔ Harborline-PeerId) mapping explicit and
/// adapter-private — a single Harborline peer may rotate Headscale node-keys
/// without rotating its <see cref="PeerId"/>.
/// </remarks>
public sealed record MeshDeviceRegistration
{
    /// <summary>The mesh-control-plane-issued device identifier (e.g., a Headscale machine key, a Tailscale device ID).</summary>
    public required string DeviceId { get; init; }

    /// <summary>The Harborline federation peer this device represents.</summary>
    public required PeerId Peer { get; init; }

    /// <summary>Human-readable device name surfaced in mesh control-plane UIs.</summary>
    public required string DeviceName { get; init; }

    /// <summary>
    /// Mesh-tier tags (e.g., <c>tag:sunfish-anchor</c>, <c>tag:sunfish-bridge</c>)
    /// used by control-plane ACL policies.
    /// </summary>
    public required IReadOnlyList<string> Tags { get; init; }
}
