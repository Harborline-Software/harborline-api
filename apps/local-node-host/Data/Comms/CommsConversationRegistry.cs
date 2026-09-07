using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Application;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The conversation registry (C1) — lazily creates + caches ONE <see cref="CommsCrdtProjection"/> per
/// conversation, keyed by conversation id, and registers each on the install-level <see cref="IDeltaRouter"/>
/// so the conversation's CRDT document syncs as its own stream. This is the seam the design's "one CRDT
/// document per conversation" decision needs: the team channel is the well-known conversation
/// <see cref="CommsConversation.TeamConversationId"/>; a 1:1 DM (C2+) is a lazily-created
/// <c>dm:</c>-prefixed conversation. C1 ships ONLY the team channel — the registry's lazy factory is the
/// place DM conversations attach later, additively.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registry, not one projection.</b> The delta router + gossip daemon isolate sync streams by
/// document id, and the later participant-scoped routing filter (C5) is naturally per-document-id. So each
/// conversation gets its OWN CRDT document (id = the conversation id) + its OWN projection, and each registers
/// on the router. One-document-many-conversations would put every thread on one stream and defeat the C5
/// routing filter — so the registry is the "prefer the cleanest long-term option" call.
/// </para>
/// <para>
/// <b>Shared durable store, scoped per conversation.</b> All conversations share the ONE recoverable
/// <c>messages</c> table (the SQLCipher <c>local-node.db</c>, SC4-C2); each projection scopes its EF
/// reads/hydration to <c>(TenantId, ConversationId)</c>. There is exactly one table; the conversation is a
/// column, not a table-per-thread.
/// </para>
/// <para>
/// <b>Forge-proof attribution carries over unchanged.</b> Every conversation's projection gets the SAME
/// <paramref name="rosterBinding"/> (the seeded <c>NodeTeamRoster</c>'s forge-proof party→key resolver), so a
/// DM message is attribution-verified exactly as a team message — the conversation scope composes
/// orthogonally with the per-author signature gate.
/// </para>
/// <para>
/// <b>Thread-safety.</b> A concurrent dictionary + a per-id lazy guards against two callers racing to create
/// the same conversation's projection; the first wins, the loser disposes its throwaway. Registration on the
/// router is idempotent-by-throw (the router rejects a duplicate id), so only the winning projection registers.
/// </para>
/// </remarks>
public sealed class CommsConversationRegistry : IAsyncDisposable
{
    private readonly ICrdtEngine _engine;
    private readonly IDbContextFactory<NodeLocalCommsDbContext> _contextFactory;
    private readonly IOperationVerifier _verifier;
    private readonly Func<string, PrincipalId?>? _rosterBinding;
    private readonly IDeltaRouter _router;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CommsConversationRegistry> _logger;

    // C4 — the per-conversation DM key provider (X25519-ECDH + HKDF). Null = no DM sealing wired (a minimal/team-
    // only host or a pre-C4 test). When present, a dm: projection is built WITH the key provider + its participant
    // pair so its body is sealed on append + unsealed-then-verified on merge for a participant (opaque for a
    // non-participant). The participant pair for a dm: id is supplied by the caller (the route derived the id from
    // the pair) and cached so a subsequent GetOrCreate for the same id reuses the keyed projection.
    private readonly IDmConversationKeyProvider? _dmKeyProvider;

    // C5 — participant-scoped routing. Resolves a connected peer's PARTY ID from the raw transport pubkey its
    // trust-gated HELLO presented (= NodeTeamRoster.PartyIdForTransportKey). When present, a dm: conversation
    // registers on the delta router WITH a recipient filter: the daemon ships the dm: stream's deltas ONLY to a peer
    // whose resolved party id is one of the two participants — so a DM is not even fanned out to a non-participant
    // team member (metadata defence-in-depth; the body is already sealed). Null (a minimal/team-only host) = no
    // routing filter (every dm: stream fans out to all peers as before — the seal still protects the body).
    private readonly Func<byte[], string?>? _peerPartyResolver;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<CommsCrdtProjection>> _conversations =
        new(StringComparer.Ordinal);

