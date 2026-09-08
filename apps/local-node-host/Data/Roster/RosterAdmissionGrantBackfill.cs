using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>One-time conversion of verified admission history to ordinary durable role grants.</summary>
public sealed class RosterAdmissionGrantBackfill(
    IDbContextFactory<NodeLocalRosterDbContext> rosterFactory,
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
    IOperationVerifier verifier,
    ILogger<RosterAdmissionGrantBackfill> logger)
{
    /// <summary>The completion marker and all grants commit together before roster sync starts.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var db = await grantFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        var count = await HomeEpochFenceTransaction.RunAsync(db, async () =>
        {
            if (await db.Set<RosterAdmissionGrantBackfillRow>().AnyAsync(ct).ConfigureAwait(false)) return 0;
            await using var rosterDb = await rosterFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await rosterDb.RosterRecords.AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false);
            var reader = new VerifiedTenantRosterReader(rosterFactory, verifier);
            var converted = 0;
            foreach (var group in rows.GroupBy(row => row.TeamId))
            {
                var tenant = new TenantId(group.Key);
                // Verification holds the shared database's write lock; no peer append can race it.
                var roster = await reader.ReadAsync(tenant, ct).ConfigureAwait(false);
                foreach (var admission in roster.EnumerateAdmissions())
                {
                    var signed = admission.Admission;
                    var source = RosterRecordCrdtState.FromAdmission(admission).RecordId;
                    var id = StableId(group.Key + ":" + source);
                    var role = new RoleReference(RoleVocabularies.Domain, "roster-admission-" + id.ToString("N"));
                    db.AuthorizationRoles.Add(new AuthorizationRoleRow
                    {
                        Vocabulary = role.Vocabulary, RoleName = role.Name, RoleDefinitionId = id.ToString("D"),
                        DisplayName = "Migrated admission", OwnerKind = (int)RoleOwnerKind.Tenant,
                        OwnerId = group.Key, IsSealed = false,
                    });
                    foreach (var permission in PermissionSet.From(signed.Permissions ?? []).Permissions)
                    {
                        var definitionId = StableId(id.ToString("D") + ":" + permission).ToString("D");
                        db.AuthorizationDefinitions.Add(new AuthorizationDefinitionRow
                        {
                            DefinitionId = definitionId, Revision = 1, DeclaringTenantId = group.Key,
                            EffectiveAtUnixMs = signed.IssuedAt.ToUnixTimeMilliseconds(),
                            PublisherPackageId = AccessGrantAuthorizationSeed.PackageId,
                            Operation = permission, ScopeType = 0, ScopeValue = "/",
                        });
                        db.AuthorizationOfferedRoles.Add(new AuthorizationOfferedRoleRow
                        { DefinitionId = definitionId, Revision = 1, Vocabulary = role.Vocabulary, RoleName = role.Name });
                    }
                    var actor = new ActorId(admission.PartyId);
                    var granter = new ActorId(signed.AdmittedByPartyId);
                    var removal = roster.Contains(admission.PartyId) ? null : group
                        .Where(row => row.Kind == (int)RosterRecordKind.Revocation && row.PartyId == admission.PartyId)
                        .Select(NodeRosterRecord.ToCrdtState).Select(state => state.ToRevocationOrNull()!)
                        .Where(record => !roster.RefusedRevocations.Any(refusal => refusal.Revocation == record))
                        .OrderBy(record => record.Signed.IssuedAt).ThenBy(record => record.Signed.Nonce).First();
                    var revocation = removal is null ? null : new GrantRevocation(
                        new ActorId(removal.Signed.RevokedByPartyId), removal.Signed.IssuedAt,
                        new GrantReason(GrantReasonCodes.RevocationReview, removal.Signed.Nonce.ToString("D")));
                    var grant = new AccessGrant(new GrantId(id), tenant, actor, role, ScopeExpression.Parse("/"),
                        GrantResidency.Cache, new GrantValidity(signed.IssuedAt), GranterKind.Person, granter,
                        signed.IssuedAt, new GrantProvenance(GrantSourceKind.Manual,
                            new GrantReason(GrantReasonCodes.Manual, signed.Nonce.ToString("D")), granter),
                        signed.IssuedAt, revocation is null ? GrantStatus.Active : GrantStatus.Revoked, revocation);
                    db.Grants.Add(NodeEfGrantStore.ToRow(grant, "roster-migration:" + id.ToString("D")));
                    await NodeEfGrantStore.AdvanceEpochAsync(db, tenant, actor, ct).ConfigureAwait(false);
                    converted++;
                }
            }
            var catalog = await db.AuthorizationCatalogVersions.SingleAsync(ct).ConfigureAwait(false);
            catalog.Version++;
            db.Set<RosterAdmissionGrantBackfillRow>().Add(new() { Id = 1, RecordCount = converted });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return converted;
        }, ct).ConfigureAwait(false);
        if (count > 0) logger.LogInformation("Migrated {Count} signed admission record(s) into durable grants.", count);
        return count;
    }

    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

/// <summary>Durable one-time completion evidence; this is never a permission verdict.</summary>
public sealed class RosterAdmissionGrantBackfillRow
{
    public int Id { get; set; }
    public int RecordCount { get; set; }
}
