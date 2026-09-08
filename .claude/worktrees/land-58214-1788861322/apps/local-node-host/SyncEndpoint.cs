namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Shared helpers for the local-node-host's peer-sync endpoint strings.
/// </summary>
/// <remarks>
/// Deduplicates the <c>tcp://</c> normalization that <c>Program.cs</c> (LISTEN
/// bind) and <see cref="LocalNodeWorker"/> (static-peer dial) each applied
/// inline — the #1266 F-2 nit. One helper means one definition of "the
/// canonical <c>tcp://host:port</c> form the <c>TcpSyncDaemonTransport</c> ctor +
/// <c>IGossipDaemon.AddPeer</c> dial expect."
/// </remarks>
internal static class SyncEndpoint
{
    /// <summary>
    /// Normalize a <c>host:port</c> / <c>tcp://host:port</c> sync endpoint to the
    /// canonical <c>tcp://</c> form. A bare <c>host:port</c> is prefixed; an
    /// already-scheme-qualified value is returned unchanged (case-insensitive on
    /// the scheme).
    /// </summary>
    public static string NormalizeTcpEndpoint(string endpoint) =>
        endpoint.StartsWith("tcp://", System.StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"tcp://{endpoint}";
}
