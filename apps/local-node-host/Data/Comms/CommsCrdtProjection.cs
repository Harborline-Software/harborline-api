using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Application;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The comms data↔delta CRDT bridge — the FIRST messaging doctype on the live node-host path. It mirrors
/// the contacts doctype (<c>ContactCrdtProjection</c>) but swaps the <see cref="ICrdtMap"/> for an
/// <see cref="ICrdtList"/>: a team-wide message log is append-only + ordered, not a keyed map. A message is
/// immutable — there is no edit or delete in this pilot (append-only is the point; it is also the GL's
/// shape).
/// </summary>
/// <remarks>
/// <para>
/// <b>The message write path flows THROUGH a CRDT list.</b> The projection owns one
/// <see cref="ICrdtDocument"/> (id <c>"comms"</c>) holding one <see cref="ICrdtList"/> (<c>"messages"</c>).
/// On every local append, <c>CommsRoutes</c> calls <see cref="AppendLocal"/> AFTER the EF write commits —
/// pushing the message onto the CRDT list, which produces a YDotNet op. The gossip daemon then ships that
/// op as a delta via the <see cref="IDeltaProducer"/> half of this class.
/// </para>
/// <para>
/// <b>Inbound peer deltas merge + reconcile to the readable store — VERIFIED-ON-MERGE.</b> The
/// <see cref="IDeltaSink"/> half applies an inbound delta into the same CRDT document; the list's
/// <c>Changed</c> event then fires <see cref="ReconcileAsync"/>, which writes list items not yet in the
/// <c>messages</c> table back into the EF store so the comms read API (<c>CommsRoutes</c> GET) returns the
/// converged log. <b>Before any item is written, its signature is re-verified
/// (<see cref="CommsMessageFactory.VerifyAuthorship"/>); a message whose signature does NOT validate for its
/// stamped <c>AuthorIssuerId</c> is DROPPED — never inserted, never reconciled.</b> Verification that isn't
/// enforced on the merge path is theater (#1277 B1a); this is where it is enforced. The drop is log-and-skip
/// (the inbound-delta recoverable-frame contract) — one bad message never aborts the round or the reconcile.
/// The EF store is the queryable read model; the CRDT list is the convergence authority — the same
/// durable-layer pattern contacts uses, and the SC4-safe one (the recoverable <c>local-node.db</c> is the
/// ONLY durable sink — never a seed-keyed per-team KV store).
/// </para>
/// <para>
/// <b>Per-author attribution — integrity-verified, NOT yet forge-proof.</b> Each appended
/// <see cref="MessageCrdtState"/> carries its author's <c>AuthorPartyId</c> (the ADR 0032 active member's id)
/// + the author's signed identity (<c>AuthorIssuerId</c> + <c>SignatureB64Url</c> over the canonical signable
/// form). The merge-path verification proves message INTEGRITY (the body/id/party/tenant were not altered)
/// and that the holder of the stamped <c>AuthorIssuerId</c> key signed it. It does NOT prove the
/// <c>AuthorPartyId</c>↔<c>AuthorIssuerId</c> binding: any member can sign with their OWN key and stamp
/// another member's <c>AuthorPartyId</c>, and that message still verifies (#1277 B1b). Forge-proof
/// cross-user attribution awaits the enrollment / trust-roster (the trusted party→pubkey map) — see
/// cerebrum [2026-06-20] "verifiable cross-user attribution REQUIRES enrollment". Two distinct authors who
/// each sign with their own key ARE distinguishable on the converged log (the Mac↔Surface two-user flow);
/// what is not yet prevented is one member forging another's partyId.
/// </para>
/// <para>
/// <b>Reconcile is append-only + idempotent.</b> Because the list is append-only, reconcile never updates
/// or deletes a row — it only inserts items whose <c>MessageId</c> is not already present. Applying the same
/// delta twice is a no-op (the CRDT merge is idempotent AND the EF insert is guarded on <c>MessageId</c>).
/// Local appends are EF-first, so their reconcile finds the row already present and inserts nothing — no
/// loop, no version dance.
/// </para>
/// </remarks>
public sealed class CommsCrdtProjection : IDeltaProducer, IDeltaStateVectorProvider, IDeltaSink, IAsyncDisposable
{
    /// <summary>
    /// Logical CRDT document id for the (pre-C1) single comms doctype. RETAINED as a back-compat constant for
    /// any caller that still references it; the LIVE document id for a conversation-scoped projection is its
    /// <see cref="ConversationId"/> (the team channel = <see cref="CommsConversation.TeamConversationId"/>),
    /// so the delta router routes by conversation id and per-conversation sync streams (C5) are natural.
    /// </summary>
    public const string DocumentId = "comms";

