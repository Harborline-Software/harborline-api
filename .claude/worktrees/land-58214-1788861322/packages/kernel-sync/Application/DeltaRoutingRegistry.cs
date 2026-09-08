using System.Collections.Concurrent;

namespace Harborline.Api.Kernel.Sync.Application;

/// <summary>
/// The stock <see cref="IDeltaRouter"/>: a thread-safe registry that dispatches
/// outbound encode + inbound apply to the <see cref="IDeltaProducer"/> /
/// <see cref="IDeltaSink"/> registered for a document id, with the first
/// registration serving as the default route for the daemon's Phase-1
/// <c>"default"</c> stream id and any unrecognized id.
/// </summary>
/// <remarks>
/// <para>
/// Container-bridge a2 (ONR survey 2026-06-19). The gossip daemon resolves THIS
/// as its <see cref="IDeltaProducer"/>/<see cref="IDeltaSink"/>; the composition
/// root registers each synced doctype. A router with zero registrations is a
/// Noop (the pre-bridge posture — an empty outbound delta, a dropped inbound
/// delta), so wiring it in is safe even before any doctype registers.
/// </para>
/// <para>
/// <b>Routing rule.</b> An explicit id match wins. Otherwise — including the
/// daemon's well-known <c>"default"</c> Phase-1 stream id — the request routes to
/// <see cref="DefaultEntry"/> (the first-registered doctype). With v1's single
/// contacts registration this delivers the daemon's <c>"default"</c>-tagged
/// traffic to the contacts projection, preserving the working sync path while
/// the id seam stands ready for doctype #2.
/// </para>
/// </remarks>
public sealed class DeltaRoutingRegistry : IDeltaRouter, IDeltaStateVectorProvider
{
    /// <summary>
    /// The daemon's well-known Phase-1 stream id. A request for THIS id (or an
    /// empty id) routes to <see cref="DefaultEntry"/> — back-compat with a peer /
    /// outbound path that has not migrated to per-id fan-out yet. A genuinely
    /// UNREGISTERED id (e.g. a future <c>"calendar"</c> doctype a newer peer ships
    /// that this node does not run) is DROPPED — never mis-routed to the default
    /// doctype. This sentinel is the ONLY id that falls through to the default.
    /// </summary>
    public const string WellKnownDefaultStreamId = "default";

    private sealed record Route(IDeltaProducer Producer, IDeltaSink Sink, Func<byte[], bool>? RecipientFilter = null);

    private readonly ConcurrentDictionary<string, Route> _routes =
        new(StringComparer.Ordinal);

    // Guards first-write of _defaultId so the FIRST Register call wins the default
    // slot deterministically even under concurrent registration.
    private readonly object _defaultGate = new();
    private volatile string? _defaultId;

    /// <inheritdoc />
    public string? DefaultEntry => _defaultId;

    /// <inheritdoc />
    public IReadOnlyCollection<string> RegisteredDocumentIds => _routes.Keys.ToArray();

    /// <inheritdoc />
    public void Register(string documentId, IDeltaProducer producer, IDeltaSink sink) =>
        Register(documentId, producer, sink, recipientFilter: null);

    /// <inheritdoc />
    public void Register(
        string documentId, IDeltaProducer producer, IDeltaSink sink, Func<byte[], bool>? recipientFilter)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(sink);

        if (!_routes.TryAdd(documentId, new Route(producer, sink, recipientFilter)))
        {
            throw new ArgumentException(
                $"A delta route for document id '{documentId}' is already registered.",
                nameof(documentId));
        }

        // First registration claims the default slot (the target for the daemon's
        // Phase-1 "default" id + any unrecognized id).
        if (_defaultId is null)
        {
            lock (_defaultGate)
            {
                _defaultId ??= documentId;
            }
        }
    }

    /// <inheritdoc />
    public Func<byte[], bool>? GetRecipientFilter(string documentId) =>
        !string.IsNullOrEmpty(documentId) && _routes.TryGetValue(documentId, out var route)
            ? route.RecipientFilter
            : null;

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
        string documentId,
        ReadOnlyMemory<byte> peerVectorClock,
        CancellationToken ct)
    {
        var route = Resolve(documentId);
        return route is null
            ? ValueTask.FromResult<ReadOnlyMemory<byte>?>(null) // Noop — nothing registered.
            : route.Producer.EncodeOutboundDeltaAsync(documentId, peerVectorClock, ct);
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> GetCurrentStateVectorAsync(
        string documentId,
        CancellationToken ct)
    {
        var route = Resolve(documentId);
        return route?.Producer is IDeltaStateVectorProvider provider
            ? provider.GetCurrentStateVectorAsync(documentId, ct)
            : ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }

    /// <inheritdoc />
    public ValueTask ApplyInboundDeltaAsync(
        string documentId,
        ulong opSequence,
        ReadOnlyMemory<byte> delta,
        CancellationToken ct)
    {
        var route = Resolve(documentId);
        return route is null
            ? ValueTask.CompletedTask // Noop — nothing registered, drop the frame.
            : route.Sink.ApplyInboundDeltaAsync(documentId, opSequence, delta, ct);
    }

    /// <summary>
    /// Resolve the route for <paramref name="documentId"/>:
    /// <list type="number">
    ///   <item>an explicit id match wins;</item>
    ///   <item>the well-known <see cref="WellKnownDefaultStreamId"/> sentinel (or
    ///     an empty id) falls through to the <see cref="DefaultEntry">default</see>
    ///     (first-registered) route — back-compat with a non-fan-out peer;</item>
    ///   <item>any OTHER unregistered id resolves to <see langword="null"/> — the
    ///     frame is DROPPED, never mis-routed to the default doctype.</item>
    /// </list>
    /// <para>
    /// The drop in case (3) is the per-id fan-out safety property: a peer that
    /// ships a <c>"calendar"</c> delta to a node that does not run a calendar
    /// doctype has that frame dropped, NOT silently applied to contacts. Only the
    /// deliberate <c>"default"</c> sentinel — which a legacy/single-stream peer
    /// emits on purpose — reaches the default route.
    /// </para>
    /// </summary>
    private Route? Resolve(string documentId)
    {
        if (!string.IsNullOrEmpty(documentId)
            && _routes.TryGetValue(documentId, out var exact))
        {
            return exact;
        }

        // Only the well-known "default" sentinel (or an empty id) falls back to the
        // default route. A genuinely-unknown id is dropped — see case (3) above.
        if (!string.IsNullOrEmpty(documentId)
            && !string.Equals(documentId, WellKnownDefaultStreamId, StringComparison.Ordinal))
        {
            return null;
        }

        var defaultId = _defaultId;
        return defaultId is not null && _routes.TryGetValue(defaultId, out var fallback)
            ? fallback
            : null;
    }
}
