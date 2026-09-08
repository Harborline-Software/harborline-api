using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// The roster data↔delta CRDT bridge — the TRUST-ROSTER synced doctype (the foundational production-wiring
/// gap #1). It mirrors <see cref="Comms.CommsCrdtProjection"/> (the comms append-log doctype): a team-wide
/// roster of signed admit/revoke records is append-only + ordered, carried on an <see cref="ICrdtList"/>, and
/// fanned out via the install-level <see cref="IDeltaRouter"/> under the <c>"roster"</c> document id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the roster must sync.</b> Before this, <see cref="NodeTeamRoster"/> was local-only/in-memory, seeded
/// from LOCAL genesis only — the roster never crossed the wire (the two-user harness FAKED it by seeding both
/// nodes). Making the roster a synced doctype is what lets admit/revoke propagate so all team members converge
/// on the same membership, with NO central authority (the sovereignty thesis).
/// </para>
/// <para>
/// <b>THE TRUST ANCHOR — verified-on-merge, fail-closed.</b> A node receiving roster deltas does NOT trust the
/// sender's say-so: it reconstructs the WHOLE team roster from the synced records via
/// <see cref="MemberRoster.FromSyncedRecords"/>, which re-validates every record to genesis (the signature is
/// re-checked; the admitter must be a validated in-roster admin holding <c>members:admit</c>; no-escalation;
/// a revocation's revoker must hold <c>members:revoke</c>). A forged / unsigned / wrong-signer / orphan /
/// second-genesis record is DROPPED — so a peer CANNOT inject a fake member by sending a bogus roster delta.
/// The rebuilt roster is then pushed into the live <see cref="NodeTeamRoster"/>, so the comms
/// <c>rosterBinding</c> + the <c>MemberSetTrustPolicy</c> read the SYNCED membership. If the rebuild yields no
/// trustworthy genesis (a peer who shares no genesis with us — e.g. two independently self-seeded nodes that
/// were never enrolled into one team), the rebuilt roster is EMPTY and we DO NOT adopt it — the node keeps its
/// own local genesis-seeded roster (non-bricking).
/// </para>
/// <para>
/// <b>Durable sink = the recoverable local store (SC4-C2).</b> Inbound records are reconciled into the
/// recoverable <see cref="NodeLocalRosterDbContext"/> (the SQLCipher-keyed <c>local-node.db</c>) — the roster
/// CRDT's ONLY durable sink — and re-hydrated into the CRDT list on cold start so they replicate to a fresh
/// peer after a restart. The trust is in the per-record signature (re-validated on rebuild), not in the
/// storage.
/// </para>
/// <para>
/// <b>Eventual-convergence revocation (the offline window).</b> A signed revocation syncs as a record; on a
/// converged node the rebuild drops the revoked member from the live trusted set + attribution binding. A
/// node that has NOT yet synced the revocation still honors the member until it syncs — the inherent cost of a
/// local-first, offline-capable trust model with no central authority. The window closes on the next sync
/// round (push-on-change keeps it short) but is NON-ZERO by design.
/// </para>
/// </remarks>
public sealed class RosterCrdtProjection : IDeltaProducer, IDeltaStateVectorProvider, IDeltaSink, IOwnTeamRosterSupersession, IAsyncDisposable
{
    /// <summary>Logical CRDT document id for the roster doctype (shared across replicas).</summary>
    public const string DocumentId = "roster";

    /// <summary>Reason recorded on an administrator removal the roster fold wrote (ticket 290).</summary>
    internal const string RosterRevocationRemovalReason = "administrator.removed_by_roster_revocation";

    private readonly RosterCrdtSchema _schema;
    private readonly CrdtProjection<RosterCrdtSchema> _projection;
    private readonly IDbContextFactory<NodeLocalRosterDbContext> _contextFactory;
    private readonly IOperationVerifier _verifier;
    private readonly NodeTeamRoster? _nodeRoster;
    private readonly Func<NodeAdministratorAuthority?>? _administrators;
    private readonly ILogger<RosterCrdtProjection> _logger;
    private readonly Func<AuthorizationRefusalAudit?>? _refusalAudit;
    // Accessed only under the CRDT projection's async reconcile gate.
    // Replaced wholesale on every reconcile (never mutated in place): the AM-16/G1 fence counts every
    // `.Remove(` in this file as a roster-record deletion, and this bookkeeping is not one.
    private Dictionary<(string RecordId, string Code), RebuildRefusal> _reportedRefusals = new();
    private AuthorizationRefusal[] _refusalReports = [];
    private RebuildRefusal? _rebuildFailure;
    private RebuildRefusal? _auditedRebuildFailure;

    /// <summary>A failed fold means the retained roster is not a converged trust reading.</summary>
    public AuthorizationRefusal? RebuildFailure => Volatile.Read(ref _rebuildFailure)?.Report;

    /// <summary>Current refusal state and remedies, rebuilt from roster evidence after restart.</summary>
    public IReadOnlyList<AuthorizationRefusal> RefusalReports => RebuildFailure is { } failure
        ? Array.AsReadOnly(Volatile.Read(ref _refusalReports).Append(failure).ToArray())
        : Array.AsReadOnly(Volatile.Read(ref _refusalReports));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingAdministratorRemoval>
        _pendingAdministratorRemovals = new(StringComparer.Ordinal);

    /// <summary>
    /// How many folds may re-attempt one refused removal before it is reported as refusing to converge. The
    /// counter resets when the team's established-administrator population changes - that is the only new
    /// information that can turn the refusal into an application - so the bound is on FRUITLESS retries, not
    /// on the pending item's lifetime.
    /// </summary>
    internal const int PendingAdministratorRemovalAttemptLimit = 4;

    /// <summary>
    /// A converged roster revocation whose administrator removal the clause-7 guard has refused so far
    /// (ticket 290): the peer's guard admitted it, so a successor exists in the peer's view, but the
    /// successor's admission or grant has not converged here yet. Membership is not authority - the roster
    /// revocation IS applied, and the reading ticket 211 slice 3 built refuses the ejected party - so the
    /// party exercises no authority in the gap.
    /// </summary>
    /// <param name="TeamId">The team the removal belongs to.</param>
    /// <param name="PartyId">The party the live roster no longer carries.</param>
    /// <param name="Code">The guard's own stable refusal code.</param>
    /// <param name="Attempts">How many folds have re-attempted it since the last population change.</param>
    /// <param name="EstablishedWhenTried">The team's established-administrator count at the last attempt.</param>
    internal readonly record struct PendingAdministratorRemoval(
        string TeamId, string PartyId, string Code, int Attempts, int EstablishedWhenTried);

    /// <summary>
    /// The fold's report of removals still pending (ticket 290). It is recomputed from the
    /// administrator-authority log and the live roster on every fold - the two durable structures - so it
    /// survives a restart and empties itself the moment the removal applies.
    /// </summary>
    internal IReadOnlyCollection<PendingAdministratorRemoval> PendingAdministratorRemovals =>
        _pendingAdministratorRemovals.Values.ToArray();

    /// <summary>
    /// Construct the bridge over a fresh roster document. <paramref name="verifier"/> is the merge-path
    /// signature gate (every synced record is re-validated before it can shape the live roster);
    /// <paramref name="nodeRoster"/> is the live install-level roster the rebuild pushes into (null in a
    /// minimal DI test — then the projection still converges the CRDT list + durable store but does not push a
    /// live roster, which is harmless).
    /// </summary>
    public RosterCrdtProjection(
        ICrdtEngine engine,
        IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
        IOperationVerifier verifier,
        ILogger<RosterCrdtProjection> logger,
        NodeTeamRoster? nodeRoster = null,
        ICrdtProjectionRegistry? projectionRegistry = null,
        Func<NodeAdministratorAuthority?>? administrators = null,
        Func<AuthorizationRefusalAudit?>? refusalAudit = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _nodeRoster = nodeRoster;
        _administrators = administrators;
        _refusalAudit = refusalAudit;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schema = new RosterCrdtSchema(ReconcileSchemaAsync);
        _projection = new CrdtProjection<RosterCrdtSchema>(engine, _schema);
        projectionRegistry?.Register(_projection);
    }

