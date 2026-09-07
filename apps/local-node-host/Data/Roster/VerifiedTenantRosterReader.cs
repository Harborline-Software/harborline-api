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

    /// <summary>Construct the reader from the durable roster context and canonical operation verifier.</summary>
    public VerifiedTenantRosterReader(
        IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
        IOperationVerifier verifier)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    /// <inheritdoc />
    public async Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct)
    {
        if (team.IsSystemSentinel || string.IsNullOrWhiteSpace(team.Value) || !Guid.TryParse(team.Value, out var teamId))
        {
            throw Refuse(VerifiedTenantRosterRefusal.WrongTenant,
                "The requested tenant is not a roster-backed team identifier.");
        }

        var canonicalTeam = teamId.ToString("D");
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.RosterRecords
            .AsNoTracking()
            .Where(row => row.TeamId == canonicalTeam)
            .OrderBy(row => row.IssuedAtUtc)
            .ThenBy(row => row.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

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
        if (genesisCount != 1)
        {
            throw Refuse(VerifiedTenantRosterRefusal.MultipleGenesis,
                "The requested tenant has multiple durable genesis admissions.");
        }

        var admissions = new List<MemberAdmissionRecord>();
        var revocations = new List<MemberRevocationRecord>();
        foreach (var row in rows)
        {
            var state = NodeRosterRecord.ToCrdtState(row);
            switch ((RosterRecordKind)row.Kind)
            {
                case RosterRecordKind.Admission:
                {
                    var admission = state.ToAdmissionOrNull();
                    if (admission is null ||
                        !RosterSigning.VerifyAdmission(
                            teamId, admission.PartyId, admission.PublicKey, admission.Admission, _verifier))
                    {
                        throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                            "A durable admission row is malformed or has an invalid signature.");
                    }
                    admissions.Add(admission);
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
                    break;
                }
                default:
                    throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                        "A durable roster row has an unknown record kind.");
            }
        }

        var rebuilt = MemberRoster.FromSyncedRecords(admissions, revocations, _verifier);
        if (rebuilt.TeamId != teamId || string.IsNullOrEmpty(rebuilt.GenesisPartyId) ||
            !rebuilt.ValidatesToGenesis(_verifier))
        {
            throw Refuse(VerifiedTenantRosterRefusal.Tampered,
                "The durable roster does not rebuild to the requested tenant's verified genesis chain.");
        }

        var acceptedAdmissions = rebuilt.EnumerateAdmissions()
            .Select(static admission =>
                (admission.PartyId, admission.Admission.Nonce, admission.Admission.Signature))
            .ToHashSet();
        if (admissions.Count != acceptedAdmissions.Count || admissions.Any(admission =>
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
}
