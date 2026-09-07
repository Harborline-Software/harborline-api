namespace Harborline.Api.Kernel.Sync.Application;

/// <summary>
/// The id-routed delta seam (container-bridge a2). A registry that maps a
/// logical <b>document id</b> to the <see cref="IDeltaProducer"/> /
/// <see cref="IDeltaSink"/> that owns that doctype, and itself acts as a single
/// composite <see cref="IDeltaProducer"/> + <see cref="IDeltaSink"/> the gossip
/// daemon resolves transparently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The container-bridge rewire (ONR survey
/// <c>container-bridge-routing-survey-2026-06-19.md</c>, CIC-decided option a2)
/// wires the per-team child-container <see cref="Gossip.GossipDaemon"/> to the
/// install-level synced-doctype projection(s) that live in the outer container.
/// Rather than hard-wire "this daemon → the one contacts projection" (option a1),
/// the daemon resolves THIS router, and the composition root
/// <see cref="Register(string,IDeltaProducer,IDeltaSink)"/>s
/// each synced doctype. v1 registers exactly one entry (contacts); doctype #2 is
/// an <b>additive registration</b>, not a re-architecture — the standing
/// "prefer cleanest long-term option" call.
/// </para>
/// <para>
/// <b>Dependency direction.</b> This type lives in <c>kernel-sync</c> alongside
/// the <see cref="IDeltaProducer"/>/<see cref="IDeltaSink"/> interfaces it
/// composes. The per-team registrar in <c>kernel-runtime</c> bridges to it via
/// those interfaces ONLY — <c>kernel-runtime</c> never references the concrete
/// app-level <c>ContactCrdtProjection</c> type (the app depends on the kernel,
/// not the reverse).
/// </para>
/// <para>
/// <b>The daemon's Phase-1 "default" stream id.</b> Today
/// <see cref="Gossip.GossipDaemon"/> hard-codes a single <c>"default"</c> stream
/// id for both outbound encode and the echoed inbound <c>StreamId</c> (it is
/// single-document at the wire level — Phase 1). The router therefore treats the
/// daemon's well-known <c>"default"</c> id, AND a request whose id matches no
/// explicit registration, as routing to the <see cref="DefaultEntry">default
/// (sole / first-registered) doctype</see>. Genuine per-id wire-level fan-out
/// (the daemon shipping one DELTA_STREAM per registered doctype) is the daemon
/// follow-on the survey flags separately; this router is the seam that makes that
/// follow-on additive rather than a re-architecture.
/// </para>
/// </remarks>
public interface IDeltaRouter : IDeltaProducer, IDeltaSink
{
    /// <summary>
    /// Register the <see cref="IDeltaProducer"/>/<see cref="IDeltaSink"/> pair
    /// that owns <paramref name="documentId"/>. The first registration also
    /// becomes the <see cref="DefaultEntry">default route</see> (the target for
    /// the daemon's Phase-1 <c>"default"</c> stream id and for any unrecognized
    /// id), so v1 — which registers exactly one doctype (contacts) — routes the
    /// daemon's traffic to it with no further wiring.
    /// </summary>
    /// <param name="documentId">The logical doctype id (e.g.
    /// <c>ContactCrdtProjection.DocumentId</c> = <c>"contacts"</c>). Must be
    /// non-empty and not already registered.</param>
    /// <param name="producer">The outbound delta producer for this doctype.</param>
    /// <param name="sink">The inbound delta sink for this doctype.</param>
    /// <exception cref="System.ArgumentException"><paramref name="documentId"/>
    /// is null/empty or already registered.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="producer"/>
    /// or <paramref name="sink"/> is null.</exception>
    void Register(string documentId, IDeltaProducer producer, IDeltaSink sink);

    /// <summary>
    /// C5 — register a doctype WITH a PARTICIPANT-SCOPED outbound recipient filter (the participant-scoped sync
    /// routing primitive). The <paramref name="recipientFilter"/> takes a connected peer's TRANSPORT public key (the
    /// raw bytes the trust-gated HELLO presented) and returns <see langword="true"/> iff that peer should RECEIVE
    /// this stream's deltas. The gossip daemon consults it per peer on the outbound round and SKIPS the stream for a
    /// peer the filter rejects — so a 1:1 DM conversation's deltas ship ONLY toward its two participants, never the
    /// whole team (metadata defence-in-depth; the body is already sealed by the DM content seal).
    /// </summary>
    /// <remarks>
    /// A stream registered via the plain <see cref="Register(string,IDeltaProducer,IDeltaSink)"/> has NO filter
    /// (<see cref="GetRecipientFilter"/> returns null) and fans out to EVERY trusted peer exactly as before (the
    /// team channel + contacts + roster). The filter is the OUTBOUND cooperative optimization; the ENFORCED boundary
    /// is the inbound fail-closed guard at the doctype's merge gate (it drops a stream it is not a participant of)
    /// plus the content seal (a leaked frame is unreadable). A null/absent filter ⇒ all-peers (back-compat).
    /// </remarks>
    /// <param name="documentId">The doctype id (e.g. a <c>dm:</c> conversation id).</param>
    /// <param name="producer">The outbound delta producer for this doctype.</param>
    /// <param name="sink">The inbound delta sink for this doctype.</param>
    /// <param name="recipientFilter">peer-transport-pubkey → "should this peer receive this stream?" — null = all peers.</param>
    void Register(
        string documentId,
        IDeltaProducer producer,
        IDeltaSink sink,
        System.Func<byte[], bool>? recipientFilter);

    /// <summary>
    /// C5 — the participant-scoped outbound recipient filter registered for <paramref name="documentId"/>, or
    /// <see langword="null"/> when the stream has none (fans out to all peers). The gossip daemon calls this per
    /// stream per peer to decide whether to ship the stream to that peer.
    /// </summary>
    System.Func<byte[], bool>? GetRecipientFilter(string documentId);

    /// <summary>
    /// The default doctype id — the first one
    /// <see cref="Register(string,IDeltaProducer,IDeltaSink)"/>ed — that the
    /// daemon's Phase-1 <c>"default"</c> stream id (and any unrecognized id)
    /// routes to. <see langword="null"/> until at least one doctype is registered
    /// (a router with no registrations behaves as a Noop, the pre-bridge posture).
    /// </summary>
    string? DefaultEntry { get; }

    /// <summary>The set of registered doctype ids — diagnostics + tests.</summary>
    System.Collections.Generic.IReadOnlyCollection<string> RegisteredDocumentIds { get; }
}
