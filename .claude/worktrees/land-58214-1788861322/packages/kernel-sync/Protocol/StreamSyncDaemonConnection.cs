using System.Buffers.Binary;

namespace Harborline.Api.Kernel.Sync.Protocol;

/// <summary>
/// Length-prefixed-CBOR <see cref="ISyncDaemonConnection"/> over any byte
/// <see cref="Stream"/> — sync-daemon-protocol §2.2 framing (4-byte big-endian
/// u32 length prefix + CBOR payload, 16 MiB single-frame cap).
/// </summary>
/// <remarks>
/// <para>
/// This is the shared framing core used by every stream-backed transport: the
/// Unix-domain-socket / named-pipe transport (<see cref="UnixSocketSyncDaemonTransport"/>)
/// and the cross-machine TCP transport (<see cref="TcpSyncDaemonTransport"/>,
/// sync-daemon-protocol §2.1 default port 7473). Only the socket-open differs
/// between transports; the wire framing is identical, so it lives here once.
/// </para>
/// <para>
/// The framing was previously a private nested type inside
/// <see cref="UnixSocketSyncDaemonTransport"/>; it was hoisted verbatim into
/// this shared internal type when the TCP transport landed (multi-device
/// INC-1) so the two transports cannot drift apart on the wire.
/// </para>
/// </remarks>
internal sealed class StreamSyncDaemonConnection : ISyncDaemonConnection
{
    /// <summary>16 MiB single-frame cap, sync-daemon-protocol §2.2.</summary>
    internal const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _disposed;

    public string RemoteEndpoint { get; }

    public StreamSyncDaemonConnection(string remote, Stream stream)
    {
        RemoteEndpoint = remote;
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task SendAsync<TMessage>(TMessage message, CancellationToken ct) where TMessage : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = SyncMessageCodec.Encode(message);
        if (bytes.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Frame exceeds maximum {MaxFrameBytes} bytes (got {bytes.Length}).");
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
            await _stream.WriteAsync(header.ToArray().AsMemory(0, 4), ct).ConfigureAwait(false);
            await _stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<object> ReceiveAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var header = new byte[4];
        await ReadExactAsync(_stream, header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Incoming frame announces {length} bytes — exceeds 16 MiB cap.");
        }

        var payload = new byte[length];
        await ReadExactAsync(_stream, payload, ct).ConfigureAwait(false);
        return SyncMessageCodec.Decode(payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _sendGate.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await s.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Peer closed after {offset} of {buffer.Length} expected bytes.");
            }
            offset += read;
        }
    }
}
