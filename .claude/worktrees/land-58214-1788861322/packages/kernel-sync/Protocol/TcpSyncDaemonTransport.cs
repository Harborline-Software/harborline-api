using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Harborline.Api.Kernel.Sync.Protocol;

/// <summary>
/// Cross-machine TCP transport for the sync daemon — sync-daemon-protocol §2.1
/// (default port <see cref="DefaultPort"/> = 7473). The first transport whose
/// <see cref="ConnectAsync"/> <b>and</b> <see cref="ListenAsync"/> both work
/// across machines on a LAN; the Unix-domain-socket / named-pipe transport is
/// same-machine only and the WebSocket transport is accept-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing.</b> Identical to <see cref="UnixSocketSyncDaemonTransport"/> —
/// the shared <see cref="StreamSyncDaemonConnection"/> (4-byte big-endian u32
/// length prefix + CBOR, 16 MiB cap, sync-daemon-protocol §2.2). Only the
/// socket-open differs: a TCP <see cref="Socket"/> over
/// <see cref="AddressFamily.InterNetwork"/> (or v6) instead of AF_UNIX, so the
/// handshake ladder and gossip loops are byte-for-byte interchangeable with the
/// local-socket transport.
/// </para>
/// <para>
/// <b>Endpoint string format.</b> Both the dial target and the advertised
/// (routable) endpoint use <c>tcp://host:port</c> — for example
/// <c>tcp://192.168.1.42:7473</c>. The bare <c>host:port</c> form is also
/// accepted on the dial path. This is the endpoint
/// <see cref="Discovery.MdnsPeerDiscovery"/> advertises in its TXT
/// <c>endpoint</c> field and that <c>AddPeer</c> dials — see
/// <see cref="ListenEndpoint"/>.
/// </para>
/// <para>
/// <b>Listener bind.</b> An ephemeral-port bind (<c>port = 0</c>, the loopback
/// two-port test shape) resolves to the OS-assigned port and is reflected in
/// <see cref="ListenEndpoint"/> after construction. A multi-device node binds a
/// fixed port (7473) on a LAN-routable address so peers can dial it.
/// </para>
/// </remarks>
public sealed class TcpSyncDaemonTransport : ISyncDaemonTransport
{
    /// <summary>Default daemon-to-daemon TCP port, sync-daemon-protocol §2.1.</summary>
    public const int DefaultPort = 7473;

    /// <summary>Endpoint URI scheme for the routable TCP endpoint string.</summary>
    public const string Scheme = "tcp";

    private readonly Socket? _listener;
    private bool _disposed;

    /// <summary>
    /// The transport's routable listen endpoint in <c>tcp://host:port</c> form,
    /// or <c>null</c> for an outbound-only transport. After an ephemeral-port
    /// bind (<c>port = 0</c>) this reflects the OS-assigned port.
    /// </summary>
    public string? ListenEndpoint { get; }

    /// <summary>Construct an outbound-only transport (no listener).</summary>
    public TcpSyncDaemonTransport()
    {
        _listener = null;
        ListenEndpoint = null;
    }

    /// <summary>
    /// Construct a transport that listens on <paramref name="listenEndpoint"/>.
    /// Accepts <c>tcp://host:port</c> or bare <c>host:port</c>. A
    /// <c>port</c> of 0 binds an OS-assigned ephemeral port; the resolved
    /// endpoint is surfaced on <see cref="ListenEndpoint"/>.
    /// </summary>
    public TcpSyncDaemonTransport(string listenEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(listenEndpoint);
        var (host, port) = ParseEndpoint(listenEndpoint);
        var bindAddress = ResolveBindAddress(host);

        _listener = BindWithTransientRetry(bindAddress, host, port);

        var bound = (IPEndPoint)_listener.LocalEndPoint!;
        // Echo back the caller-supplied host (which may be a routable LAN
        // address or hostname) but with the resolved port — an ephemeral
        // (port 0) bind must surface the real OS-assigned port so the mDNS
        // advertisement and AddPeer dial target agree.
        ListenEndpoint = FormatEndpoint(host, bound.Port);
    }

    // Total budget for the transient-collision retry below. A just-disposed
    // sibling listener (the DAEMON-REBIND case: the old team's transport was
    // disposed microseconds ago to free this exact fixed port) releases the OS
    // port asynchronously — Socket.Dispose() returns before the kernel has fully
    // torn the listening socket down, so an immediate re-bind to the SAME port
    // can briefly observe AddressAlreadyInUse. That transient clears within tens
    // of milliseconds. A GENUINE stale orphan holding the port never clears, so a
    // short bounded retry cleanly separates the two: the rebind wins, the real
    // orphan still fails (after the budget) with the same actionable message.
    private const int TransientBindRetryBudgetMs = 750;
    private const int TransientBindRetryStepMs = 25;