    /// <summary>
    /// The conversation (thread) this projection owns — its CRDT document id, and the
    /// <c>(TenantId, ConversationId)</c> scope of its EF reads/hydration. C1: the team channel is
    /// <see cref="CommsConversation.TeamConversationId"/>; a 1:1 DM (C2+) is a <c>dm:</c>-prefixed id.
    /// </summary>
    public string ConversationId { get; }

    private readonly CommsCrdtSchema _schema;
    private readonly CrdtProjection<CommsCrdtSchema> _projection;
    private readonly IDbContextFactory<NodeLocalCommsDbContext> _contextFactory;

    // SECURITY: the verify-on-merge gate. EVERY inbound message is re-verified before it is allowed into
    // the durable EF read store (the GET source). A message whose signature does not validate for its
    // stamped AuthorIssuerId is DROPPED — see ReconcileAsync. When a roster binding is wired (the PRODUCTION
    // path, enrollment Phase B — NodeCommsComposition resolves the seeded NodeTeamRoster), the gate ALSO
    // enforces the party↔key binding, so a member forging another's partyId is DROPPED too (#1277 B1b CLOSED
    // end-to-end). Without a roster (a minimal DI test) the gate stays at integrity+key-holder (#1277 B1a).
    private readonly IOperationVerifier _verifier;

    // FORGE-PROOF ATTRIBUTION (#1277 B1b; enrollment Phase A). The trust roster's party→pubkey binding
    // resolver: returns the verified key bound to a claimed AuthorPartyId, or null when the party is not an
    // enrolled member. When present, the merge gate upgrades from integrity+key-holder verification to
    // forge-proof attribution (the stamped key must BE the roster-bound key for the claimed party) — so an
    // attacker who signs with their own key while claiming another party's id is DROPPED. Null = pre-enrollment
    // (single-/shared-root pilot, no adversary among your own nodes): the gate stays at the B1a integrity check.
    private readonly Func<string, PrincipalId?>? _rosterBinding;

    // C4 — DM CONTENT ENCRYPTION. For a dm: conversation the BODY is SEALED to a per-conversation key only the
    // two participants can derive (DerivedDmConversationKeyProvider). The projection holds the key provider + the
    // two participant party ids (the descriptor) so the merge gate can UNSEAL-then-verify a participant's DM (and
    // store the ciphertext at rest), while a non-participant projection — lacking the key — stores only opaque
    // ciphertext it cannot read (the leak property, DR-5). Null on a team-channel projection (plaintext, no seal).
    private readonly IDmConversationKeyProvider? _dmKeyProvider;
    private readonly string? _dmParticipantA;
    private readonly string? _dmParticipantB;

    private readonly ILogger<CommsCrdtProjection> _logger;

