using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Startup hook that registers the trust-roster <see cref="RosterCrdtProjection"/> on the install-level
/// <see cref="IDeltaRouter"/> as a synced doctype (after contacts + comms), seeds the local genesis
/// self-admission as a SYNCED record, and runs cold-start hydration — so the CRDT roster list carries the
/// full existing record log before the gossip daemon ships any delta. The roster-sync analogue of
/// <see cref="CommsSyncBootstrapHostedService"/> (production-wiring gap #1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive router registration.</b> Contacts is registered first (default) by
/// <see cref="ContactSyncBootstrapHostedService"/>, comms second by <see cref="CommsSyncBootstrapHostedService"/>;
/// this registers roster as the third — one more <c>IDeltaRouter.Register</c> call (the #1265 seam). The router
/// routes by the <c>"roster"</c> document id to this projection.
/// </para>
/// <para>
/// <b>Genesis becomes a SYNCED record.</b> The install-level <see cref="NodeTeamRoster"/> is seeded at bootstrap
/// (Program.cs) with the genesis self-admission. This service emits that genesis admission AS a roster-sync
/// record (durable-first, then onto the CRDT list) so it propagates to peers — the genesis is the chain root a
/// peer needs to validate every later admission to. Idempotent: on a restart the record is already in the
/// durable store (hydration re-pushes it), so this re-publish inserts nothing. A fresh single-user node thus
/// still works (the genesis self-admission seeds the roster locally and is ready to sync the moment a peer
/// appears).
/// </para>
/// <para>
/// <b>Ordering.</b> Registered AFTER contacts + comms (so they keep their routes) and BEFORE
/// <see cref="MultiTeamBootstrapHostedService"/> (the per-team daemon bridge) — registration order guarantees
/// the router owns the roster route + the doc is hydrated by the time the daemon starts.
/// </para>
/// </remarks>
public sealed class RosterSyncBootstrapHostedService : IHostedService
{
    private readonly IDeltaRouter _router;
    private readonly RosterCrdtProjection _projection;
    private readonly NodeTeamRoster? _nodeRoster;
    private readonly NodeOwnTransportKey? _ownTransportKey;
    private readonly NodeOwnDmKey? _ownDmKey;
    private readonly ILogger<RosterSyncBootstrapHostedService> _logger;
    private readonly RosterAdmissionGrantBackfill? _grantBackfill;

    public RosterSyncBootstrapHostedService(
        IDeltaRouter router,
        RosterCrdtProjection projection,
        ILogger<RosterSyncBootstrapHostedService> logger,
        NodeTeamRoster? nodeRoster = null,
        NodeOwnTransportKey? ownTransportKey = null,
        NodeOwnDmKey? ownDmKey = null,
        RosterAdmissionGrantBackfill? grantBackfill = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nodeRoster = nodeRoster;
        _ownTransportKey = ownTransportKey;
        _ownDmKey = ownDmKey;
        _grantBackfill = grantBackfill;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_grantBackfill is not null)
            await _grantBackfill.RunAsync(cancellationToken).ConfigureAwait(false);
        // Register roster as a synced doctype on the install-level router (routed by the "roster" id).
        _router.Register(RosterCrdtProjection.DocumentId, _projection, _projection);
        _logger.LogInformation(
            "Registered roster doctype '{DocumentId}' on the delta router.", RosterCrdtProjection.DocumentId);

        // Cold-start hydration: seed the CRDT roster list from the recoverable local-node.db so the existing
        // record log replicates to a fresh peer on the first sync round.
        var hydrated = await _projection.HydrateFromStoreAsync(cancellationToken).ConfigureAwait(false);
        if (_projection.RebuildFailure is not null) return;
        _logger.LogInformation("Roster CRDT cold-start hydration complete ({Count} record(s)).", hydrated);

        // Publish the local GENESIS self-admission AS a synced record (idempotent — already-present after a
        // restart). The chain root a peer needs to validate every later admission. A node with no NodeTeamRoster
        // (a minimal DI test) skips this — nothing to seed.
        //
        // INFO-2 (≥3-node mesh) — STAMP each own-authored admission record with the right team-scoped TRANSPORT
        // key so the synced record carries it and every converging member harvests it (roster-derived trust):
        //   * the GENESIS self-admission carries the FOUNDER's OWN team subkey (NodeOwnTransportKey, derived in
        //     Program.cs the SAME way the per-team registrar's own-subkey floor is — HKDF(root, genesisTeamId));
        //   * an already-admitted PEER's record (this founder admitted them, so it holds their transport key in
        //     NodeTeamRoster.AdmittedPeerTransportKeys) is BACKFILLED with that key — PublishLocalAsync replaces
        //     the unsigned transport field in place (same RecordId/signed payload), and the replace converges so
        //     peers harvest it too. This makes a team formed BEFORE the upgrade mesh-correct after one boot of the
        //     upgraded founder, alongside new admissions which are mesh-correct from day one (WireEnrollmentAdmitter).
        if (_nodeRoster is not null)
        {
            var reconciled = await _projection.ReconcileLocallyMintedGenesisAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reconciled > 0)
            {
                _logger.LogWarning(
                    "Roster boot reconciliation removed {Count} stale locally-minted genesis record(s) before "
                    + "publishing the resolved genesis trust anchor.",
                    reconciled);
            }

            var genesisPartyId = _nodeRoster.Current.GenesisPartyId;
            var ownTransportKey = _ownTransportKey?.PublicKey; // null in a minimal DI test → empty field, no harm.
            var ownDmKey = _ownDmKey?.PublicKey;               // C5 — likewise null in a minimal DI test.
            var admittedPeerKeys = _nodeRoster.AdmittedPeerTransportKeys();
            var admittedPeerDmKeys = _nodeRoster.AdmittedPeerDmKeys();

            var records = _nodeRoster.Current.EnumerateAdmissions();
            var stamped = 0;
            var dmStamped = 0;
            foreach (var rec in records)
            {
                // The transport key for THIS record's party: the founder's own subkey for the genesis member; a
                // known admitted-peer key otherwise. Absent → null (the record carries no transport key — back-compat).
                byte[]? transportKey =
                    string.Equals(rec.PartyId, genesisPartyId, StringComparison.Ordinal)
                        ? ownTransportKey
                        : admittedPeerKeys.TryGetValue(rec.PartyId, out var k) ? k : null;
                // C5 — the DM PUBLIC key for THIS record's party, the SAME way: the founder's own DM key for the
                // genesis member; a known admitted-peer DM key otherwise. Absent → null (no DM key on the record).
                byte[]? dmKey =
                    string.Equals(rec.PartyId, genesisPartyId, StringComparison.Ordinal)
                        ? ownDmKey
                        : admittedPeerDmKeys.TryGetValue(rec.PartyId, out var dk) ? dk : null;
                if (transportKey is { Length: > 0 }) stamped++;
                if (dmKey is { Length: > 0 }) dmStamped++;
                await _projection.PublishLocalAsync(
                    RosterRecordCrdtState.FromAdmission(rec, transportKey, dmKey), cancellationToken)
                    .ConfigureAwait(false);
            }
            _logger.LogInformation(
                "Roster sync seeded {Count} local admission record(s) (genesis self-admission) onto the synced "
                + "doctype; {Stamped} carry a team-scoped transport key, {DmStamped} carry a team-scoped DM key "
                + "(INFO-2 ≥3-node mesh + C5 DM keys: genesis + backfilled peers).",
                records.Count, stamped, dmStamped);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