    // The participant pair a dm: conversation was first created with — so a later GetOrCreate(id) (e.g. cold-start
    // hydration, the bare router resolve) reconstructs the SAME keyed projection. (party id pair, ordered.)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string A, string B)> _dmParticipants =
        new(StringComparer.Ordinal);

    /// <summary>Constructs the registry over the shared comms dependencies.</summary>
    /// <param name="engine">The CRDT engine each conversation's document is created on.</param>
    /// <param name="contextFactory">The recoverable comms store factory.</param>
    /// <param name="verifier">The merge-path signature verifier (#1277 B1a).</param>
    /// <param name="router">The install-level delta router each conversation's stream registers on.</param>
    /// <param name="loggerFactory">Logger factory for per-projection loggers.</param>
    /// <param name="rosterBinding">The forge-proof party→key resolver (null = pre-enrollment B1a-only).</param>
    /// <param name="dmKeyProvider">
    /// The per-conversation DM key provider (C4) — when present, dm: projections seal/unseal bodies. Null = team-
    /// only / pre-C4 (no DM sealing).
    /// </param>
    /// <param name="peerPartyResolver">
    /// C5 — resolves a connected peer's party id from its raw transport pubkey (= <c>NodeTeamRoster
    /// .PartyIdForTransportKey</c>). When present, a dm: conversation registers WITH a participant-scoped recipient
    /// filter so its stream ships only to its two participants. Null = no routing filter (the seal still protects).
    /// </param>
    public CommsConversationRegistry(
        ICrdtEngine engine,
        IDbContextFactory<NodeLocalCommsDbContext> contextFactory,
        IOperationVerifier verifier,
        IDeltaRouter router,
        ILoggerFactory loggerFactory,
        Func<string, PrincipalId?>? rosterBinding = null,
        IDmConversationKeyProvider? dmKeyProvider = null,
        Func<byte[], string?>? peerPartyResolver = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<CommsConversationRegistry>();
        _rosterBinding = rosterBinding;
        _dmKeyProvider = dmKeyProvider;
        _peerPartyResolver = peerPartyResolver;
    }

    /// <summary>The conversation ids that currently have a live projection — diagnostics + tests.</summary>
    public IReadOnlyCollection<string> ActiveConversationIds => _conversations.Keys.ToArray();

    /// <summary>
    /// Get (creating + registering on first use) the projection for <paramref name="conversationId"/>. A
    /// null/empty/whitespace id normalizes to the team channel. The first caller for an id creates the
    /// projection + registers its CRDT-document stream on the delta router; subsequent callers get the cached
    /// instance.
    /// </summary>
    public CommsCrdtProjection GetOrCreate(string? conversationId)
    {
        var id = CommsConversation.Normalize(conversationId);
        var lazy = _conversations.GetOrAdd(id, key => new Lazy<CommsCrdtProjection>(() => CreateAndRegister(key)));
        return lazy.Value;
    }