    /// <summary>
    /// Construct the bridge over a fresh comms document from the engine. The <paramref name="verifier"/> is
    /// the merge-path signature gate (#1277 B1a): inbound messages are re-verified before reaching the EF
    /// store. Stateless / shareable (a single <see cref="Ed25519Verifier"/> is registered as a singleton).
    /// </summary>
    /// <param name="engine">The CRDT engine the conversation's document is created on.</param>
    /// <param name="contextFactory">The recoverable comms store factory (the only durable sink, SC4-C2).</param>
    /// <param name="verifier">The merge-path signature gate (#1277 B1a).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="rosterBinding">The forge-proof party→key resolver (null = pre-enrollment B1a-only).</param>
    /// <param name="conversationId">
    /// The conversation this projection owns — its CRDT document id + EF scope. Defaults to the well-known team
    /// channel (<see cref="CommsConversation.TeamConversationId"/>) so existing single-projection callers
    /// (tests, the team-channel boot path) get a team-channel projection that behaves exactly as the pre-C1
    /// single comms document did.
    /// </param>
    /// <param name="dmKeyProvider">
    /// The per-conversation key provider (C4) — present ONLY for a <c>dm:</c> conversation so the merge gate can
    /// unseal-then-verify a participant's sealed DM body (and a non-participant projection stores opaque
    /// ciphertext). Null on the team channel (plaintext, no seal).
    /// </param>
    /// <param name="dmParticipantA">For a DM: one participant party id (the seal key derivation pair). Null on the team channel.</param>
    /// <param name="dmParticipantB">For a DM: the other participant party id. Null on the team channel.</param>
    public CommsCrdtProjection(
        ICrdtEngine engine,
        IDbContextFactory<NodeLocalCommsDbContext> contextFactory,
        IOperationVerifier verifier,
        ILogger<CommsCrdtProjection> logger,
        Func<string, PrincipalId?>? rosterBinding = null,
        string conversationId = CommsConversation.TeamConversationId,
        IDmConversationKeyProvider? dmKeyProvider = null,
        string? dmParticipantA = null,
        string? dmParticipantB = null,
        ICrdtProjectionRegistry? projectionRegistry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ConversationId = conversationId;
        _dmKeyProvider = dmKeyProvider;
        _dmParticipantA = dmParticipantA;
        _dmParticipantB = dmParticipantB;
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _rosterBinding = rosterBinding; // null = pre-enrollment (B1a-only); non-null = forge-proof (B1b).
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schema = new CommsCrdtSchema(conversationId, ReconcileSchemaAsync);
        _projection = new CrdtProjection<CommsCrdtSchema>(engine, _schema);
        projectionRegistry?.Register(_projection);
    }

    /// <summary>The CRDT document's current vector clock — opaque; for diagnostics + tests.</summary>
    public ReadOnlyMemory<byte> VectorClock => _projection.CurrentStateVector;

    /// <summary>Number of messages currently present in the CRDT list.</summary>
    public int Count => _projection.Read(static schema => schema.Count);

    /// <summary>
    /// Raised AFTER a LOCAL append produces a CRDT op (an <see cref="AppendLocal"/> from the comms write
    /// path). The push-on-change signal: the composition root subscribes this to the live gossip daemon's
    /// <c>TriggerPushAsync</c> so a newly-appended message reaches connected, authenticated peers in
    /// sub-second time instead of waiting for the next periodic anti-entropy round.
    /// </summary>
    /// <remarks>
    /// <b>Local-origin only.</b> Fires ONLY from <see cref="AppendLocal"/>, NOT from the list's
    /// <c>Changed</c> event (which also fires on inbound merges) — so an inbound peer message does not loop
    /// back out as a push. Handlers must not block.
    /// </remarks>
    public event EventHandler? LocalDeltaProduced
    {
        add => _projection.LocalDeltaProduced += value;
        remove => _projection.LocalDeltaProduced -= value;
    }

    /// <summary>
    /// Read the merged CRDT snapshot at <paramref name="index"/> (null if out of range). Test/diagnostic.
    /// </summary>
    public MessageCrdtState? GetAt(int index)
        => _projection.Read(schema => schema.GetAt(index));

    /// <summary>
    /// Snapshot the full converged list in its CRDT (deterministic total) order. Test/diagnostic surface;
    /// the production read path is the EF read model the reconcile writes into.
    /// </summary>
    public IReadOnlyList<MessageCrdtState> Snapshot()
    {
        return _projection.Read(static schema => schema.Snapshot());
    }

