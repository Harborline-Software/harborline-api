using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Durable explicit-tenant roster authority. Every row is loaded from <see cref="NodeLocalRosterDbContext"/>,
/// converted fail-closed, signature-checked, and rebuilt from the unique genesis before any live trust is returned.
/// </summary>
public sealed class VerifiedTenantRosterReader : IVerifiedTenantRosterReader
{
    private readonly IDbContextFactory<NodeLocalRosterDbContext> _contextFactory;
    private readonly IOperationVerifier _verifier;
    // 293 s3b2: no permission set rides a roster record, so the chain gates (admitter holds members:admit,
    // revoker holds members:revoke, no-escalation) read a party's authority from the host's grant view.
    // Absent → the fail-closed floor, where only the genesis chain root holds authority.
    private readonly IRosterAuthority? _authority;

    /// <summary>Construct the reader from the durable roster context and canonical operation verifier.</summary>
    public VerifiedTenantRosterReader(
        IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
        IOperationVerifier verifier,
        IRosterAuthority? authority = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _authority = authority;
    }

    /// <inheritdoc />
    public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) => ReadCoreAsync(team, false, ct);
    /// <summary>Verify the durable chain prefix at the signed grant's issue instant.</summary>
    public Task<MemberRoster> ReadAtAsync(TenantId team, DateTimeOffset at, CancellationToken ct) =>
        ReadCoreAsync(team, false, ct, at: at);

    internal Task<MemberRoster> ReadPartialAsync(TenantId team, PrincipalId derivedPrincipal, CancellationToken ct) => ReadCoreAsync(team, true, ct, derivedPrincipal);

    // A live fold already has its install identity. Preserve partial adoption's durable anchor and
    // orphan checks without re-running the boot-only proof that chooses that install identity.
    internal Task<MemberRoster> ReadForRebuildAsync(TenantId team, CancellationToken ct) =>
        ReadCoreAsync(team, true, ct, requireInstallAnchor: false);

    // The existing SQLite append log supplies precedence, not a peer-controlled issuance time or a shell value.
    // Boot also requires install-signed root evidence below; append order alone cannot authenticate an install.
    internal static async Task<MemberAdmissionRecord?> ReadGenesisAsync(
        IDbContextFactory<NodeLocalRosterDbContext> factory, Guid tenant, IOperationVerifier verifier, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var team = tenant.ToString("D");
        var roots = await db.RosterRecords.FromSql(
            $"SELECT * FROM roster_records WHERE team_id = {team} AND kind = 0 AND is_genesis = 1 ORDER BY rowid")
            .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in roots)
        {
            RequireCurrentWireFormat(row);
            var root = NodeRosterRecord.ToCrdtState(row).ToAdmissionOrNull();
            if (root is not null && MemberRoster.FromSyncedRecords([root], [], verifier).HasRootGrantHolder())
                return root;
        }
        return null;
    }

    private async Task<MemberRoster> ReadCoreAsync(TenantId team, bool partial, CancellationToken ct,
        PrincipalId? derivedPrincipal = null, bool requireInstallAnchor = true, DateTimeOffset? at = null)
    {
        if (team.IsSystemSentinel || string.IsNullOrWhiteSpace(team.Value) || !Guid.TryParse(team.Value, out var teamId))
        {
            throw Refuse(VerifiedTenantRosterRefusal.WrongTenant,
                "The requested tenant is not a roster-backed team identifier.");
        }

        var canonicalTeam = teamId.ToString("D");
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.RosterRecords.Where(row => row.TeamId == canonicalTeam);
        if (at is { } queryAt)
        {
            query = query.Where(row =>
                row.WireFormatVersion != RosterWireFormat.CurrentVersion
                || row.IssuedAtUtc <= queryAt
                || row.ReceivedAtUtc <= queryAt);
        }
        var rows = await query.AsNoTracking()
            .OrderBy(row => row.IssuedAtUtc)
            .ThenBy(row => row.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.FirstOrDefault(row => row.WireFormatVersion != RosterWireFormat.CurrentVersion)
            is { } unsupported)
            RequireCurrentWireFormat(unsupported);
        // SQLite cannot translate the cross-column DateTimeOffset subtraction through this model's
        // epoch-millisecond value converter. The query above is a safe temporal superset; apply only
        // the exact bounded comparison here, after the explicit-tenant predicate has authorized the rows.
        if (at is { } boundedAt)
            rows = rows.Where(row => NodeRosterRecord.BoundedOrderTime(
                row.IssuedAtUtc, row.ReceivedAtUtc) <= boundedAt).ToList();

        var orderTime = NodeRosterRecord.OrderTimes(rows);
        if (rows.Count == 0)
        {
            var hasAnotherTenant = await db.RosterRecords.AsNoTracking()
                .AnyAsync(ct)
                .ConfigureAwait(false);
            throw Refuse(
                hasAnotherTenant
                    ? VerifiedTenantRosterRefusal.WrongTenant
                    : VerifiedTenantRosterRefusal.MissingGenesis,
                hasAnotherTenant
                    ? "The durable roster contains no rows for the requested tenant."
                    : "The requested tenant has no durable genesis admission.");
        }

        var genesisCount = rows.Count(row =>
            row.Kind == (int)RosterRecordKind.Admission && row.IsGenesis);
        if (genesisCount == 0)
        {
            throw Refuse(VerifiedTenantRosterRefusal.MissingGenesis,
                "The requested tenant has no durable genesis admission.");
        }
        if (genesisCount != 1 && !partial)
        {
            throw Refuse(VerifiedTenantRosterRefusal.MultipleGenesis,
                "The requested tenant has multiple durable genesis admissions.");
        }

        var anchor = partial ? await ReadGenesisAsync(_contextFactory, teamId, _verifier, ct).ConfigureAwait(false) : null;
        var admissions = new List<MemberAdmissionRecord>();
        var revocations = new List<MemberRevocationRecord>();
        var attestations = new List<RosterReceiveAttestation>();
        foreach (var row in rows)
        {
            var state = NodeRosterRecord.ToCrdtState(row);
            var attestation = state.ReceiveAttestationOrNull();
            if (attestation is null || !Guid.TryParse(state.NonceGuid, out var recordNonce)
                || !RosterReceiveAttestationSigning.Verify(state.RecordId, recordNonce, attestation, _verifier))
                throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                    "A durable roster receipt has invalid signed evidence.");
            switch ((RosterRecordKind)row.Kind)
            {
                case RosterRecordKind.Admission:
                {
                    var admission = state.ToAdmissionOrNull();
                    if (admission is null || (partial && row.Id != RosterRecordCrdtState.FromAdmission(admission).RecordId) ||
                        !RosterSigning.VerifyAdmission(
                            teamId, admission.PartyId, admission.PublicKey, admission.Admission, _verifier))
                    {
                        // Ticket 296 already reports discarded duplicate roots, including malformed
                        // candidates. They must not prevent the established chain's revocations folding.
                        if (!requireInstallAnchor && row.IsGenesis && anchor is not null
                            && row.Id != RosterRecordCrdtState.FromAdmission(anchor).RecordId) break;
                        throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                            "A durable admission row is malformed or has an invalid signature.");
                    }
                    admissions.Add(admission);
                    attestations.Add(attestation);
                    break;
                }
                case RosterRecordKind.Revocation:
                {
                    var revocation = state.ToRevocationOrNull();
                    if (revocation is null ||
                        !RosterSigning.VerifyRevocation(
                            teamId, revocation.RevokedPartyId, revocation.Signed, _verifier))
                    {
                        throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                            "A durable revocation row is malformed or has an invalid signature.");
                    }
                    revocations.Add(revocation);
                    attestations.Add(attestation);
                    break;
                }
                default:
                    throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                        "A durable roster row has an unknown record kind.");
            }
        }

        // An admission can name our public key without our consent. With competing roots, only an
        // earlier genesis signed by this install proves its anchor; an enrolled install must refuse.
        // A unique genesis retains slice 1's enrolled-member read-back contract.
        if (partial && requireInstallAnchor && genesisCount > 1 && (anchor is null || !anchor.PublicKey.Equals(derivedPrincipal)))
            throw Refuse(VerifiedTenantRosterRefusal.MultipleGenesis,
                "The durable log cannot name an earlier verified genesis signed by this install.");
        var selected = anchor is null ? admissions : admissions.Where(a => !a.Admission.IsGenesis
            || a.Admission.Signature == anchor.Admission.Signature);
        var rebuilt = MemberRoster.FromSyncedRecords(selected, revocations, _verifier, orderTime, _authority);
        if (rebuilt.TeamId != teamId || string.IsNullOrEmpty(rebuilt.GenesisPartyId) ||
            !rebuilt.ValidatesToGenesis(_verifier))
        {
            throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                "The durable roster does not rebuild to the requested tenant's verified genesis chain.");
        }
        var chainAdmissions = rebuilt.EnumerateAdmissions().ToList();
        if (partial && genesisCount > 1)
            chainAdmissions.AddRange(admissions.Where(a => a.Admission.IsGenesis).SelectMany(root =>
                MemberRoster.FromSyncedRecords(admissions.Where(a => !a.Admission.IsGenesis
                    || a.Admission.Signature == root.Admission.Signature), revocations, _verifier, orderTime, _authority)
                    .EnumerateAdmissions()));
        if (attestations.Any(attestation => !chainAdmissions.Any(admission =>
                admission.PartyId == attestation.NodePartyId
                && admission.PublicKey.ToBase64Url() == attestation.NodePublicKey)))
            throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                "A durable roster receipt was signed by a node outside the verified chain.");

        var acceptedAdmissions = rebuilt.EnumerateAdmissions()
            .Select(static admission =>
                (admission.PartyId, admission.Admission.Nonce, admission.Admission.Signature))
            .ToHashSet();
        var expectedAdmissions = admissions.AsEnumerable();
        if (partial && genesisCount > 1 && anchor is not null)
        {
            // Exempt only records verified as rooted in a discarded genesis. An unrelated orphan
            // (including one that merely claims a dropped party as its signer) must still alarm.
            var dropped = admissions.Where(a => a.Admission.IsGenesis && a.Admission.Signature != anchor.Admission.Signature)
                .SelectMany(root => MemberRoster.FromSyncedRecords(admissions.Where(a => !a.Admission.IsGenesis
                    || a.Admission.Signature == root.Admission.Signature), [], _verifier, authority: _authority)
                    .EnumerateAdmissions())
                .Select(a => (a.PartyId, a.Admission.Nonce, a.Admission.Signature)).ToHashSet();
            expectedAdmissions = admissions.Where(a => !dropped.Contains((a.PartyId, a.Admission.Nonce, a.Admission.Signature))
                || acceptedAdmissions.Contains((a.PartyId, a.Admission.Nonce, a.Admission.Signature)));
        }
        if (expectedAdmissions.Count() != acceptedAdmissions.Count || expectedAdmissions.Any(admission =>
                !acceptedAdmissions.Contains(
                    (admission.PartyId, admission.Admission.Nonce, admission.Admission.Signature))))
        {
            throw Refuse(VerifiedTenantRosterRefusal.Orphan,
                "A durable admission is not reachable from the requested tenant's genesis.");
        }

        return rebuilt;
    }

    private static VerifiedTenantRosterRefusedException Refuse(
        VerifiedTenantRosterRefusal refusal,
        string message) => new(refusal, message);

    private static void RequireCurrentWireFormat(NodeRosterRecord row)
    {
        if (row.WireFormatVersion != RosterWireFormat.CurrentVersion)
            throw Refuse(VerifiedTenantRosterRefusal.WireVersionUnsupported,
                $"A durable roster row has unsupported wire format version '{row.WireFormatVersion}'; "
                + $"expected '{RosterWireFormat.CurrentVersion}'.");
    }
}