    /// <summary>
    /// Bind + listen on <paramref name="bindAddress"/>:<paramref name="port"/>, retrying briefly on a
    /// TRANSIENT <see cref="SocketError.AddressAlreadyInUse"/> before declaring the port genuinely held.
    /// <para>
    /// The transient case is the daemon-rebind free-and-rebind-the-same-fixed-port path
    /// (<c>LocalNodeWorker.OnActiveTeamChanged</c>): the old team's transport is disposed to release the
    /// fixed port immediately before the new team's transport binds it, but the kernel releases the
    /// listening socket asynchronously after <c>Socket.Dispose()</c> returns — so the new bind can
    /// momentarily race the release. Without the retry this surfaced as a faulted rebind
    /// (<c>RebindOutcome.Faulted</c>) and, in tests, as an opaque <c>WaitUntil</c> timeout that was
    /// re-run-lottery'd rather than fixed. The retry makes the rebind DETERMINISTIC.
    /// </para>
    /// <para>
    /// A persistent hold (a real stale node-host orphan still owning the port) never releases, so the
    /// retry budget elapses and the original clear, actionable message is thrown — unchanged behavior for
    /// the genuine-orphan case.
    /// </para>
    /// </summary>
    private static Socket BindWithTransientRetry(IPAddress bindAddress, string host, int port)
    {
        var deadline = Environment.TickCount64 + TransientBindRetryBudgetMs;
        SocketException last;
        while (true)
        {
            var listener = new Socket(bindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(bindAddress, port));
                listener.Listen(backlog: 32);
                return listener;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Dispose();
                last = ex;

                // An ephemeral (port 0) bind can never collide on a specific port — there is nothing to
                // wait out, so do not burn the budget; fail straight to the actionable throw below. Only a
                // FIXED port can be transiently held by a just-disposed sibling.
                if (port == 0 || Environment.TickCount64 >= deadline)
                {
                    break;
                }

                Thread.Sleep(TransientBindRetryStepMs);
            }
        }

        // Budget elapsed (or an ephemeral bind, which cannot transiently collide): the port is genuinely
        // held — almost always a STALE node-host orphaned by a hard-killed Harborline App (it re-parents to PID 1
        // and keeps the port). Surface a clear, actionable message instead of letting the raw
        // SocketException bubble up to SharedHostedWebApp.StartAsync as an opaque OperationCanceledException.
        throw new InvalidOperationException(
            $"gossip port {port} on {host} is already held — a stale local-node-host " +
            "(e.g. orphaned by a previously hard-killed app) may still be running. " +
            "Stop the stray local-node-host and retry, or restart the app (which " +
            "reclaims a stale orphan holding 7473 on launch).",
            last);
    }

    public async Task<ISyncDaemonConnection> ConnectAsync(string peerEndpoint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerEndpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (host, port) = ParseEndpoint(peerEndpoint);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                if (ip.AddressFamily != socket.AddressFamily)
                {
                    socket.Dispose();
                    socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                }
                await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            }
            else
            {
                // Hostname — let the socket layer resolve + connect.
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new StreamSyncDaemonConnection(
            FormatEndpoint(host, port),
            new NetworkStream(socket, ownsSocket: true));
    }

    public async IAsyncEnumerable<ISyncDaemonConnection> ListenAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException(
                "Transport was not constructed with a listen endpoint; cannot ListenAsync.");
        }

        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            var remote = accepted.RemoteEndPoint is IPEndPoint rep
                ? FormatEndpoint(rep.Address.ToString(), rep.Port)
                : accepted.RemoteEndPoint?.ToString() ?? "tcp://unknown";
            yield return new StreamSyncDaemonConnection(
                remote, new NetworkStream(accepted, ownsSocket: true));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _listener?.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Endpoint parsing — tcp://host:port or bare host:port
    // ------------------------------------------------------------------

    /// <summary>
    /// Parse a <c>tcp://host:port</c> or bare <c>host:port</c> endpoint string.
    /// A missing port defaults to <see cref="DefaultPort"/>.
    /// </summary>
    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);

        var s = endpoint;
        if (s.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            s = s[(Scheme.Length + 3)..];
        }

        // Strip any path/query the routable form might carry.
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        if (s.Length == 0)
        {
            throw new FormatException($"Endpoint '{endpoint}' has no host:port authority.");
        }

        // IPv6 literal: [::1]:7473
        if (s[0] == '[')
        {
            var close = s.IndexOf(']');
            if (close < 0)
            {
                throw new FormatException($"Endpoint '{endpoint}' has an unterminated IPv6 literal.");
            }
            var v6Host = s[1..close];
            var rest = s[(close + 1)..];
            var v6Port = rest.StartsWith(':')
                ? ParsePort(rest[1..], endpoint)
                : DefaultPort;
            return (v6Host, v6Port);
        }

        var colon = s.LastIndexOf(':');
        if (colon < 0)
        {
            return (s, DefaultPort);
        }
        var host = s[..colon];
        var port = ParsePort(s[(colon + 1)..], endpoint);
        return (host.Length == 0 ? "0.0.0.0" : host, port);
    }

    private static int ParsePort(string portText, string endpoint)
    {
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port < 0 || port > 65535)
        {
            throw new FormatException($"Endpoint '{endpoint}' has an invalid port '{portText}'.");
        }
        return port;
    }

    private static string FormatEndpoint(string host, int port) =>
        host.Contains(':') && !host.StartsWith('[')
            ? $"{Scheme}://[{host}]:{port}"   // IPv6 literal
            : $"{Scheme}://{host}:{port}";

    /// <summary>
    /// Resolve the bind address from the caller's host token. <c>0.0.0.0</c> /
    /// empty binds <see cref="IPAddress.Any"/>; <c>localhost</c> binds loopback;
    /// an IP literal binds that address; a hostname binds Any (the listener
    /// accepts on all interfaces and the routable host is advertised separately).
    /// </summary>
    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "*")
        {
            return IPAddress.Any;
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip;
        }
        // Hostname we can't bind directly — listen on all interfaces.
        return IPAddress.Any;
    }
}