    /// <summary>The CRDT document's current vector clock — opaque; for diagnostics + tests.</summary>
    public ReadOnlyMemory<byte> VectorClock => _projection.CurrentStateVector;

    /// <summary>Number of roster records currently present in the CRDT list.</summary>
    public int Count => _projection.Read(static schema => schema.Count);

    /// <summary>
    /// Raised AFTER a LOCAL append produces a CRDT op (a <see cref="PublishLocal"/> from the admission surface).
    /// The push-on-change signal: the composition root subscribes this to the live gossip daemon so a
    /// just-published admit/revoke reaches connected, authenticated peers in sub-second time. Local-origin only
    /// (does NOT fire from the list's Changed event, which also fires on inbound merges) — so an inbound peer
    /// record does not loop back out as a push.
    /// </summary>
    public event EventHandler? LocalDeltaProduced
    {
        add => _projection.LocalDeltaProduced += value;
        remove => _projection.LocalDeltaProduced -= value;
    }

    /// <summary>Snapshot the full converged record list in its CRDT total order. Test/diagnostic.</summary>
    public IReadOnlyList<RosterRecordCrdtState> Snapshot()
    {
        return _projection.Read(static schema => schema.Snapshot());
    }

    // ── Cold-start hydration ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cold-start hydration (mirrors comms). Seeds the CRDT roster list from the existing
    /// <c>roster_records</c> rows so that, after a process restart, every record replicates to a fresh peer.
    /// Run ONCE at startup, BEFORE the gossip daemon ships deltas. Idempotent + loop-safe (hydration only
    /// pushes onto the CRDT list; the resulting reconcile inserts nothing because every hydrated row already
    /// exists in the durable store). Returns the number of rows hydrated.
    /// </summary>
    public async Task<int> HydrateFromStoreAsync(CancellationToken ct)
    {
        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await ctx.Set<NodeRosterRecord>()
                .AsNoTracking()
                .OrderBy(r => r.IssuedAtUtc).ThenBy(r => r.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // Decode the whole batch before changing the document: a bad row must not hydrate half a log.
            var states = rows.Select(NodeRosterRecord.ToCrdtState).ToArray();
            _projection.Mutate(
                schema => schema.PushMany(states),
                signalLocalDelta: false);
            await ReconcileAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Roster CRDT cold-start hydration pushed {Count} record(s) from local-node.db into the sync document.",
                rows.Count);
            return rows.Count;
        }
        catch (Exception ex) when (ex is JsonException or VerifiedTenantRosterRefusedException)
        {
            await ReportRebuildFailureAsync(ex, "roster.rebuild.durable_verification_failed", ct).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReportRebuildFailureAsync(ex, "roster.rebuild.failed", ct).ConfigureAwait(false);
            return 0;
        }
    }

    // ── Local publish path: durable-first, then push onto the CRDT list ─────────────────────────────────

    /// <summary>
    /// Persist + publish a locally-produced signed roster record (the genesis self-admission seeded at
    /// bootstrap, or an admit/revoke the local admin produced). Durable-first (so the row exists when the
    /// append's reconcile runs — no loop), then push onto the CRDT list so the gossip daemon ships it. Also
    /// re-derives the live <see cref="NodeTeamRoster"/> from the converged records so the local node's own gates
    /// reflect the change immediately.
    /// </summary>
    public async Task PublishLocalAsync(RosterRecordCrdtState record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Idempotent on the CRDT list: skip if this record id is already present with the SAME content (a restart
        // re-seed of the genesis self-admission, or a re-publish). The list is append-only — a duplicate push
        // would otherwise grow the converged log. INFO-2 BACKFILL EXCEPTION: when the record id is already present
        // but this publish CARRIES a transport key the stored record lacks (or a different one), REPLACE it in
        // place (RemoveAt + Insert — the same converging CRDT-list op SupersedeOwnTeamRecordsAsync uses). This is
        // the backfill path: the founder re-emits an already-admitted member's record carrying its now-known
        // transport key. It is a legitimate self-supersession of the UNSIGNED transport field — the signed
        // admission payload + nonce are byte-identical (same RecordId), so the chain validity is preserved; only
        // the unsigned routing datum is updated. The replace converges to peers so they harvest the key too.
        var replaceIndex = _projection.Read(schema => schema.FindReplaceIndex(record));
        if (replaceIndex == RosterCrdtSchema.IdenticalRecord)
        {
            return;
        }

        await using (var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var existingRow = await ctx.Set<NodeRosterRecord>()
                .FirstOrDefaultAsync(r => r.Id == record.RecordId, ct).ConfigureAwait(false);
            if (existingRow is null)
            {
                ctx.Set<NodeRosterRecord>().Add(NodeRosterRecord.FromCrdtState(record));
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // Backfill: update only the unsigned transport + DM key fields on the durable row (cold-start
                // preserves them). C5: the DM public key rides the same record; backfill it the same way.
                var transportDiffers = !string.Equals(
                    existingRow.TransportPublicKeyB64Url ?? string.Empty,
                    record.TransportPublicKeyB64Url ?? string.Empty, StringComparison.Ordinal);
                var dmDiffers = !string.Equals(
                    existingRow.DmPublicKeyB64Url ?? string.Empty,
                    record.DmPublicKeyB64Url ?? string.Empty, StringComparison.Ordinal);
                // 2c-iii-b: the X-Wing key rides the same record; backfill it the same way (cold-start preserves it).
                var xwingDiffers = !string.Equals(
                    existingRow.XWingPublicKeyB64Url ?? string.Empty,
                    record.XWingPublicKeyB64Url ?? string.Empty, StringComparison.Ordinal);
                if (transportDiffers || dmDiffers || xwingDiffers)
                {
                    existingRow.TransportPublicKeyB64Url = record.TransportPublicKeyB64Url ?? string.Empty;
                    existingRow.DmPublicKeyB64Url = record.DmPublicKeyB64Url ?? string.Empty;
                    existingRow.XWingPublicKeyB64Url = record.XWingPublicKeyB64Url ?? string.Empty;
                    await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }
        }

        _projection.Mutate(schema => schema.ReplaceOrPush(replaceIndex, record));
    }

    // ── Adopt-time own-team supersession (BLOCKER-2: dual-genesis convergence poison) ─────────────────────

    /// <summary>
    /// SUPERSEDE this node's OWN-team roster records in the synced doctype after it has WIRE-ENROLLED into another
    /// team (cerebrum [2026-06-21] DECISIVE cross-machine verify — BLOCKER-2). On boot the bootstrap seeds THIS
    /// node's genesis self-admission AS a synced roster-CRDT record; on a single-team wire enrollment the node
    /// ADOPTS the admitter's team and supersedes its own genesis team. But the in-memory roster swap
    /// (<see cref="NodeTeamRoster.AdoptEnrollment"/>) never removed the node's own genesis from the SYNCED doctype,
    /// so after the admitter's genesis converged in, the doctype held TWO genesis records from differing parties →
    /// <see cref="MemberRoster.FromSyncedRecords"/>'s injection guard returned <c>Empty()</c> fail-closed → the
    /// roster never converged → the comms forge-proof <c>rosterBinding</c> had no members → inbound deltas were
    /// dropped → no convergence either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Legitimate self-supersession — NOT a guard weakening.</b> This removes ONLY records bearing this node's
    /// OWN (superseded) genesis team id — matched by a <c>TeamId == <paramref name="ownTeamId"/></c> equality
    /// filter (NOT a per-record signer/author predicate), which is harmless-or-beneficial: every such record is for
    /// the node's superseded team, irrelevant to the joined team's chain and dropped by
    /// <see cref="MemberRoster.FromSyncedRecords"/> anyway, and <paramref name="ownTeamId"/> is the node's own
    /// random/HKDF-derived team id (never attacker-steerable). The foreign-dual-genesis injection guard in
    /// <see cref="MemberRoster.FromSyncedRecords"/> is left completely intact: an attacker injecting a SECOND
    /// genesis for the JOINED team is still rejected (a different team id than <paramref name="ownTeamId"/>, signed
    /// by a party that is not this node). The fix makes legitimate self-supersession expressible without touching
    /// the trust anchor.
    /// </para>
    /// <para>
    /// <b>Mechanism — a converging CRDT removal + a durable purge.</b> Each own-team record is removed from the
    /// CRDT list (<see cref="ICrdtList.RemoveAt"/> emits a delete op that converges, so the admitter and every
    /// peer also drop this node's superseded genesis on the next sync round) AND deleted from the recoverable
    /// durable store (so a RESTART's cold-start hydration does not re-introduce it — the same restart-stability
    /// discipline #1291 F1 established for the genesis nonce). A local delta is signalled so the removal pushes
    /// promptly. Idempotent: re-running after the records are already gone removes nothing.
    /// </para>
    /// </remarks>
    /// <param name="ownTeamId">This node's OWN (pre-enrollment) genesis team id — the records to supersede.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of own-team records superseded.</returns>
    public async Task<int> SupersedeOwnTeamRecordsAsync(Guid ownTeamId, CancellationToken ct)
    {
        if (ownTeamId == Guid.Empty) return 0;
        var ownTeamIdString = ownTeamId.ToString("D");

        // (1) Remove own-team records from the CRDT list (back-to-front so indices stay valid as the list shrinks).
        //     RemoveAt emits a CRDT delete op → the removal converges to the admitter + every peer. Suppress the
        //     reconcile re-entry while we mutate (the durable purge below is the authoritative durable step).
        var removed = 0;
        _projection.Mutate(
            schema => removed = schema.RemoveWhere(
                record => string.Equals(record.TeamId, ownTeamIdString, StringComparison.Ordinal)),
            signalLocalDelta: false);

        // (2) Purge the same records from the recoverable durable store so a restart's hydration does not
        //     re-seed the superseded own-team genesis (restart-stability — #1291 F1 discipline).
        await using (var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var rows = await ctx.Set<NodeRosterRecord>()
                .Where(r => r.TeamId == ownTeamIdString)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (rows.Count > 0)
            {
                ctx.Set<NodeRosterRecord>().RemoveRange(rows);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation(
                "Roster CRDT superseded {Count} own-team record(s) (team '{TeamId}') after wire-enrollment — the "
                + "removal converges so peers drop this node's superseded genesis (BLOCKER-2: dual-genesis fix).",
                removed, ownTeamIdString);
            _projection.SignalLocalDeltaProduced();
        }
        return removed;
    }

    /// <summary>
    /// Retract stale genesis records that this install cryptographically proves it minted before its resolved
    /// genesis team changed. This is deliberately narrower than <see cref="SupersedeOwnTeamRecordsAsync"/>:
    /// startup has no enrollment transaction proving an entire old team is locally owned, so only a valid,
    /// self-signed genesis carrying the current founder party and public key may be selected.
    /// </summary>
    public async Task<int> ReconcileLocallyMintedGenesisAsync(CancellationToken ct)
    {
        if (_nodeRoster is null) return 0;
        if (!_nodeRoster.Current.ValidatesToGenesis(_verifier))
        {
            throw new InvalidOperationException(
                "Cannot reconcile durable genesis records because the live local trust anchor is invalid.");
        }

        var currentGenesis = _nodeRoster.Current.EnumerateAdmissions()
            .Single(record => record.Admission.IsGenesis);
        if (!currentGenesis.Admission.IsGenesis
            || !Guid.TryParse(currentGenesis.TeamId, out var currentTeamId)
            || currentTeamId == Guid.Empty)
        {
            throw new ArgumentException("The current trust anchor must be a non-empty genesis admission.",
                "currentGenesis");
        }

        var currentKey = currentGenesis.PublicKey.ToBase64Url();
        var staleRecordIds = Snapshot()
            .Where(record => IsLocallyMintedStaleGenesis(
                record, currentTeamId, currentGenesis.PartyId, currentGenesis.PublicKey, currentKey))
            .Select(record => record.RecordId)
            .ToHashSet(StringComparer.Ordinal);

        if (staleRecordIds.Count == 0) return 0;

        var removed = 0;
        _projection.Mutate(
            schema => removed = schema.RemoveWhere(record => staleRecordIds.Contains(record.RecordId)),
            signalLocalDelta: false);

        await using (var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var rows = await ctx.Set<NodeRosterRecord>()
                .Where(row => staleRecordIds.Contains(row.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (rows.Count > 0)
            {
                ctx.Set<NodeRosterRecord>().RemoveRange(rows);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (removed > 0)
        {
            _logger.LogWarning(
                "Roster CRDT boot reconciliation superseded {Count} stale genesis record(s) cryptographically "
                + "proven to be minted by local founder '{Party}'; resolved genesis team is '{TeamId}'.",
                removed, currentGenesis.PartyId, currentGenesis.TeamId);
            _projection.SignalLocalDeltaProduced();
        }

        return removed;
    }

    private bool IsLocallyMintedStaleGenesis(
        RosterRecordCrdtState record,
        Guid currentTeamId,
        string currentPartyId,
        PrincipalId currentPublicKey,
        string currentPublicKeyText)
    {
        if (record.Kind != RosterRecordKind.Admission
            || !record.IsGenesis
            || !Guid.TryParse(record.TeamId, out var recordTeamId)
            || recordTeamId == currentTeamId
            || !string.Equals(record.PartyId, currentPartyId, StringComparison.Ordinal)
            || !string.Equals(record.PublicKeyB64Url, currentPublicKeyText, StringComparison.Ordinal)
            || !string.Equals(record.AdmittedByPartyId, currentPartyId, StringComparison.Ordinal)
            || !string.Equals(record.AdmittedByPublicKey, currentPublicKeyText, StringComparison.Ordinal))
        {
            return false;
        }

        var admission = record.ToAdmissionOrNull();
        return admission is not null
            && RosterSigning.VerifyAdmission(
                recordTeamId, admission.PartyId, currentPublicKey, admission.Admission, _verifier);
    }

    // ── IDeltaProducer — outbound (send local records the peer hasn't seen) ──────────────────────────────

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

    // ── IDeltaSink — inbound (merge a peer's delta, then reconcile + rebuild the live roster) ────────────

    /// <inheritdoc />
    public async ValueTask ApplyInboundDeltaAsync(
        string documentId,
        ulong opSequence,
        ReadOnlyMemory<byte> delta,
        CancellationToken ct)
    {
        var result = _projection.ApplyDelta(documentId, opSequence, delta);
        if (!result.Succeeded)
        {
            _logger.LogWarning(result.Error,
                "Roster CRDT bridge dropped inbound delta {DocumentId} op {OpSequence}: {Reason}",
                documentId, opSequence, result.Error?.Message);
        }
        await _projection.DrainPendingReconcilesAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    // ── Merge → durable reconcile + live-roster rebuild ─────────────────────────────────────────────────

    /// <summary>
    /// Await every reconcile the live <see cref="OnListChanged"/> trigger has spawned so far. Test-only
    /// deterministic join point (the production daemon path is fire-and-forget).
    /// </summary>
    internal Task DrainPendingReconcilesAsync() => _projection.DrainPendingReconcilesAsync();

    /// <summary>
    /// Reconcile the converged record list into the durable store AND rebuild the live <see cref="NodeTeamRoster"/>
    /// from the (re-validated) synced records. Append-only insert into the durable store (idempotent on RecordId).
    /// The live-roster rebuild is the TRUST ANCHOR: <see cref="MemberRoster.FromSyncedRecords"/> drops any forged /
    /// unsigned / wrong-signer / orphan / second-genesis record fail-closed, so a bogus delta cannot inject a
    /// member.
    /// </summary>
    public Task ReconcileAsync(CancellationToken ct) => _projection.ReconcileAsync(ct);

    private async Task ReconcileSchemaAsync(CancellationToken ct)
    {
            IReadOnlyList<RosterRecordCrdtState> snapshot = Snapshot();
            if (snapshot.Count == 0)
            {
                await ReconcileRefusalAuditAsync(null, snapshot, null, [], ct).ConfigureAwait(false);
                return;
            }

            try
            {
                var inboundRefusals = new List<RebuildRefusal>();
                // (1) Durable reconcile — insert any record not already present (append-only, idempotent on id);
                //     REFRESH the unsigned transport field on an already-present row whose carried key changed (the
                //     INFO-2 backfill replace — same RecordId/signed payload, the transport key was filled in). This
                //     keeps cold-start hydration consistent with the converged CRDT snapshot.
                await using (var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
                {
                    var existing = await ctx.Set<NodeRosterRecord>()
                        .ToDictionaryAsync(r => r.Id, StringComparer.Ordinal, ct)
                        .ConfigureAwait(false);

                    // Durable rows are evidence for this fold, never fresh inbound refusal targets.
                    var reader = new VerifiedTenantRosterReader(_contextFactory, _verifier);
                    foreach (var tenant in existing.Values.Select(r => r.TeamId).Distinct(StringComparer.Ordinal))
                        await reader.ReadForRebuildAsync(new TenantId(tenant), ct).ConfigureAwait(false);

                    var toInsert = new List<NodeRosterRecord>();
                    var refused = await VerifyBeforeInsertAsync(snapshot, existing, ct).ConfigureAwait(false);
                    inboundRefusals.AddRange(refused.Values);
                    var changed = false;
                    foreach (var s in snapshot)
                    {
                        if (existing.TryGetValue(s.RecordId, out var row))
                        {
                            // Already present — refresh the UNSIGNED-by-association routing/key fields (transport +
                            // X-Wing) if the converged record carries a value the durable row lacks (or differs). The
                            // signed fields (incl. the C5 DM key) are byte-identical (same RecordId), so only the
                            // unsigned fields can change via a backfill re-emit.
                            var carried = s.TransportPublicKeyB64Url ?? string.Empty;
                            if (!string.Equals(row.TransportPublicKeyB64Url ?? string.Empty, carried, StringComparison.Ordinal)
                                && carried.Length > 0)
                            {
                                row.TransportPublicKeyB64Url = carried;
                                changed = true;
                            }
                            // 2c-iii-b — refresh the carried X-Wing key the same way (a recipient's record re-emits
                            // carrying its now-derived X-Wing key → it becomes X-Wing-capable on every converged node).
                            var carriedXWing = s.XWingPublicKeyB64Url ?? string.Empty;
                            if (!string.Equals(row.XWingPublicKeyB64Url ?? string.Empty, carriedXWing, StringComparison.Ordinal)
                                && carriedXWing.Length > 0)
                            {
                                row.XWingPublicKeyB64Url = carriedXWing;
                                changed = true;
                            }
                            continue;
                        }
                        if (refused.ContainsKey(s.RecordId)) continue;
                        var inserted = NodeRosterRecord.FromCrdtState(s);
                        toInsert.Add(inserted);
                        existing.Add(s.RecordId, inserted);
                    }
                    if (toInsert.Count > 0)
                    {
                        ctx.Set<NodeRosterRecord>().AddRange(toInsert);
                        changed = true;
                    }
                    if (changed)
                    {
                        try
                        {
                            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await ReportRebuildFailureAsync(ex, "roster.insert.failed", ct).ConfigureAwait(false);
                            return;
                        }
                        _logger.LogDebug(
                            "Roster CRDT reconcile inserted {Count} new record(s) + refreshed carried transport keys.",
                            toInsert.Count);
                    }
                }

                // (2) Live-roster rebuild — THE TRUST ANCHOR. Validate every record to genesis; drop forgeries.
                var anchor = _nodeRoster is null ? null : await VerifiedTenantRosterReader.ReadGenesisAsync(
                    _contextFactory, _nodeRoster.Current.TeamId, _verifier, ct).ConfigureAwait(false);
                var rejectedIds = inboundRefusals.Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal);
                var acceptedSnapshot = snapshot.Where(s => !rejectedIds.Contains(s.RecordId)).ToArray();
                var adopted = TryRebuildLiveRoster(acceptedSnapshot, anchor, inboundRefusals);
                await ReconcileRefusalAuditAsync(adopted, acceptedSnapshot, anchor, inboundRefusals, ct).ConfigureAwait(false);

                // (3) Ticket 290 — fold the administrator-authority log back onto the live roster. A revocation
                //     CONVERGED from a peer never passed through a local revocation authority, so this is its
                //     removal leg; and for a locally-originated revocation whose removal leg failed after the
                //     roster leg committed, this is the recorded repair on the next fold.
                await ReconcileAdministratorRemovalsAsync(ct).ConfigureAwait(false);
                await ClearRebuildFailureAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or VerifiedTenantRosterRefusedException)
            {
                await ReportRebuildFailureAsync(ex, "roster.rebuild.durable_verification_failed", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ReportRebuildFailureAsync(ex, "roster.rebuild.failed", ct).ConfigureAwait(false);
            }
    }

    private async Task ReportRebuildFailureAsync(Exception ex, string code, CancellationToken ct)
    {
        var cause = ex.GetBaseException();
        var detail = cause switch
        {
            VerifiedTenantRosterRefusedException refused => refused.Message,
            JsonException => "The durable roster contains malformed JSON.",
            _ => code == "roster.insert.failed" ? "The roster insert failed; the node has not converged."
                : "The roster rebuild failed; the retained roster is not a converged trust reading."
        };
        var report = new AuthorizationRefusal(code, "Roster convergence refused", detail,
            "Repair the roster store or verifier fault and retry the rebuild; preserve the durable log as evidence.",
            JsonSerializer.Serialize(new { reason = cause.Message, exception = cause.GetType().FullName }));
        var evidence = _nodeRoster?.Current.EnumerateAdmissions().FirstOrDefault();
        var refusal = new RebuildRefusal("rebuild", report, "roster.rebuild", ActorId.System,
            evidence is null ? TenantId.System : new TenantId(evidence.TeamId),
            evidence?.Admission.IssuedAt ?? DateTimeOffset.UnixEpoch);
        Volatile.Write(ref _rebuildFailure, refusal);
        var audit = _refusalAudit?.Invoke();
        if (_auditedRebuildFailure?.Report == report || audit is null) return;
        if (await audit.RecordAsync(report, refusal.Permission, refusal.Actor, refusal.Tenant,
                refusal.IssuedAt, decision: null, ct).ConfigureAwait(false) is not null)
            _auditedRebuildFailure = refusal;
    }

    private async Task ClearRebuildFailureAsync(CancellationToken ct)
    {
        if (_auditedRebuildFailure is { } previous && _refusalAudit?.Invoke() is { } audit)
            await audit.RecordClearedAsync(previous.Report, previous.Permission, previous.Actor,
                previous.Tenant, previous.IssuedAt, ct).ConfigureAwait(false);
        _auditedRebuildFailure = null;
        Volatile.Write(ref _rebuildFailure, null);
    }

    /// <summary>
    /// Append an administrator removal for every party the administrator-authority log still folds as usable
    /// but the LIVE roster no longer carries (ticket 290). The live roster is the one the trust anchor just
    /// adopted, so a rebuild that was REJECTED (the non-bricking guard keeps the local roster) removes nobody.
    /// </summary>
    private async Task ReconcileAdministratorRemovalsAsync(CancellationToken ct)
    {
        if (_nodeRoster is null || _administrators?.Invoke() is not { } administrators) return;

        var live = _nodeRoster.Current;
        if (live.Members.Count == 0) return;
        var team = live.TeamId.ToString("D");
        var liveParties = new HashSet<string>(
            live.Members.Select(member => member.PartyId), StringComparer.Ordinal);

        var established = (await administrators.EstablishedPartiesAsync(ct).ConfigureAwait(false))
            .Where(entry => string.Equals(entry.TeamId, team, StringComparison.Ordinal))
            .ToArray();

        foreach (var (_, partyId) in established)
        {
            var key = team + "/" + partyId;
            if (liveParties.Contains(partyId))
            {
                _pendingAdministratorRemovals.TryRemove(key, out _);
                continue;
            }

            // Bounded retry: once the guard has refused this removal the attempt limit's worth of times with
            // the SAME established population, re-attempting adds nothing - the successor's admission or
            // grant has not converged. The item stays in the fold's report as a refusal-to-converge
            // diagnostic and is re-attempted the moment the population changes (the next roster or grant
            // delta that establishes anyone), rather than looping silently on every reconcile.
            var seen = _pendingAdministratorRemovals.TryGetValue(key, out var pending) ? pending : default;
            if (seen.Attempts >= PendingAdministratorRemovalAttemptLimit
                && seen.EstablishedWhenTried == established.Length)
            {
                continue;
            }

            var result = await administrators.AppendConvergedRevocationRemovalAsync(
                team, partyId, RosterRevocationRemovalReason, ct).ConfigureAwait(false);
            if (result.Applied)
            {
                _pendingAdministratorRemovals.TryRemove(key, out _);
                _logger.LogWarning(
                    "Roster revocation of party '{Party}' in team {Team} wrote an administrator removal on the "
                    + "fold (converged, or repairing a removal leg that did not commit with its roster leg).",
                    partyId, team);
                continue;
            }

            var attempts = seen.EstablishedWhenTried == established.Length ? seen.Attempts + 1 : 1;
            _pendingAdministratorRemovals[key] = new PendingAdministratorRemoval(
                team, partyId, result.Code, attempts, established.Length);
            if (attempts >= PendingAdministratorRemovalAttemptLimit)
            {
                _logger.LogError(
                    "Roster revocation of party '{Party}' in team {Team} is REFUSING TO CONVERGE its "
                    + "administrator removal after {Attempts} folds: {Code}. The party is ejected from the "
                    + "roster and exercises no authority, but the administrator-authority log still carries "
                    + "them; the removal is re-attempted when an administrator is established or removed.",
                    partyId, team, attempts, result.Code);
            }
            else
            {
                _logger.LogWarning(
                    "Roster revocation of party '{Party}' in team {Team} could not yet write its administrator "
                    + "removal ({Code}); the successor's delta has not arrived. Pending, attempt {Attempts}.",
                    partyId, team, result.Code, attempts);
            }
        }
    }

    /// <summary>
    /// Rebuild the live <see cref="NodeTeamRoster"/> from the converged records, re-validating to genesis
    /// (fail-closed). Exposed for the local publish path + tests. If the records yield no trustworthy genesis
    /// (an empty rebuilt roster), the node KEEPS its local genesis-seeded roster — it does not adopt an empty
    /// one (non-bricking).
    /// </summary>
    public void RebuildLiveRoster(IReadOnlyList<RosterRecordCrdtState> snapshot) => TryRebuildLiveRoster(snapshot);

    private MemberRoster? TryRebuildLiveRoster(IReadOnlyList<RosterRecordCrdtState> snapshot, MemberAdmissionRecord? anchor = null, List<RebuildRefusal>? refusals = null)
    {
        if (_nodeRoster is null) return null; // minimal DI test — no live roster to push into.

        var admissions = new List<MemberAdmissionRecord>();
        var revocations = new List<MemberRevocationRecord>();
        // INFO-2 — harvest the carried team-scoped transport pubkeys keyed by party (the ≥3-node mesh fix). The
        // synced admission record now carries each member's transport key; a later admission for a party (the
        // backfill re-emit, or a re-admission) wins, so use the last carried key per party. These are RAW carried
        // values — NOT yet trust-validated; they are filtered to genesis-validated LIVE members below (the
        // by-association rule: a transport key is honored only for a party the SIGNED chain admitted).
        var carriedTransportByParty = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // 2c-iii-b + C5-X (X-Wing key-substitution DEK-leak fix; #1489) — the X-Wing key is NO LONGER harvested here
        // from the raw, last-carried-wins, unsigned snapshot field. That was the SAME hole C5 closed for the DM key: a
        // trusted roster writer could inject a 2nd same-party record {PartyId:"alice", XWingKey:Mallory's} with a fresh
        // nonce (distinct RecordId ⇒ not deduped) — and because the X-Wing key was NOT in the signed admission bytes,
        // that record could even carry Alice's REAL valid signature, so last-write-wins set alice's X-Wing key to
        // Mallory's ⇒ a sender boxed the tenant DEK to Mallory ⇒ Mallory decapsulates ⇒ DEK LEAKED. The X-Wing key is
        // now bound INTO the signed admission, so the AUTHORITATIVE X-Wing key is harvested BELOW from the
        // chain-VALIDATED rebuilt roster (MemberRoster.XWingPublicKeyOf) — only the key the admitter actually signed
        // for that party survives; a substituted-X-Wing record fails signature verification in FromSyncedRecords and is
        // dropped, so it never even becomes a live member.
        foreach (var s in snapshot)
        {
            switch (s.Kind)
            {
                case RosterRecordKind.Admission:
                    var a = s.ToAdmissionOrNull();
                    if (a is not null)
                    {
                        admissions.Add(a);
                        if (a.TransportPublicKey is { Length: > 0 })
                        {
                            carriedTransportByParty[a.PartyId] = a.TransportPublicKey;
                        }
                        // C5-X (X-Wing key-substitution DEK-leak fix; #1489) — the X-Wing key is NO LONGER harvested
                        // here from the raw last-carried-wins snapshot field (the leak hole — see the comment above the
                        // loop). The authoritative X-Wing key is harvested below from the chain-validated rebuilt roster.
                        // C5 (DM key-substitution fix; sec-eng deep-review of PR #1326) — the DM key is NO LONGER
                        // harvested here from the raw, last-carried-wins, unsigned snapshot field (that was the hole:
                        // a trusted member could WRITE {PartyId:"alice", DmKey:Mallory's} and override Alice's real
                        // key with only a "is alice a member" filter). The DM key is now bound INTO the signed
                        // admission, so the AUTHORITATIVE DM key is harvested BELOW from the chain-VALIDATED rebuilt
                        // roster (MemberRoster.DmPublicKeyOf) — only the key the admitter actually signed for that
                        // party survives. A substituted DM-key record fails signature verification in
                        // FromSyncedRecords and is dropped, so it never even becomes a live member.
                    }
                    break;
                case RosterRecordKind.Revocation:
                    var r = s.ToRevocationOrNull();
                    if (r is not null) revocations.Add(r);
                    break;
            }
        }

        // THE TRUST ANCHOR: rebuild + validate. Forged/unsigned/orphan records are dropped here.
        var selected = anchor is null ? admissions : admissions.Where(a => !a.Admission.IsGenesis
            || a.Admission.Signature == anchor.Admission.Signature);
        var rebuilt = MemberRoster.FromSyncedRecords(selected, revocations, _verifier);

        // Only the accepted admission may carry a live party's transport key. A dropped chain can name
        // the same party, but that does not let its unsigned routing field replace the valid binding.
        var accepted = rebuilt.EnumerateAdmissions().Select(a => a.Admission.Signature).ToHashSet(StringComparer.Ordinal);
        carriedTransportByParty.Clear();
        foreach (var admission in admissions.Where(a => accepted.Contains(a.Admission.Signature)
                     && RosterSigning.VerifyAdmission(rebuilt.TeamId, a.PartyId, a.PublicKey, a.Admission, _verifier)))
            if (admission.TransportPublicKey is { Length: > 0 } key)
                carriedTransportByParty[admission.PartyId] = key;

        // If the rebuild has no trustworthy genesis (Members empty), DO NOT adopt it — keep the local roster.
        // This covers a peer who shares no genesis with us (never enrolled into one team): we never let a
        // foreign or empty roster brick our own membership.
        if (rebuilt.Members.Count == 0)
        {
            ReportRejectedGenesis(snapshot, refusals);
            return null;
        }

        // gap #2 — OWN-MEMBERSHIP GUARD (the app/dev-seed cold-start crash, bug-1333). A NON-EMPTY rebuilt roster
        // is only adoptable if THIS NODE IS A MEMBER OF IT — i.e. this node's OWN signing key is bound to some party in
        // the rebuilt roster (it founded this team, or it was enrolled into it). Cold-start hydration
        // (RosterSyncBootstrapHostedService) pushes the DURABLE roster_records onto the CRDT list BEFORE the live
        // seed-derived genesis is published as a synced record — so a durable store left over from a PRIOR root seed
        // (a changed HARBORLINE_DEV_SEED_HEX, or a data dir reused across distinct seeds) rebuilds a roster the CURRENT
        // node is NOT a member of (every member is bound to the OLD seed's keys). Adopting it would replace
        // NodeTeamRoster.Current with a genesis whose bound key ≠ this node's comms signing key (derived from the
        // CURRENT seed) → HostedCommsApiEndpoint's forge-proof consistency assertion fails-closed and the host crashes
        // ("Comms author binding is inconsistent … gap #2"). The STANDALONE node never hit this: its store was
        // seed-consistent, so it WAS a member of the hydrated roster.
        //
        // FAIL-SAFE (mirrors the dual-genesis guard above): if the rebuilt roster does NOT bind this node's own key to
        // any member, DO NOT adopt — keep the local seed-derived roster. This is the precise, security-sound
        // discriminator between the two cases that BOTH adopt a foreign-genesis roster via hydration:
        //   * LEGITIMATE enrollment (a joiner that ran NodeWireEnrollmentClient.EnrollAsync → AdoptEnrollment + own-team
        //     supersession, leaving ONLY the joined team's records durable): the joiner re-uses its OWN genesis/comms
        //     signer as its enrolled principal (Program.cs: principalSigner = genesisSigner.Signer; bug-1332 — the
        //     party id is STABLE across the join), so its OWN key IS bound to its member entry in the joined roster →
        //     ADOPT (correct — the comms author is then this node's own member entry, the binding holds).
        //   * STALE/FOREIGN durable roster (bug-1333): no member is bound to this node's CURRENT key → REFUSE.
        // The node's own signing key is the key bound to its boot genesis party in NodeTeamRoster.Current, which (at
        // the first hydration rebuild) is still the in-memory seed-derived genesis seeded in Program.cs — exactly the
        // binding the comms signer matches.
        var liveGenesisParty = _nodeRoster.Current.GenesisPartyId;
        var ownSigningKey = string.IsNullOrEmpty(liveGenesisParty)
            ? (PrincipalId?)null
            : _nodeRoster.Current.PublicKeyOf(liveGenesisParty);

        var nodeIsMemberOfRebuilt = ownSigningKey is not null
            && rebuilt.Members.Any(m =>
            {
                var bound = rebuilt.PublicKeyOf(m.PartyId);
                return bound is not null && bound.Value.Equals(ownSigningKey.Value);
            });

        if (!nodeIsMemberOfRebuilt)
        {
            ReportRejectedGenesis(snapshot, refusals);
            return null;
        }

        // INFO-2 (≥3-node mesh) — the transport-trust set is now ROSTER-DERIVED. Filter the carried transport keys
        // to the parties the genesis-rooted rebuild VALIDATED as LIVE members (the by-association trust rule: a
        // transport key is honored ONLY for a party the SIGNED chain admitted — a forged/orphan key never reaches
        // here because its party is not in rebuilt.Members). AdoptSyncedRoster REBUILDS its transport map from this
        // set, so every converged member derives the SAME full transport-trust set from the SAME converged roster —
        // two peers admitted at different times (B↔C) now trust each other directly, not only via the admitting
        // hub. A revoked member is absent from the live set, so its key is not rebuilt (gap-#3 revocation-drop
        // SUBSUMED by rebuild-from-live). Single-user trust never regresses (the own-subkey floor is outside this
        // map; a fresh node's map is empty). The own-subkey floor stays the never-brick guarantee.
        var liveMemberParties = new HashSet<string>(
            rebuilt.Members.Select(m => m.PartyId), StringComparer.Ordinal);
        var transportForLiveMembers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (party, key) in carriedTransportByParty)
        {
            if (liveMemberParties.Contains(party))
            {
                transportForLiveMembers[party] = key;
            }
        }
        // C5 (DM key-substitution fix) — harvest each member's DM public key from the chain-VALIDATED rebuilt roster
        // (the key the admission SIGNATURE attests for that party), NOT from the raw last-carried-wins snapshot. This
        // is what closes the substitution MITM: MemberRoster.DmPublicKeyOf returns ONLY the DM key bound into the
        // signed admission for that party — a record substituting a peer's DM key fails signature verification in
        // FromSyncedRecords (so its party may not even be live), and even if the party is live via its OWN real
        // record, the harvested key is the SIGNED one (the attacker's unsigned override never reaches the resolver).
        var dmForLiveMembers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var m in rebuilt.Members)
        {
            var signedDmKey = rebuilt.DmPublicKeyOf(m.PartyId);
            if (signedDmKey is { Length: > 0 })
            {
                dmForLiveMembers[m.PartyId] = signedDmKey;
            }
        }
        // 2c-iii-b + C5-X (X-Wing key-substitution DEK-leak fix; #1489) — harvest each member's X-Wing public key from
        // the chain-VALIDATED rebuilt roster (the key the admission SIGNATURE attests for that party), NOT from the raw
        // last-carried-wins snapshot. This is what closes the DEK-leak substitution: MemberRoster.XWingPublicKeyOf
        // returns ONLY the X-Wing key bound into the signed admission for that party — a record substituting a peer's
        // X-Wing key fails signature verification in FromSyncedRecords (so its party may not even be live), and even if
        // the party is live via its OWN real record, the harvested key is the SIGNED one (the attacker's override never
        // reaches the resolver). This is the harvest source PR-B's suite-#3 capability gate consumes
        // (NodeTeamRoster.XWingPublicKeyOf). A revoked member is absent from rebuilt.Members → its key is not rebuilt.
        var xwingForLiveMembers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var m in rebuilt.Members)
        {
            var signedXWingKey = rebuilt.XWingPublicKeyOf(m.PartyId);
            if (signedXWingKey is { Length: > 0 })
            {
                xwingForLiveMembers[m.PartyId] = signedXWingKey;
            }
        }
        _nodeRoster.AdoptSyncedRoster(rebuilt, transportForLiveMembers, dmForLiveMembers, xwingForLiveMembers);
        return rebuilt;
    }

    // Reconstruct authority from durable evidence on every fold, including before cold-start hydration.
    // Relax new admissions from their signers outward; the CRDT list order is not causal order.
    private async Task<Dictionary<string, RebuildRefusal>> VerifyBeforeInsertAsync(
        IReadOnlyList<RosterRecordCrdtState> snapshot, Dictionary<string, NodeRosterRecord> existing,
        CancellationToken ct)
    {
        var refused = new Dictionary<string, RebuildRefusal>(StringComparer.Ordinal);
        foreach (var tenant in snapshot.Where(s => !existing.ContainsKey(s.RecordId)).GroupBy(s => s.TeamId))
        {
            var pending = tenant.DistinctBy(s => s.RecordId).OrderBy(s => s.Kind)
                .ThenBy(s => s.IssuedAtIso, StringComparer.Ordinal).ThenBy(s => s.NonceGuid, StringComparer.Ordinal).ToList();
            var evidence = existing.Values.Where(r => r.TeamId == tenant.Key).Select(NodeRosterRecord.ToCrdtState).ToList();
            var anchor = Guid.TryParse(tenant.Key, out var tenantId)
                ? await VerifiedTenantRosterReader.ReadGenesisAsync(_contextFactory, tenantId, _verifier, ct).ConfigureAwait(false)
                : null;
            // No append precedence exists for simultaneous roots. Keep the existing unique-genesis rule.
            var competingRoots = anchor is null && pending.Count(s => s.IsGenesis && s.ToAdmissionOrNull() is { } a
                && MemberRoster.FromSyncedRecords([a], [], _verifier).HasRootGrantHolder()) > 1;
            bool grew;
            do
            {
                grew = false;
                var remaining = new List<RosterRecordCrdtState>();
                foreach (var candidate in pending)
                {
                    var code = VerifyInboundRecord(candidate, evidence, tenant, anchor, competingRoots);
                    if (code is null)
                    {
                        evidence.Add(candidate);
                        anchor ??= candidate.IsGenesis ? candidate.ToAdmissionOrNull() : null;
                        grew = true;
                    }
                    else remaining.Add(candidate);
                }
                pending = remaining;
            } while (grew && pending.Count > 0);
            foreach (var candidate in pending)
            {
                var code = VerifyInboundRecord(candidate, evidence, tenant, anchor, competingRoots)!;
                var report = code == "roster.genesis.duplicate" ? GenesisRefusal(candidate).Report
                    : new AuthorizationRefusal(code, "Roster record refused",
                    "The record has no valid signature and eligible live signer in the durable genesis chain.",
                    "Supply a signed record rooted in the durable genesis with the required authority.",
                    JsonSerializer.Serialize(candidate));
                refused[candidate.RecordId] = new RebuildRefusal(candidate.RecordId, report,
                    candidate.Kind == RosterRecordKind.Revocation ? Permission.MembersRevoke : Permission.MembersAdmit,
                    new ActorId(candidate.AdmittedByPartyId), new TenantId(candidate.TeamId),
                    NodeRosterRecord.FromCrdtState(candidate).IssuedAtUtc);
            }
        }
        return refused;
    }

    private string? VerifyInboundRecord(RosterRecordCrdtState candidate, List<RosterRecordCrdtState> evidence,
        IEnumerable<RosterRecordCrdtState> incoming, MemberAdmissionRecord? anchor, bool competingRoots)
    {
        if (!Guid.TryParse(candidate.TeamId, out var tenant) || string.IsNullOrWhiteSpace(candidate.PartyId)
            || string.IsNullOrWhiteSpace(candidate.AdmittedByPartyId)) return "roster.record.malformed";
        var admission = candidate.ToAdmissionOrNull();
        var revocation = candidate.ToRevocationOrNull();
        var signatureValid = admission is not null
            ? RosterSigning.VerifyAdmission(tenant, admission.PartyId, admission.PublicKey, admission.Admission, _verifier)
            : revocation is not null && RosterSigning.VerifyRevocation(tenant, revocation.RevokedPartyId, revocation.Signed, _verifier);
        if (!signatureValid) return "roster.record.signature_invalid";
        var canonicalId = admission is not null ? RosterRecordCrdtState.FromAdmission(admission).RecordId
            : RosterRecordCrdtState.FromRevocation(revocation!).RecordId;
        if (candidate.RecordId != canonicalId) return "roster.record.malformed";
        if (admission?.Admission.IsGenesis == true)
        {
            if (anchor is not null || competingRoots) return "roster.genesis.duplicate";
            return MemberRoster.FromSyncedRecords([admission], [], _verifier).HasRootGrantHolder()
                ? null : "roster.record.chain_ineligible";
        }
        var admissions = evidence.Select(s => s.ToAdmissionOrNull()).OfType<MemberAdmissionRecord>()
            .Where(a => !a.Admission.IsGenesis || a.Admission.Signature == anchor?.Admission.Signature).ToList();
        if (admission is not null) admissions.Add(admission);
        var at = admission?.Admission.IssuedAt ?? revocation!.Signed.IssuedAt;
        var nonce = admission?.Admission.Nonce ?? revocation!.Signed.Nonce;
        // Retain the rebuild's signed ordering in this slice; ticket 295 slice 3 owns its time bound.
        var preceding = evidence.Concat(incoming).DistinctBy(s => s.RecordId)
            .Select(s => s.ToRevocationOrNull()).OfType<MemberRevocationRecord>()
            .Where(r => r.Signed.IssuedAt < at || (r.Signed.IssuedAt == at && r.Signed.Nonce.CompareTo(nonce) < 0));
        var chain = MemberRoster.FromSyncedRecords(admissions, preceding, _verifier);
        var permission = admission is not null ? Permission.MembersAdmit : Permission.MembersRevoke;
        if (!chain.Contains(candidate.AdmittedByPartyId)
            || chain.PublicKeyOf(candidate.AdmittedByPartyId)?.ToBase64Url() != candidate.AdmittedByPublicKey
            || chain.PermissionsOf(candidate.AdmittedByPartyId)?.Contains(permission) != true)
            return "roster.record.chain_ineligible";
        if (admission is not null && !chain.EnumerateAdmissions().Any(a =>
                a.PartyId == admission.PartyId && a.Admission.Signature == admission.Admission.Signature))
            return "roster.record.chain_ineligible";
        return null;
    }

    private sealed record RebuildRefusal(string RecordId, AuthorizationRefusal Report, string Permission,
        ActorId Actor, TenantId Tenant, DateTimeOffset IssuedAt);

    private async Task ReconcileRefusalAuditAsync(MemberRoster? adopted, IReadOnlyList<RosterRecordCrdtState> snapshot,
        MemberAdmissionRecord? anchor, List<RebuildRefusal> inboundRefusals, CancellationToken ct)
    {
        var audit = _refusalAudit?.Invoke();
        var refusals = (adopted?.RefusedRevocations ?? []).Select(refusal => new RebuildRefusal(
            RosterRecordCrdtState.FromRevocation(refusal.Revocation).RecordId, RefusalReport(refusal), Permission.MembersRevoke,
            new ActorId(refusal.Revocation.Signed.RevokedByPartyId), new TenantId(refusal.Revocation.TeamId),
            refusal.Revocation.Signed.IssuedAt)).ToList();
        refusals.AddRange(inboundRefusals);
        if (adopted is null)
            refusals.AddRange(_reportedRefusals.Values.Where(r => !r.Report.Code.StartsWith("roster.genesis.", StringComparison.Ordinal)
                && snapshot.Any(s => s.RecordId == r.RecordId)));
        if (anchor is not null)
            foreach (var candidate in snapshot.Where(s => s.Kind == RosterRecordKind.Admission && s.IsGenesis
                         && (s.TeamId != anchor.TeamId || s.SignatureB64Url != anchor.Admission.Signature)
                         && !IsLocallyMintedStaleGenesis(s, _nodeRoster!.Current.TeamId,
                             anchor.PartyId, anchor.PublicKey, anchor.PublicKey.ToBase64Url())))
                refusals.Add(GenesisRefusal(candidate));
        var current = refusals.GroupBy(
            refusal => (refusal.RecordId, refusal.Report.Code))
            .ToDictionary(group => group.Key, group => group.First());
        var previousReports = Volatile.Read(ref _refusalReports);
        Volatile.Write(ref _refusalReports, current.Values.Select(r => r.Report).ToArray());
        foreach (var (key, refusal) in current)
        {
            if (!previousReports.Any(r => r.Code == refusal.Report.Code && r.Diagnostic == refusal.Report.Diagnostic))
                _logger.LogWarning("Roster rebuild refused record {Record}: {Code}. {Remedy}",
                refusal.RecordId, refusal.Report.Code, refusal.Report.Remediation);
            if (_reportedRefusals.ContainsKey(key) || audit is null) continue;
            await audit.RecordAsync(refusal.Report, refusal.Permission, refusal.Actor, refusal.Tenant,
                refusal.IssuedAt, decision: null, ct).ConfigureAwait(false);
            _reportedRefusals.Add(key, refusal);
        }
        foreach (var (key, refusal) in _reportedRefusals)
        {
            if (current.ContainsKey(key)) continue;
            if (audit is not null) await audit.RecordClearedAsync(refusal.Report, refusal.Permission, refusal.Actor, refusal.Tenant,
                refusal.IssuedAt, ct).ConfigureAwait(false);
        }
        if (audit is not null) _reportedRefusals = current;
    }

    private void ReportRejectedGenesis(IReadOnlyList<RosterRecordCrdtState> snapshot, List<RebuildRefusal>? refusals)
    {
        if (refusals is null) return; // Direct rebuild is a test seam; production reconciles await the audit.
        var localGenesis = _nodeRoster?.Current.EnumerateAdmissions().SingleOrDefault(a => a.Admission.IsGenesis);
        foreach (var candidate in snapshot.Where(s => s.Kind == RosterRecordKind.Admission && s.IsGenesis
                     && s.SignatureB64Url != localGenesis?.Admission.Signature))
            refusals.Add(GenesisRefusal(candidate));
    }

    private RebuildRefusal GenesisRefusal(RosterRecordCrdtState candidate)
    {
        var foreign = _nodeRoster is not null && candidate.TeamId != _nodeRoster.Current.TeamId.ToString("D");
        var report = new AuthorizationRefusal(
            foreign ? "roster.genesis.foreign_tenant" : "roster.genesis.duplicate", "Roster genesis refused",
            foreign ? "The candidate belongs to a foreign tenant; the local roster is retained."
                : "A duplicate genesis candidate conflicts with this tenant's established chain.",
            foreign ? "Use the data directory and tenant configuration belonging to this install, or enrol through a current member of the intended tenant; do not overwrite the roster log."
                : "A duplicate may be a restart/bug artifact or an injection attempt. " +
                  "Remove the duplicate candidate; keep the chain rooted in the durable genesis. " + GenesisStartupMessages.InvalidLog,
            JsonSerializer.Serialize(candidate));
        return new RebuildRefusal(candidate.RecordId, report, Permission.MembersAdmit,
            new ActorId(candidate.AdmittedByPartyId), new TenantId(candidate.TeamId),
            NodeRosterRecord.FromCrdtState(candidate).IssuedAtUtc);
    }

    private static AuthorizationRefusal RefusalReport(RosterRevocationRefusal refusal) =>
        new(refusal.Code, "Roster revocation refused", "The signed and live authority floor would be lost.",
            "Admit a signed successor that holds the floor live.", JsonSerializer.Serialize(refusal));

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _projection.DisposeAsync();
}

internal sealed class RosterCrdtSchema : ICrdtProjectionSchema
{
    internal const int IdenticalRecord = -2;
    private const string ListName = "records";

    private readonly Func<CancellationToken, Task> _reconcile;
    private ICrdtList? _records;
    private EventHandler<CrdtListChangedEventArgs>? _changedHandler;
    private Action<CrdtProjectionChange>? _changed;
    private int _suppressChanges;
    private bool _changedWhileSuppressed;

    public RosterCrdtSchema(Func<CancellationToken, Task> reconcile)
    {
        _reconcile = reconcile;
    }

    public string DocumentId => RosterCrdtProjection.DocumentId;

    public int Count => Records.Count;

    public void Bind(ICrdtDocument document, Action<CrdtProjectionChange> changed)
    {
        _records = document.GetList(ListName);
        _changed = changed;
        _changedHandler = (_, args) => OnChanged(args.Index);
        _records.Changed += _changedHandler;
    }

    public void Unbind()
    {
        if (_records is not null && _changedHandler is not null)
        {
            _records.Changed -= _changedHandler;
        }
    }

    public ValueTask ReconcileAsync(CrdtProjectionChange change, CancellationToken ct) =>
        new(_reconcile(ct));

    public IReadOnlyList<RosterRecordCrdtState> Snapshot()
    {
        var list = new List<RosterRecordCrdtState>(Records.Count);
        for (var index = 0; index < Records.Count; index++)
        {
            var record = Records.Get<RosterRecordCrdtState>(index);
            if (record is not null)
            {
                list.Add(record);
            }
        }
        return list;
    }

    public int FindReplaceIndex(RosterRecordCrdtState record)
    {
        for (var index = 0; index < Records.Count; index++)
        {
            var existing = Records.Get<RosterRecordCrdtState>(index);
            if (existing is null
                || !string.Equals(existing.RecordId, record.RecordId, StringComparison.Ordinal))
            {
                continue;
            }

            var transportSame = string.Equals(
                existing.TransportPublicKeyB64Url ?? string.Empty,
                record.TransportPublicKeyB64Url ?? string.Empty,
                StringComparison.Ordinal);
            var dmSame = string.Equals(
                existing.DmPublicKeyB64Url ?? string.Empty,
                record.DmPublicKeyB64Url ?? string.Empty,
                StringComparison.Ordinal);
            var xwingSame = string.Equals(
                existing.XWingPublicKeyB64Url ?? string.Empty,
                record.XWingPublicKeyB64Url ?? string.Empty,
                StringComparison.Ordinal);
            return transportSame && dmSame && xwingSame ? IdenticalRecord : index;
        }

        return -1;
    }

    public void PushMany(IEnumerable<RosterRecordCrdtState> records) =>
        RunComposite(() =>
        {
            foreach (var record in records)
            {
                Records.Push(record);
            }
        });

    public void ReplaceOrPush(int replaceIndex, RosterRecordCrdtState record)
    {
        if (replaceIndex < 0)
        {
            Records.Push(record);
            return;
        }

        RunComposite(() =>
        {
            if (replaceIndex < Records.Count && Records.RemoveAt(replaceIndex))
            {
                Records.Insert(replaceIndex, record);
            }
            else
            {
                Records.Push(record);
            }
        });
    }

    public int RemoveWhere(Func<RosterRecordCrdtState, bool> predicate)
    {
        var removed = 0;
        RunComposite(() =>
        {
            for (var index = Records.Count - 1; index >= 0; index--)
            {
                var record = Records.Get<RosterRecordCrdtState>(index);
                if (record is not null && predicate(record) && Records.RemoveAt(index))
                {
                    removed++;
                }
            }
        });
        return removed;
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

    private void RunComposite(Action mutation)
    {
        _suppressChanges++;
        try
        {
            mutation();
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

    private ICrdtList Records =>
        _records ?? throw new InvalidOperationException("The roster schema is not bound to a CRDT document.");
}