    /// <summary>
    /// Get (creating + registering on first use) the projection for a DM conversation, supplying the participant
    /// PAIR (C4) so the projection can derive its per-conversation seal key + seal/unseal bodies. The route, which
    /// derived the dm: id from the (active member, other party) pair, calls this so the keyed projection exists.
    /// The pair is cached, so a later <see cref="GetOrCreate(string)"/> for the same id (cold-start hydration, the
    /// bare router resolve) reconstructs the SAME keyed projection. Throws if the id is not a dm: id or the pair
    /// does not derive to it (the deterministic identity binds the pair to the id).
    /// </summary>
    public CommsCrdtProjection GetOrCreateDm(string conversationId, string participantA, string participantB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantA);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantB);
        if (!CommsConversation.IsDirectMessage(conversationId))
            throw new ArgumentException($"'{conversationId}' is not a DM conversation id.", nameof(conversationId));

        // Order the pair deterministically (matches DmConversationId.Derive + the descriptor) so a re-create with
        // the pair in the other order maps to the same cached entry + projection.
        var (a, b) = string.CompareOrdinal(participantA, participantB) <= 0
            ? (participantA, participantB) : (participantB, participantA);
        _dmParticipants[conversationId] = (a, b);
        return GetOrCreate(conversationId);
    }

    /// <summary>
    /// Hydrate the conversation's CRDT list from the recoverable store (cold-start). Idempotent + loop-safe —
    /// delegates to <see cref="CommsCrdtProjection.HydrateFromStoreAsync"/> for the (created-if-needed)
    /// conversation. Returns the number of rows hydrated.
    /// </summary>
    public Task<int> HydrateAsync(string conversationId, CancellationToken ct) =>
        GetOrCreate(conversationId).HydrateFromStoreAsync(ct);

    private CommsCrdtProjection CreateAndRegister(string conversationId)
    {
        // C4 — for a dm: conversation with a known participant pair + a wired key provider, build a KEYED
        // projection (seals on append, unseals-then-verifies on merge for a participant; opaque for a
        // non-participant). For the team channel (or a dm: id with no known pair / no key provider) build a
        // plaintext projection.
        IDmConversationKeyProvider? dmKeyProvider = null;
        string? dmA = null, dmB = null;
        if (CommsConversation.IsDirectMessage(conversationId)
            && _dmKeyProvider is not null
            && _dmParticipants.TryGetValue(conversationId, out var pair))
        {
            dmKeyProvider = _dmKeyProvider;
            (dmA, dmB) = pair;
        }

        var projection = new CommsCrdtProjection(
            _engine,
            _contextFactory,
            _verifier,
            _loggerFactory.CreateLogger<CommsCrdtProjection>(),
            _rosterBinding,
            conversationId,
            dmKeyProvider,
            dmA,
            dmB);

        // Register the conversation's CRDT document (id = the conversation id) on the install-level router so
        // it syncs as its own stream. The team channel is the FIRST comms conversation registered, so it claims
        // the comms-side default route exactly as the pre-C1 single "comms" document did.
        //
        // C5 — PARTICIPANT-SCOPED ROUTING: a dm: conversation registers WITH a recipient filter so the daemon ships
        // its stream ONLY toward its two participants (metadata defence-in-depth). The filter resolves a connected
        // peer's party id from the transport pubkey its HELLO presented, then admits the stream iff that party is one
        // of the two participants. Fail-closed: an unidentified peer (no party resolved) is NOT a recipient — a DM is
        // never shipped to a peer we cannot place in the conversation. The team channel + a dm: with no resolver
        // register with NO filter (fan out to all peers; the seal protects the DM body regardless).
        if (CommsConversation.IsDirectMessage(conversationId)
            && _peerPartyResolver is not null
            && dmA is not null && dmB is not null)
        {
            var participantA = dmA;
            var participantB = dmB;
            var resolvePeerParty = _peerPartyResolver;
            bool RecipientFilter(byte[] peerTransportKey)
            {
                var party = resolvePeerParty(peerTransportKey);
                if (party is null) return false; // unidentified peer → fail-closed (not a recipient of this DM).
                return string.Equals(party, participantA, StringComparison.Ordinal)
                    || string.Equals(party, participantB, StringComparison.Ordinal);
            }
            _router.Register(conversationId, projection, projection, RecipientFilter);
        }
        else
        {
            _router.Register(conversationId, projection, projection);
        }

        _logger.LogInformation(
            "Comms conversation '{ConversationId}' created + registered on the delta router (kind: {Kind}).",
            conversationId, CommsConversation.IsTeam(conversationId) ? "team" : "non-team");

        return projection;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _conversations.Values)
        {
            if (lazy.IsValueCreated)
            {
                await lazy.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
        _conversations.Clear();
    }
}