    // ── Cold-start hydration ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cold-start hydration (mirrors the contacts doctype). Seeds the CRDT comms list from the existing
    /// <c>local-node.db</c> <c>messages</c> rows so that, after a process restart, every message replicates
    /// to a fresh peer — not only the ones appended since startup. Pushes each EF row (in authored order)
    /// into the CRDT list exactly as the local append path would. Run ONCE at startup, BEFORE the gossip
    /// daemon ships deltas. Returns the number of rows hydrated.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent + loop-safe.</b> Hydration only pushes onto the CRDT list; each push raises
    /// <c>Changed</c>, but the resulting <see cref="ReconcileAsync"/> inserts nothing because every hydrated
    /// row already exists in EF (the insert is guarded on <c>MessageId</c>) — so hydration writes the CRDT,
    /// never back to EF, and cannot loop. Safe to run more than once.
    /// </remarks>
    public async Task<int> HydrateFromStoreAsync(CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // C1: hydrate ONLY this conversation's rows into this conversation's document (one document per
        // conversation). The back-fill default ("team") means a pre-C1 row whose conversation_id is "team"
        // hydrates into the team channel projection — preserving the pre-C1 team log exactly.
        var conversationId = ConversationId;
        var rows = await ctx.Set<NodeMessage>()
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.AuthoredAtUtc).ThenBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _projection.Mutate(
            schema => schema.PushMany(rows.Select(NodeMessage.ToCrdtState)),
            signalLocalDelta: false);

