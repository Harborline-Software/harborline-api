using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Discovery;

/// <summary>
/// The mDNS ↔ TCP-transport endpoint bridge — multi-device INC-2. Builds the
/// <see cref="PeerAdvertisement"/> a node publishes over mDNS so that the
/// advertised <see cref="PeerAdvertisement.Endpoint"/> is the <b>routable</b>
/// <c>tcp://host:port</c> a peer's <see cref="TcpSyncDaemonTransport.ConnectAsync"/>
/// can dial — not the local socket path the legacy transport advertised.
/// </summary>
/// <remarks>
/// <para>
/// The discovery → dial chain is otherwise already wired:
/// <see cref="GossipDaemonDiscoveryExtensions.AttachDiscovery"/> subscribes the
/// gossip daemon to <see cref="IPeerDiscovery.PeerDiscovered"/> and calls
/// <c>daemon.AddPeer(advertisement.Endpoint, advertisement.PublicKey)</c>; the
/// daemon's round loop then dials that endpoint via
/// <see cref="ISyncDaemonTransport.ConnectAsync"/>. The single missing piece
/// was a routable advertised endpoint, which this factory supplies by
/// resolving any wildcard / loopback host token in the transport's
/// <see cref="TcpSyncDaemonTransport.ListenEndpoint"/> to a concrete LAN
/// address.
/// </para>
/// </remarks>
public static class TcpPeerAdvertisement
{
    /// <summary>
    /// Build a self-advertisement for a node listening on
    /// <paramref name="transport"/>. The advertised endpoint is the transport's
    /// routable TCP endpoint with any wildcard / loopback host resolved to a
    /// concrete address (see <see cref="ResolveRoutableEndpoint"/>).
    /// </summary>
    /// <param name="transport">The TCP transport whose listener this node advertises.</param>
    /// <param name="nodeId">The node id (string form) advertised in TXT <c>node</c>.</param>
    /// <param name="teamPublicKey">The team-scoped Ed25519 public key advertised in TXT <c>pk</c>.</param>
    /// <param name="teamId">The team id advertised in TXT <c>team</c> (used for segment filtering).</param>
    /// <param name="schemaVersion">Protocol schema version advertised in TXT <c>schema</c>.</param>
    /// <param name="metadata">Optional free-form metadata (TXT <c>m.*</c>).</param>
    /// <param name="rosterId">Immutable roster-root identifier advertised in TXT <c>roster</c>.</param>
    public static PeerAdvertisement ForTransport(
        TcpSyncDaemonTransport transport,
        string nodeId,
        byte[] teamPublicKey,
        string teamId,
        string schemaVersion,
        IReadOnlyDictionary<string, string>? metadata = null,
        string rosterId = "")
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(teamPublicKey);
        if (transport.ListenEndpoint is null)
        {
            throw new InvalidOperationException(
                "TCP transport has no listen endpoint — an outbound-only transport cannot advertise " +
                "a dialable endpoint. Construct it with a listen endpoint to advertise over mDNS.");
        }

        var routable = ResolveRoutableEndpoint(transport.ListenEndpoint);
        return new PeerAdvertisement(
            NodeId: nodeId,
            Endpoint: routable,
            PublicKey: teamPublicKey,
            TeamId: teamId ?? string.Empty,
            SchemaVersion: schemaVersion ?? string.Empty,
            Metadata: metadata ?? new Dictionary<string, string>(),
            RosterId: rosterId ?? string.Empty);
    }

    /// <summary>
    /// Resolve a <c>tcp://host:port</c> endpoint into one whose host is
    /// reachable by a peer on the LAN. A wildcard (<c>0.0.0.0</c>) or hostname
    /// host is replaced with the first non-loopback IPv4 address of an up
    /// interface; a loopback host is kept as-is (loopback two-port test shape);
    /// a concrete address is kept as-is. The port is always preserved.
    /// </summary>
    public static string ResolveRoutableEndpoint(string listenEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(listenEndpoint);
        var (host, port) = TcpSyncDaemonTransport.ParseEndpoint(listenEndpoint);

        var needsResolution =
            string.IsNullOrEmpty(host) ||
            host == "0.0.0.0" ||
            host == "*" ||
            (!IPAddress.TryParse(host, out _) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var parsed) && IPAddress.IsLoopback(parsed)))
        {
            // Loopback is intentional (single-host two-port test) — keep it.
            return $"{TcpSyncDaemonTransport.Scheme}://{host}:{port}";
        }

        if (!needsResolution)
        {
            return $"{TcpSyncDaemonTransport.Scheme}://{host}:{port}";
        }

        var lan = FirstRoutableIPv4();
        var resolvedHost = lan?.ToString() ?? IPAddress.Loopback.ToString();
        return $"{TcpSyncDaemonTransport.Scheme}://{resolvedHost}:{port}";
    }

    private static IPAddress? FirstRoutableIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var addr = unicast.Address;
                    if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    {
                        return addr;
                    }
                }
            }
        }
        catch
        {
            /* sandboxed hosts may refuse enumeration — fall through to loopback */
        }
        return null;
    }
}