        _logger.LogInformation(
            "Comms CRDT cold-start hydration pushed {Count} message(s) for conversation '{ConversationId}' " +
            "from local-node.db into the sync document.",
            rows.Count, ConversationId);
        return rows.Count;
    }

    // ── Local write path: EF-first, then push onto the CRDT list ──────────────────────────────────────────

    /// <summary>
    /// Persist a just-authored, signed message into the durable EF read model (the recoverable
    /// <c>local-node.db</c> — the CRDT's only durable sink, SC4-C2). Called by <c>CommsRoutes</c> BEFORE
    /// <see cref="AppendLocal"/> (EF-first, then project) so the local row exists when the append's
    /// <c>Changed</c> reconcile runs (which then finds it already present and inserts nothing — no loop).
    /// </summary>
    public async Task PersistLocalAsync(MessageCrdtState message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<NodeMessage>().Add(NodeMessage.FromCrdtState(message));
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the durable log for THIS conversation under <paramref name="tenantId"/> in authored
    /// (chronological) order — the GET route's source. The append-only EF read model that the CRDT reconcile
    /// writes converged messages into. Scoped by BOTH the active team (<paramref name="tenantId"/>, ADR 0032 —
    /// a different active team is a different log) AND this projection's <see cref="ConversationId"/> (C1 — a
    /// different conversation is a different thread).
    /// </summary>
    public async Task<IReadOnlyList<MessageCrdtState>> ReadLogAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var conversationId = ConversationId;
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.Set<NodeMessage>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
            .OrderBy(m => m.AuthoredAtUtc).ThenBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(NodeMessage.ToCrdtState).ToArray();
    }

    // ── C4 — DM content seal/unseal helpers (the per-conversation key drives both). ──────────────────────────

    /// <summary>
    /// True if this projection owns a <c>dm:</c> conversation AND can derive its per-conversation seal key (the
    /// active member is a participant). A team-channel projection (plaintext) returns false. Used by the route to
    /// decide whether to SEAL an outgoing body + UNSEAL stored bodies for the reader.
    /// </summary>
    public bool CanSealConversation =>
        CommsConversation.IsDirectMessage(ConversationId)
        && _dmKeyProvider is not null
        && _dmParticipantA is not null && _dmParticipantB is not null
        && DeriveConversationKey() is not null;

    /// <summary>
    /// Derive this DM conversation's 32-byte per-conversation AEAD key (X25519-ECDH + HKDF, C4), or null if this
    /// is not a DM / the active member is not a participant / a participant key is unresolvable (fail-closed).
    /// </summary>
    private byte[]? DeriveConversationKey()
    {
        if (_dmKeyProvider is null || _dmParticipantA is null || _dmParticipantB is null) return null;
        if (!CommsConversation.IsDirectMessage(ConversationId)) return null;
        return _dmKeyProvider.TryDeriveConversationKey(ConversationId, _dmParticipantA, _dmParticipantB);
    }

    /// <summary>
    /// Seal a plaintext DM body for a LOCAL append (C4) — produces the signed + SEALED message the route persists
    /// + appends. The signature covers the PLAINTEXT (DR-3 sign-then-encrypt); the body is the sealed ciphertext.
    /// Throws if this projection cannot seal (not a DM / not a participant) — the route must only call this on a
    /// sealable DM conversation.
    /// </summary>
    public async ValueTask<MessageCrdtState> CreateSignedSealedLocalAsync(
        IOperationSigner signer, string authorPartyId, string tenantId, string plaintextBody,
        DateTimeOffset authoredAt, CancellationToken ct)
    {
        var key = DeriveConversationKey()
            ?? throw new InvalidOperationException(
                $"Cannot seal a DM for conversation '{ConversationId}' — the active member is not a participant or the key is unresolvable.");
        try
        {
            return await CommsMessageFactory.CreateSignedSealedAsync(
                signer, authorPartyId, tenantId, plaintextBody, authoredAt, ConversationId, key, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Try to unseal an inbound sealed DM body using this conversation's per-conversation key. Returns true +
    /// the plaintext for a PARTICIPANT; false for a non-participant (no key) or a tampered/foreign envelope
    /// (fail-closed). Used by the merge gate + the unsealed read path.
    /// </summary>
    private bool TryUnsealDmBody(MessageCrdtState m, out string plaintext)
    {
        plaintext = string.Empty;
        var key = DeriveConversationKey();
        if (key is null) return false; // not a participant — opaque ciphertext (the leak property).
        try
        {
            return DmContentSeal.TryUnseal(
                m.Body, key, m.ConversationId, m.TenantId, m.AuthorPartyId, m.MessageId, out plaintext);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Read the durable log for THIS conversation, UNSEALING DM bodies for the participant reader (C4). For a
    /// team channel this is identical to <see cref="ReadLogAsync"/> (plaintext passes through). For a DM, each
    /// sealed body is unsealed to plaintext for the participant — decryption happens IN THE NODE (DR-4 ONR lean:
    /// the renderer stays node:-free and never holds keys/crypto), and the plaintext is returned over the
    /// already-caller-auth-gated loopback. A message that fails to unseal (should not happen for a participant on
    /// a well-formed thread) is returned with a sentinel body rather than leaking ciphertext to the UI.
    /// </summary>
    public async Task<IReadOnlyList<MessageCrdtState>> ReadUnsealedLogAsync(string tenantId, CancellationToken ct)
    {
        var rows = await ReadLogAsync(tenantId, ct).ConfigureAwait(false);
        if (!CommsConversation.IsDirectMessage(ConversationId)) return rows; // team channel — plaintext as-is.

        var key = DeriveConversationKey();
        if (key is null) return rows; // not a participant — never reached on the read route (it gates on participation).
        try
        {
            var unsealed = new List<MessageCrdtState>(rows.Count);
            foreach (var m in rows)
            {
                if (DmContentSeal.IsSealed(m.Body)
                    && DmContentSeal.TryUnseal(m.Body, key, m.ConversationId, m.TenantId, m.AuthorPartyId, m.MessageId, out var pt))
                {
                    unsealed.Add(m with { Body = pt });
                }
                else
                {
                    // A non-sealed legacy/team body, or an unsealable body (a foreign/tampered ciphertext that
                    // converged): do NOT surface ciphertext as text — replace with a sentinel so the UI shows a
                    // benign placeholder rather than the opaque envelope.
                    unsealed.Add(DmContentSeal.IsSealed(m.Body) ? m with { Body = "[unable to decrypt]" } : m);
                }
            }
            return unsealed;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Append a just-persisted message onto the CRDT list. Called by <c>CommsRoutes</c> AFTER the EF write
    /// commits. Pushing onto the list produces a YDotNet op that the gossip daemon ships as a delta.
    /// </summary>
    public void AppendLocal(MessageCrdtState message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _projection.Mutate(schema => schema.Push(message));
    }

    // ── IDeltaProducer — outbound (send local ops the peer hasn't seen) ─────────────────────────────────

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
        string documentId,
        ReadOnlyMemory<byte> peerVectorClock,
        CancellationToken ct)
        => ValueTask.FromResult<ReadOnlyMemory<byte>?>(_projection.EncodeDelta(peerVectorClock));

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> GetCurrentStateVectorAsync(
        string documentId,
        CancellationToken ct)
        => ValueTask.FromResult(_projection.CurrentStateVector);

    // ── IDeltaSink — inbound (merge a peer's delta, then reconcile the new tail to EF) ──────────────────

    /// <inheritdoc />
    public ValueTask ApplyInboundDeltaAsync(
        string documentId,
        ulong opSequence,
        ReadOnlyMemory<byte> delta,
        CancellationToken ct)
    {
        var result = _projection.ApplyDelta(documentId, opSequence, delta);
        if (!result.Succeeded)
        {
            _logger.LogWarning(result.Error,
                "Comms CRDT bridge dropped inbound delta {DocumentId} op {OpSequence}: {Reason}",
                documentId, opSequence, result.Error?.Message);
        }
        return ValueTask.CompletedTask;
    }

    // ── Merge → EF reconcile ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Await every reconcile the live <see cref="OnListChanged"/> trigger has spawned so far. Test-only: the
    /// production daemon path is fire-and-forget (gossip re-ships on the next anti-entropy round if a
    /// reconcile loses), but a test asserting the EF read store after the LIVE trigger needs a deterministic
    /// join point.
    /// </summary>
    internal Task DrainPendingReconcilesAsync() => _projection.DrainPendingReconcilesAsync();

    /// <summary>
    /// Insert any CRDT-list messages not yet present in the EF <c>messages</c> table. Append-only: this only
    /// ever INSERTs (never updates/deletes), and is idempotent — a message already in EF (by
    /// <c>MessageId</c>) is skipped. Public so a test can deterministically reconcile after an explicit delta
    /// apply (the fire-and-forget event path is for the live daemon).
    /// </summary>
    /// <remarks>
    /// <b>SECURITY — verify-on-merge (#1277 B1a).</b> This is the enforcement point: a converged message is
    /// inserted into the durable read store ONLY if its signature re-verifies for its stamped
    /// <c>AuthorIssuerId</c> (<see cref="CommsMessageFactory.VerifyAuthorship"/>). An unsigned / garbage-signed
    /// / body-tampered message is DROPPED (log-and-skip) — it never reaches EF, so the GET route never returns
    /// it. The CRDT list (the convergence authority) still HOLDS the item — convergence is by design
    /// merge-everything — but the EF read model is gated. SECURITY: this verifies INTEGRITY + that the holder
    /// of the stamped key signed it; it does NOT verify the <c>AuthorPartyId</c>↔<c>AuthorIssuerId</c> binding
    /// (a member can sign with their own key and claim another's partyId — that still passes), pending the
    /// enrollment / trust-roster (ADR 0032 follow-on); see cerebrum [2026-06-20].
    /// </remarks>
    public Task ReconcileAsync(CancellationToken ct) => _projection.ReconcileAsync(ct);

    private async Task ReconcileSchemaAsync(CancellationToken ct)
    {
        // Snapshot the converged list under the gate, then write outside it.
        IReadOnlyList<MessageCrdtState> snapshot = Snapshot();
        if (snapshot.Count == 0) return;

        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var existingIds = await ctx.Set<NodeMessage>()
                .AsNoTracking()
                .Select(m => m.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var present = new HashSet<string>(existingIds, StringComparer.Ordinal);

            var toInsert = new List<NodeMessage>();
            foreach (var m in snapshot)
            {
                if (!present.Add(m.MessageId)) continue; // Add returns false if already present — skip duplicates.

                // C1 — CONVERSATION-SCOPE GUARD (fail-closed): this projection owns exactly one conversation;
                // its CRDT document id IS that conversation id, so a converged message MUST carry this
                // conversation's id. A message stamped with a DIFFERENT conversation id is dropped — it does not
                // belong in this thread's read store. This is the inbound twin of the per-conversation routing
                // (C5) seam: the conversation id is in the SIGNED payload, so a mis-stamped message also fails
                // the verify gate below, but guarding here keeps a mis-routed/buggy frame out of this thread
                // even before the (cheaper) signature check. (For C1 the team channel is the only live
                // conversation; this guard is the structural seed the DM increments build their participant
                // fail-closed twin on.)
                if (!string.Equals(m.ConversationId, ConversationId, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Comms CRDT reconcile DROPPED message {MessageId}: stamped conversation '{Stamped}' " +
                        "does not match this projection's conversation '{Owned}' — not stored.",
                        m.MessageId, m.ConversationId, ConversationId);
                    continue;
                }

                // C4 — DM CONTENT ENCRYPTION merge handling. A dm: message body is SEALED (ciphertext). The seal
                // is verified + stored differently from a plaintext team message:
                //   • PARTICIPANT (this node can derive the per-conversation key): UNSEAL → re-verify authorship
                //     over the recovered PLAINTEXT (DR-3 sign-then-encrypt — the signature attested the plaintext)
                //     → store the CIPHERTEXT at rest (the at-rest body stays sealed; decryption is per-read). A
                //     forged/tampered DM that unseals but fails authorship is DROPPED; one whose ciphertext is
                //     tampered fails to unseal and is DROPPED.
                //   • NON-PARTICIPANT (this node CANNOT derive the key — a team member on the same plane, DR-5):
                //     the body is opaque ciphertext. It is stored AS-IS (the CRDT converged it) but is NEVER
                //     readable — the non-participant holds only ciphertext. We do NOT run plaintext-authorship on
                //     it (we can't read it), and we do NOT drop it (the leak test asserts the ciphertext row is
                //     present-but-opaque). It will never reach a renderer as plaintext (the read path can't
                //     unseal it either). This is the cryptographic leak guarantee.
                if (CommsConversation.IsDirectMessage(m.ConversationId) && DmContentSeal.IsSealed(m.Body))
                {
                    // C5 — INBOUND FAIL-CLOSED PARTICIPANT GUARD (the inbound twin of participant-scoped routing).
                    // A dm: conversation is readable ONLY by its two participants. If THIS node is not a participant
                    // (it cannot derive the per-conversation key — the active member is not one of the two parties, or
                    // a participant DM key is unresolvable) it DROPS the message entirely — it does not even persist
                    // the opaque ciphertext. This is stronger than C4 (which stored the opaque row): C5 keeps a
                    // non-participant's store free of the DM altogether, so a mis-routed/hostile frame that slipped
                    // past the outbound filter never lands on a non-participant. (Defence-in-depth: even without this
                    // the body is unreadable — the seal — but a non-participant should hold NOTHING of a DM it is not
                    // in.) A participant (key derivable) proceeds to unseal-then-verify below.
                    if (!CanSealConversation)
                    {
                        _logger.LogWarning(
                            "Comms CRDT reconcile DROPPED DM {MessageId} for conversation '{ConversationId}': this "
                            + "node is NOT a participant (cannot derive the per-conversation key) — not stored "
                            + "(C5 inbound fail-closed participant guard).",
                            m.MessageId, m.ConversationId);
                        continue;
                    }

                    if (TryUnsealDmBody(m, out var dmPlaintext))
                    {
                        // Participant: verify authorship over the recovered plaintext, then store the ciphertext.
                        var sealedVerified = _rosterBinding is null
                            ? CommsMessageFactory.VerifySealedAuthorship(m, dmPlaintext, _verifier)
                            : CommsMessageFactory.VerifySealedAuthorship(m, dmPlaintext, _verifier, _rosterBinding);
                        if (!sealedVerified)
                        {
                            _logger.LogWarning(
                                "Comms CRDT reconcile DROPPED sealed DM {MessageId} (author {AuthorPartyId}): "
                                + "authorship did not verify over the unsealed plaintext — not stored.",
                                m.MessageId, m.AuthorPartyId);
                            continue;
                        }
                    }
                    // Whether participant (verified) or non-participant (opaque), store the CIPHERTEXT at rest.
                    toInsert.Add(NodeMessage.FromCrdtState(m));
                    continue;
                }

                // SECURITY — verify-on-merge (plaintext path: team channel + any non-sealed body): drop any
                // message that fails authorship verification. log-and-skip (the inbound recoverable-frame
                // contract): one bad message never aborts the reconcile. A LOCALLY-appended message was signed by
                // this node + (when enrolled) carries this node's roster-bound key, so it verifies and is
                // unaffected; a peer message that fails verification never lands in EF.
                //   • WITH a roster binding (enrollment Phase A) → FORGE-PROOF (#1277 B1b): the stamped issuer
                //     key MUST be the roster's bound key for the claimed AuthorPartyId, so an attacker signing
                //     with their own key while claiming another party's id is DROPPED. Closes #1277 B1.
                //   • WITHOUT a roster (pre-enrollment single-/shared-root pilot) → B1a only: integrity +
                //     key-holder (party↔key binding not checked — no adversary among your own nodes).
                var verified = _rosterBinding is null
                    ? CommsMessageFactory.VerifyAuthorship(m, _verifier)
                    : CommsMessageFactory.VerifyAuthorship(m, _verifier, _rosterBinding);
                if (!verified)
                {
                    _logger.LogWarning(
                        "Comms CRDT reconcile DROPPED message {MessageId} (author {AuthorPartyId}, issuer {AuthorIssuerId}): "
                        + "authorship did not verify ({Mode}) — not stored, not reconciled.",
                        m.MessageId, m.AuthorPartyId, m.AuthorIssuerId,
                        _rosterBinding is null ? "integrity+key-holder" : "forge-proof party↔key binding");
                    continue;
                }

                toInsert.Add(NodeMessage.FromCrdtState(m));
            }

            if (toInsert.Count == 0) return; // EF already reflects the converged log — nothing to write.

            ctx.Set<NodeMessage>().AddRange(toInsert);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("Comms CRDT reconcile inserted {Count} new message(s) into EF store", toInsert.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Comms CRDT reconcile failed: {Reason}", ex.Message);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _projection.DisposeAsync();
}

internal sealed class CommsCrdtSchema : ICrdtProjectionSchema
{
    private const string ListName = "messages";

    private readonly string _documentId;
    private readonly Func<CancellationToken, Task> _reconcile;
    private ICrdtList? _messages;
    private EventHandler<CrdtListChangedEventArgs>? _changedHandler;
    private Action<CrdtProjectionChange>? _changed;
    private int _suppressChanges;
    private bool _changedWhileSuppressed;

    public CommsCrdtSchema(string documentId, Func<CancellationToken, Task> reconcile)
    {
        _documentId = documentId;
        _reconcile = reconcile;
    }

    public string DocumentId => _documentId;

    public int Count => Messages.Count;

    public void Bind(ICrdtDocument document, Action<CrdtProjectionChange> changed)
    {
        _messages = document.GetList(ListName);
        _changed = changed;
        _changedHandler = (_, args) => OnChanged(args.Index);
        _messages.Changed += _changedHandler;
    }

    public void Unbind()
    {
        if (_messages is not null && _changedHandler is not null)
        {
            _messages.Changed -= _changedHandler;
        }
    }

    public ValueTask ReconcileAsync(CrdtProjectionChange change, CancellationToken ct) =>
        new(_reconcile(ct));

    public MessageCrdtState? GetAt(int index) => Messages.Get<MessageCrdtState>(index);

    public IReadOnlyList<MessageCrdtState> Snapshot()
    {
        var list = new List<MessageCrdtState>(Messages.Count);
        for (var index = 0; index < Messages.Count; index++)
        {
            var message = Messages.Get<MessageCrdtState>(index);
            if (message is not null)
            {
                list.Add(message);
            }
        }
        return list;
    }

    public void Push(MessageCrdtState message) => Messages.Push(message);

    public void PushMany(IEnumerable<MessageCrdtState> messages)
    {
        _suppressChanges++;
        try
        {
            foreach (var message in messages)
            {
                Messages.Push(message);
            }
        }
        finally
        {
            _suppressChanges--;
            if (_suppressChanges == 0 && _changedWhileSuppressed)
            {
                _changedWhileSuppressed = false;
                _changed?.Invoke(CrdtProjectionChange.All);
            }
        }
    }

    private void OnChanged(int index)
    {
        if (_suppressChanges > 0)
        {
            _changedWhileSuppressed = true;
            return;
        }

        _changed?.Invoke(CrdtProjectionChange.ForIndex(index));
    }

    private ICrdtList Messages =>
        _messages ?? throw new InvalidOperationException("The comms schema is not bound to a CRDT document.");
}
