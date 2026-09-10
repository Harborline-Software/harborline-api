using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>The two legs of one atomic member-grant narrowing (ticket 362).</summary>
public sealed record AdmissionGrantNarrowing(AccessGrant Reissued, AccessGrant Revoked);

public sealed class NodeEfAuthorizationConfigurationStore(
    IDbContextFactory<NodeLocalSearchDbContext> factory,
    IRoleVocabularyReader vocabulary)
    : AuthorizationConfigurationStateReader, IAuthorizationConfigurationStore, IAuthorizationDefinitionReader,
        IAuthorizationDefinitionCatalogueReader, IHistoricalAuthorizationConfigurationReader
{
    /// <summary>Boot-only publication: signed roster provenance and completion commit under one fence.</summary>
    /// <remarks>
    /// Ticket 294 slice 2a: <c>new ActorId(admission.PartyId)</c> below is correct BY CONSTRUCTION now that
    /// the roster's <c>PartyId</c> is the canonical tenant principal id — it is the same key this store's
    /// own <c>SubjectId</c> column and every closure read use, so the backfilled grant lands under the key
    /// the gate reads. No column, index, migration or backfill row changes for that; only the meaning of
    /// the value already being written.
    /// </remarks>
    internal async Task<int> CommitRosterAdmissionMigrationAsync(
        IDbContextFactory<NodeLocalRosterDbContext> rosterFactory, IOperationVerifier verifier, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        return await HomeEpochFenceTransaction.RunAsync(db, async () =>
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
                    var removal = roster.Contains(admission.PartyId) ? null : group
                        .Where(row => row.Kind == (int)RosterRecordKind.Revocation && row.PartyId == admission.PartyId)
                        .Select(NodeRosterRecord.ToCrdtState).Select(state => state.ToRevocationOrNull()!)
                        .Where(record => !roster.RefusedRevocations.Any(refusal => refusal.Revocation == record))
                        .OrderBy(record => record.Signed.IssuedAt).ThenBy(record => record.Signed.Nonce).First();
                    var revocation = removal is null ? null : new GrantRevocation(
                        new ActorId(removal.Signed.RevokedByPartyId), removal.Signed.IssuedAt,
                        new GrantReason(GrantReasonCodes.RevocationReview, removal.Signed.Nonce.ToString("D")));
                    await StageAdmissionGrantAsync(
                        db, tenant, id, admission.PartyId, signed.AdmittedByPartyId,
                        HistoricalSignedAtoms(group, admission.PartyId), signed.IssuedAt, signed.Nonce,
                        revocation, "roster-migration:" + id.ToString("D"), ct).ConfigureAwait(false);
                    converted++;
                }
            }
            db.Set<RosterAdmissionGrantBackfillRow>().Add(new() { Id = 1, RecordCount = converted });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return converted;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR 0066 clause 3 (293 slice 4 fix 3) — the ONE derivation from "party X was admitted carrying
    /// permissions P" to a durable grant. The 3a boot backfill (verified historical admissions) and every
    /// LIVE admission conferral both go through here, so the two can never write a different grant shape.
    /// The grant is keyed on the ROSTER PARTY ID: that is the actor id the roster plane's own reads use
    /// (<c>AdminTeamAccessAuthority.ListMembersAsync</c> asks the gate for
    /// <c>InstallRootPermissionsAsync(new ActorId(rosterMember.PartyId), ...)</c>), so the gate's reader
    /// finds it under exactly the key the roster edge is addressed by. Since ticket 294 slice 2a that key
    /// IS the canonical tenant principal id, so both planes look THE GRANT up under one key. (That is a claim
    /// about the grant lookup only — a roster EDGE lookup keyed on the People CanonicalPartyReference would
    /// still miss, which is why ticket 293 slice 4 fix 4 moved the selected-session PEP onto this key too.)
    /// Staged only — the caller's SaveChanges inside its own fence commits it, so a conferral failure
    /// aborts the caller's unit of work rather than leaving a half state.
    /// </summary>
    internal async Task<AccessGrant> StageAdmissionGrantAsync(
        NodeLocalSearchDbContext db,
        TenantId tenant,
        Guid id,
        string admittedPartyId,
        string admittedByPartyId,
        PermissionSet permissions,
        DateTimeOffset issuedAt,
        Guid nonce,
        GrantRevocation? revocation,
        string sourceReference,
        CancellationToken ct,
        bool advanceEpoch = true)
    {
        var role = new RoleReference(RoleVocabularies.Domain, AdmissionRolePrefix + id.ToString("N"));
        var roleDefinition = AccessGrantAuthorizationSeed.AdmissionMigrationRole(id, tenant);
        var admissionVocabulary = new InMemoryRoleVocabulary([roleDefinition]);
        await EnsureRoleAsync(db, role, ct, admissionVocabulary).ConfigureAwait(false);
        foreach (var permission in permissions.Permissions)
        {
            var operation = AuthorizationOperation.Parse(permission);
            var definition = new AuthorizationCapabilityDefinition(
                new(StableId(id.ToString("D") + ":" + permission)), AccessGrantAuthorizationSeed.PackageId,
                1, operation, new PermissionAtom(operation, ScopeExpression.Parse("/")), RoleBindingSet.From([role]));
            var write = await AuthorizationDefinitionWriter.ValidateAdmissionMigrationAsync(
                definition, tenant, issuedAt, admissionVocabulary, ct).ConfigureAwait(false);
            await StageWriteAsync(db, write, ct, admissionVocabulary).ConfigureAwait(false);
        }

        var actor = new ActorId(admittedPartyId);
        var granter = new ActorId(admittedByPartyId);
        var grant = new AccessGrant(new GrantId(id), tenant, actor, role, ScopeExpression.Parse("/"),
            GrantResidency.Cache, new GrantValidity(issuedAt), GranterKind.Person, granter,
            issuedAt, new GrantProvenance(GrantSourceKind.Manual,
                new GrantReason(GrantReasonCodes.Manual, nonce.ToString("D")), granter),
            issuedAt, revocation is null ? GrantStatus.Active : GrantStatus.Revoked, revocation);
        db.Grants.Add(NodeEfGrantStore.ToRow(grant, sourceReference));
        // Ticket 293 slice 4 fix 4 — the boot backfill advances the epoch (it rewrites history before any
        // session exists); a LIVE conferral must NOT. The admitted principal's web session has just had its
        // (grant owner-version, authorization epoch) pins verified by the admission itself, and bumping the
        // epoch underneath them invalidates the very pins that authorized the admission: the second admission
        // of the same party then refuses "grant_unavailable" instead of "already_member", and a revoked
        // party's re-enrollment is over-blocked. Not advancing is fail-CLOSED in direction — a cached
        // closure snapshot keeps the narrower pre-conferral set, never a wider one.
        if (advanceEpoch)
            await NodeEfGrantStore.AdvanceEpochAsync(db, tenant, actor, ct).ConfigureAwait(false);
        return grant;
    }

    /// <summary>
    /// Confer one LIVE admission's grant — the same derivation the 3a backfill runs over history, under one
    /// fence with its own commit. What it writes is a grant of the admission's OWN per-admission role carrying
    /// the permission set the admission signed, anchored to that admission. It is NOT the web-plane membership
    /// grant — <see cref="InitialGrantIssuanceService"/> writes that one, at invitation acceptance, under the
    /// same subject key and at the same scope — and it OUTLIVES it: revoking the membership grant leaves the
    /// admitted party's signed authority standing. Returns null when this admission's grant already exists;
    /// idempotent on (tenant, admitted party), so a second admission of the same party mints no rival. Unlike
    /// the boot backfill it does NOT advance the admitted principal's authorization epoch (see
    /// <c>StageAdmissionGrantAsync</c>). The caller treats a throw as an admission failure: the roster write it
    /// guards must not be published.
    /// </summary>
    public async Task<AccessGrant?> ConferAdmissionGrantAsync(
        TenantId tenant,
        string admittedPartyId,
        string admittedByPartyId,
        PermissionSet permissions,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedByPartyId);
        ArgumentNullException.ThrowIfNull(permissions);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await HomeEpochFenceTransaction.RunAsync(db, async () =>
        {
            var id = StableId(tenant.Value + ":admission:" + admittedPartyId);
            var key = id.ToString("D");
            if (await db.Grants.AsNoTracking().AnyAsync(row => row.GrantId == key, ct).ConfigureAwait(false))
                return (AccessGrant?)null;
            var grant = await StageAdmissionGrantAsync(
                db, tenant, id, admittedPartyId, admittedByPartyId, permissions, at, id, null,
                "roster-admission:" + key, ct, advanceEpoch: false).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return grant;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ticket 362 — an administrator narrows a member's admission-conferred grant as ONE unit of work:
    /// the wider grant is revoked and a narrower one is appended on the SAME subject key, scope and role
    /// derivation (<see cref="StageAdmissionGrantAsync"/> — the one derivation
    /// <see cref="ConferAdmissionGrantAsync"/> routes through, so nothing derives an admission grant twice),
    /// inside one BEGIN IMMEDIATE fence committed by a single SaveChanges. A failure anywhere rolls both
    /// legs back, so the member is never left holding both sets or neither. Returns null when the grant is
    /// not a live install-root grant of this tenant.
    /// </summary>
    /// <summary>The per-admission role-name prefix this one derivation mints (and only it).</summary>
    private const string AdmissionRolePrefix = "roster-admission-";

    internal async Task<AdmissionGrantNarrowing?> NarrowAdmissionGrantAsync(
        TenantId tenant,
        GrantId current,
        PermissionSet narrowed,
        GrantRevocation revocation,
        Guid correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(narrowed);
        ArgumentNullException.ThrowIfNull(revocation);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        AdmissionGrantNarrowing? result = null;
        await HomeEpochFenceTransaction.RunAsync(db, async () =>
        {
            var key = current.ToString();
            var row = await db.Grants.FirstOrDefaultAsync(
                r => r.TenantId == tenant.Value && r.GrantId == key, ct).ConfigureAwait(false);
            if (row is null) return;
            var existing = NodeEfGrantStore.ToGrant(row);
            // Three refusals, all fail-closed and all silent (the caller reports one non-enumerating status):
            //  - a revoked grant has nothing left to narrow;
            //  - the reissue is staged at the install root, the only scope this derivation writes, so a
            //    narrower-scoped grant would be WIDENED by reissuing it there;
            //  - only a grant carrying a PER-ADMISSION role this derivation minted can be reissued narrower.
            //    A shared catalogue role (Member, Administrator) is the role a tenant membership PINS, so
            //    revoking such a grant locks the member out of their own session instead of narrowing them;
            //    and a narrower copy of a shared role would silently rewrite what that role means.
            if (existing.Status == GrantStatus.Revoked
                || existing.Scope != ScopeExpression.Parse("/")
                || existing.Role.Vocabulary != RoleVocabularies.Domain
                || !existing.Role.Name.StartsWith(AdmissionRolePrefix, StringComparison.Ordinal)) return;
            var at = revocation.RevokedAt;
            var id = StableId(tenant.Value + ":narrowed:" + key + ":" + correlationId.ToString("D"));
            var reissued = await StageAdmissionGrantAsync(
                    db, tenant, id, existing.Subject.Value, revocation.RevokedBy.Value, narrowed, at,
                    correlationId, revocation: null, "member-narrowed:" + id.ToString("D"), ct,
                    advanceEpoch: false)
                .ConfigureAwait(false);
            var revoked = existing with { Status = GrantStatus.Revoked, Revocation = revocation };
            db.Entry(row).CurrentValues.SetValues(
                NodeEfGrantStore.ToRow(revoked, row.SourceReference, checked(row.OwnerVersion + 1)));
            // ONE epoch advance for the whole act (both legs name one principal), so every live session of
            // that member re-evaluates exactly once. An administrator's narrowing MUST invalidate the
            // member's pins — the opposite of the live admission conferral, which must not, because there
            // the pins being bumped are the ones that authorized the admission itself (293 s4 fix 4).
            await NodeEfGrantStore.AdvanceEpochAsync(db, tenant, existing.Subject, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            result = new AdmissionGrantNarrowing(reissued, revoked);
        }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// What a pre-wire-version-3 install admitted a member with. A version-3 admission carries no permission
    /// set at all (293 slice 3b2), so the durable <c>signed_permissions</c> column written by the earlier wire
    /// versions is the only evidence this one-time migration can convert into grants. An install that never
    /// held a signed set (a fresh one, or a member admitted after version 3) migrates a grant with no
    /// capability definitions - grants, not the roster, then decide every act.
    /// </summary>
    private static PermissionSet HistoricalSignedAtoms(IEnumerable<NodeRosterRecord> rows, string partyId)
    {
        var json = rows.FirstOrDefault(row =>
            row.Kind == (int)RosterRecordKind.Admission && row.PartyId == partyId)?.SignedPermissionsJson;
        return string.IsNullOrWhiteSpace(json)
            ? PermissionSet.Empty
            : PermissionSet.From(JsonSerializer.Deserialize<string[]>(json) ?? []);
    }

    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    public override async ValueTask<AuthorizationConfigurationState> ReadStateAsync(
        AuthorizationCapabilityDefinitionId definitionId, TenantId? tenantId = null, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definition = await CurrentDefinitionAsync(context, definitionId, tenantId, ct).ConfigureAwait(false);
        if (definition is null) return new(null, RoleBindingSet.Empty, 0);
        var binding = tenantId is null ? null : await CurrentBindingAsync(context, tenantId.Value, definitionId, ct).ConfigureAwait(false);
        return binding is { } currentBinding
            ? new(definition, definition.OfferedRoles.Intersect(currentBinding.Roles), currentBinding.Revision)
            : new(definition, definition.OfferedRoles, 0);
    }

    public ValueTask CommitAsync(ValidatedAuthorizationConfigurationWrite write, CancellationToken ct = default) =>
        CommitCoreAsync(write, bootstrapTenant: null, ct);

    public ValueTask CommitBootstrapAsync(
        ValidatedAuthorizationConfigurationWrite write,
        TenantId tenant,
        IGrantStore grants,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return CommitCoreAsync(write, tenant, ct);
    }

    private async ValueTask CommitCoreAsync(
        ValidatedAuthorizationConfigurationWrite write,
        TenantId? bootstrapTenant,
        CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await HomeEpochFenceTransaction.RunAsync(context, async () =>
        {
        if (bootstrapTenant is not null &&
            (await HasBootstrapRetirementEvidenceAsync(context, ct).ConfigureAwait(false) ||
             await context.Grants.AsNoTracking().AnyAsync(
                 row => row.RoleVocabulary == RoleReference.Administrator.Vocabulary
                     && row.RoleName == RoleReference.Administrator.Name,
                 ct).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("The authorization-definition bootstrap path is permanently sealed.");
        }
        await StageWriteAsync(context, write, ct).ConfigureAwait(false);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task StageWriteAsync(NodeLocalSearchDbContext context,
        ValidatedAuthorizationConfigurationWrite write, CancellationToken ct, IRoleVocabularyReader? writeVocabulary = null)
    {
        if (write.Definition is { } definition)
        {
            var current = await context.AuthorizationDefinitions.Where(x => x.DefinitionId == definition.DefinitionId.Value.ToString())
                .MaxAsync(x => (long?)x.Revision, ct).ConfigureAwait(false) ?? 0;
            if (current != write.ExpectedDefinitionRevision) throw new InvalidOperationException("Authorization definition changed after bind.");
            if (current > 0)
            {
                var declaringTenantId = await context.AuthorizationDefinitions
                    .Where(x => x.DefinitionId == definition.DefinitionId.Value.ToString() && x.Revision == current)
                    .Select(x => x.DeclaringTenantId)
                    .SingleAsync(ct)
                    .ConfigureAwait(false);
                if (!string.Equals(declaringTenantId, write.DeclaringTenantId?.Value, StringComparison.Ordinal))
                    throw new InvalidOperationException("A replacement must retain the definition's declaring tenant.");
            }
            foreach (var role in definition.OfferedRoles.Roles)
                await EnsureRoleAsync(context, role, ct, writeVocabulary).ConfigureAwait(false);
            context.AuthorizationDefinitions.Add(new AuthorizationDefinitionRow
            {
                DefinitionId = definition.DefinitionId.Value.ToString(), Revision = definition.Revision,
                EffectiveAtUnixMs = (write.DefinitionEffectiveAt
                    ?? throw new InvalidOperationException("A definition write requires an effective instant."))
                    .ToUnixTimeMilliseconds(),
                DeclaringTenantId = write.DeclaringTenantId?.Value,
                PublisherPackageId = definition.PublisherPackageId, Operation = definition.Operation.Value,
                ScopeType = (int)definition.Atom.Scope.Type, ScopeValue = definition.Atom.Scope.Value,
            });
            context.AuthorizationOfferedRoles.AddRange(definition.OfferedRoles.Roles.Select(role => new AuthorizationOfferedRoleRow
            { DefinitionId = definition.DefinitionId.Value.ToString(), Revision = definition.Revision, Vocabulary = role.Vocabulary, RoleName = role.Name }));
            var catalog = await context.AuthorizationCatalogVersions.SingleAsync(x => x.Id == 1, ct).ConfigureAwait(false);
            catalog.Version++;
        }
        if (write.BindingRevision is { } binding)
        {
            var current = await context.AuthorizationBindingRevisions
                .Where(x => x.TenantId == binding.TenantId.Value && x.DefinitionId == binding.DefinitionId.Value.ToString())
                .MaxAsync(x => (long?)x.Revision, ct).ConfigureAwait(false) ?? 0;
            if (current != write.ExpectedBindingRevision) throw new InvalidOperationException("Authorization binding changed after bind.");
            context.AuthorizationBindingRevisions.Add(new AuthorizationBindingRevisionRow
            {
                TenantId = binding.TenantId.Value, DefinitionId = binding.DefinitionId.Value.ToString(), Revision = binding.Revision,
                ChangedBy = binding.ChangedBy.Value, ChangedAtUnixMs = binding.ChangedAt.ToUnixTimeMilliseconds(),
                Reason = binding.Reason.Value, Warning = binding.SelectedRoles.Equals(RoleBindingSet.Empty) ? (int)BindingWarningCode.EmptyBinding : null,
            });
            context.AuthorizationBindingRoles.AddRange(binding.SelectedRoles.Roles.Select(role => new AuthorizationBindingRoleRow
            { TenantId = binding.TenantId.Value, DefinitionId = binding.DefinitionId.Value.ToString(), Revision = binding.Revision, Vocabulary = role.Vocabulary, RoleName = role.Name }));
            var version = await context.AuthorizationTenantVersions.SingleOrDefaultAsync(x => x.TenantId == binding.TenantId.Value, ct).ConfigureAwait(false);
            if (version is null) context.AuthorizationTenantVersions.Add(new AuthorizationTenantVersionRow { TenantId = binding.TenantId.Value, Version = 1 });
            else version.Version++;
        }
    }

    private static async Task<bool> HasBootstrapRetirementEvidenceAsync(
        NodeLocalSearchDbContext context,
        CancellationToken ct)
    {
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM bootstrap_claim_marker) " +
            "OR EXISTS (SELECT 1 FROM installation_audit_envelopes " +
            "WHERE event_type = 'InstallationBootstrapClaimRedeemed') THEN 1 ELSE 0 END;";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private async Task EnsureRoleAsync(NodeLocalSearchDbContext context, RoleReference role, CancellationToken ct, IRoleVocabularyReader? writeVocabulary = null)
    {
        if (context.AuthorizationRoles.Local.Any(x => x.Vocabulary == role.Vocabulary && x.RoleName == role.Name)
            || await context.AuthorizationRoles.AnyAsync(
            x => x.Vocabulary == role.Vocabulary && x.RoleName == role.Name, ct).ConfigureAwait(false)) return;
        var definition = await (writeVocabulary ?? vocabulary).ResolveAsync(role, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown role '{role}'.");
        context.AuthorizationRoles.Add(new AuthorizationRoleRow
        {
            Vocabulary = role.Vocabulary,
            RoleName = role.Name,
            RoleDefinitionId = definition.RoleDefinitionId.ToString(),
            DisplayName = definition.DisplayName,
            OwnerKind = (int)definition.Owner.Kind,
            OwnerId = definition.Owner.OwnerId,
            IsSealed = definition.IsSealed,
        });
    }

    public async ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAsync(TenantId tenantId, RoleReference role, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await context.AuthorizationDefinitions
            .Where(x => x.DeclaringTenantId == null || x.DeclaringTenantId == tenantId.Value)
            .Select(x => x.DefinitionId).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<AuthorizationCapabilityDefinition>();
        foreach (var id in ids)
        {
            var definitionId = new AuthorizationCapabilityDefinitionId(Guid.Parse(id));
            var definition = await CurrentDefinitionAsync(context, definitionId, tenantId, ct).ConfigureAwait(false);
            if (definition is null) continue;
            var binding = await CurrentBindingAsync(context, tenantId, definitionId, ct).ConfigureAwait(false);
            var effective = binding is { } currentBinding
                ? definition.OfferedRoles.Intersect(currentBinding.Roles)
                : definition.OfferedRoles;
            if (effective.Roles.Contains(role)) result.Add(definition);
        }
        return result;
    }

    public async ValueTask<RoleBindingSet> EffectiveBindingAsync(TenantId tenantId, AuthorizationCapabilityDefinitionId definitionId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definition = await CurrentDefinitionAsync(context, definitionId, tenantId, ct).ConfigureAwait(false);
        if (definition is null) return RoleBindingSet.Empty;
        var binding = await CurrentBindingAsync(context, tenantId, definitionId, ct).ConfigureAwait(false);
        return binding is { } currentBinding
            ? definition.OfferedRoles.Intersect(currentBinding.Roles)
            : definition.OfferedRoles;
    }

    public async ValueTask<IReadOnlyList<AuthorizationDefinitionBindingView>> ListAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = (await context.AuthorizationDefinitions
                .Where(row => row.DeclaringTenantId == null || row.DeclaringTenantId == tenantId.Value)
                .Select(row => row.DefinitionId)
                .Distinct()
                .ToArrayAsync(ct)
                .ConfigureAwait(false))
            .Select(value => new AuthorizationCapabilityDefinitionId(Guid.Parse(value)))
            .OrderBy(id => id.Value)
            .ToArray();
        var result = new List<AuthorizationDefinitionBindingView>(ids.Length);
        foreach (var id in ids)
        {
            var view = await FindAsync(context, tenantId, id, ct).ConfigureAwait(false);
            if (view is not null) result.Add(view);
        }
        return result;
    }

    public async ValueTask<AuthorizationDefinitionBindingView?> FindAsync(
        TenantId tenantId,
        AuthorizationCapabilityDefinitionId definitionId,
        CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await FindAsync(context, tenantId, definitionId, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAtAsync(
        TenantId tenantId,
        RoleReference role,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var atUnixMs = at.ToUnixTimeMilliseconds();
        var ids = await context.AuthorizationDefinitions
            .Where(row => row.EffectiveAtUnixMs.HasValue && row.EffectiveAtUnixMs.Value <= atUnixMs)
            .Where(row => row.DeclaringTenantId == null || row.DeclaringTenantId == tenantId.Value)
            .Select(row => row.DefinitionId)
            .Distinct()
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        var result = new List<AuthorizationCapabilityDefinition>();
        foreach (var id in ids)
        {
            var definitionId = new AuthorizationCapabilityDefinitionId(Guid.Parse(id));
            var definition = await DefinitionAtAsync(context, definitionId, tenantId, atUnixMs, ct).ConfigureAwait(false);
            if (definition is null) continue;
            var binding = await BindingAtAsync(context, tenantId, definitionId, atUnixMs, ct).ConfigureAwait(false);
            var effective = binding is { } revision
                ? definition.OfferedRoles.Intersect(revision.Roles)
                : definition.OfferedRoles;
            if (effective.Roles.Contains(role)) result.Add(definition);
        }

        return result;
    }

    private static async Task<AuthorizationCapabilityDefinition?> CurrentDefinitionAsync(
        NodeLocalSearchDbContext context,
        AuthorizationCapabilityDefinitionId id,
        TenantId? tenantId,
        CancellationToken ct)
    {
        var query = context.AuthorizationDefinitions.Where(x => x.DefinitionId == id.Value.ToString());
        if (tenantId is { } tenant)
            query = query.Where(x => x.DeclaringTenantId == null || x.DeclaringTenantId == tenant.Value);
        var row = await query
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return null;
        var roles = await context.AuthorizationOfferedRoles.Where(x => x.DefinitionId == row.DefinitionId && x.Revision == row.Revision)
            .Select(x => new RoleReference(x.Vocabulary, x.RoleName)).ToArrayAsync(ct).ConfigureAwait(false);
        var operation = AuthorizationOperation.Parse(row.Operation);
        return new(id, row.PublisherPackageId, row.Revision, operation,
            new PermissionAtom(operation, ScopeExpression.Parse(row.ScopeValue)), RoleBindingSet.From(roles));
    }

    private static async Task<AuthorizationCapabilityDefinition?> DefinitionAtAsync(
        NodeLocalSearchDbContext context,
        AuthorizationCapabilityDefinitionId id,
        TenantId tenantId,
        long atUnixMs,
        CancellationToken ct)
    {
        var row = await context.AuthorizationDefinitions
            .Where(x => x.DefinitionId == id.Value.ToString()
                && (x.DeclaringTenantId == null || x.DeclaringTenantId == tenantId.Value)
                && x.EffectiveAtUnixMs.HasValue
                && x.EffectiveAtUnixMs.Value <= atUnixMs)
            .OrderByDescending(x => x.EffectiveAtUnixMs)
            .ThenByDescending(x => x.Revision)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return row is null ? null : await ToDefinitionAsync(context, row, ct).ConfigureAwait(false);
    }

    private static async Task<(long Revision, RoleBindingSet Roles)?> CurrentBindingAsync(NodeLocalSearchDbContext context, TenantId tenant, AuthorizationCapabilityDefinitionId id, CancellationToken ct)
    {
        var row = await context.AuthorizationBindingRevisions.Where(x => x.TenantId == tenant.Value && x.DefinitionId == id.Value.ToString())
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return null;
        var roles = await context.AuthorizationBindingRoles.Where(x => x.TenantId == tenant.Value && x.DefinitionId == row.DefinitionId && x.Revision == row.Revision)
            .Select(x => new RoleReference(x.Vocabulary, x.RoleName)).ToArrayAsync(ct).ConfigureAwait(false);
        return (row.Revision, RoleBindingSet.From(roles));
    }

    private static async Task<AuthorizationDefinitionBindingView?> FindAsync(
        NodeLocalSearchDbContext context,
        TenantId tenantId,
        AuthorizationCapabilityDefinitionId definitionId,
        CancellationToken ct)
    {
        var definition = await CurrentDefinitionAsync(context, definitionId, tenantId, ct).ConfigureAwait(false);
        if (definition is null) return null;
        var row = await context.AuthorizationBindingRevisions
            .Where(value => value.TenantId == tenantId.Value
                && value.DefinitionId == definitionId.Value.ToString())
            .OrderByDescending(value => value.Revision)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
            return new AuthorizationDefinitionBindingView(definition, 0, definition.OfferedRoles, null);
        var roles = await context.AuthorizationBindingRoles
            .Where(value => value.TenantId == tenantId.Value
                && value.DefinitionId == row.DefinitionId
                && value.Revision == row.Revision)
            .Select(value => new RoleReference(value.Vocabulary, value.RoleName))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        return new AuthorizationDefinitionBindingView(
            definition,
            row.Revision,
            definition.OfferedRoles.Intersect(RoleBindingSet.From(roles)),
            row.Warning is null ? null : (BindingWarningCode)row.Warning.Value);
    }

    private static async Task<(long Revision, RoleBindingSet Roles)?> BindingAtAsync(
        NodeLocalSearchDbContext context,
        TenantId tenant,
        AuthorizationCapabilityDefinitionId id,
        long atUnixMs,
        CancellationToken ct)
    {
        var row = await context.AuthorizationBindingRevisions
            .Where(x => x.TenantId == tenant.Value
                && x.DefinitionId == id.Value.ToString()
                && x.ChangedAtUnixMs <= atUnixMs)
            .OrderByDescending(x => x.ChangedAtUnixMs)
            .ThenByDescending(x => x.Revision)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        var roles = await context.AuthorizationBindingRoles
            .Where(x => x.TenantId == tenant.Value
                && x.DefinitionId == row.DefinitionId
                && x.Revision == row.Revision)
            .Select(x => new RoleReference(x.Vocabulary, x.RoleName))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        return (row.Revision, RoleBindingSet.From(roles));
    }

    private static async Task<AuthorizationCapabilityDefinition> ToDefinitionAsync(
        NodeLocalSearchDbContext context,
        AuthorizationDefinitionRow row,
        CancellationToken ct)
    {
        var roles = await context.AuthorizationOfferedRoles
            .Where(x => x.DefinitionId == row.DefinitionId && x.Revision == row.Revision)
            .Select(x => new RoleReference(x.Vocabulary, x.RoleName))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        var operation = AuthorizationOperation.Parse(row.Operation);
        return new(
            new AuthorizationCapabilityDefinitionId(Guid.Parse(row.DefinitionId)),
            row.PublisherPackageId,
            row.Revision,
            operation,
            new PermissionAtom(operation, ScopeExpression.Parse(row.ScopeValue)),
            RoleBindingSet.From(roles));
    }
}

/// <summary>Registers the node's single durable authorization model and all fixed admission gates.</summary>
public static class NodeAuthorizationComposition
{
    public static IServiceCollection AddNodeAuthorizationModel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<NodeEfAuthorizationConfigurationStore>();
        services.AddSingleton<IAuthorizationConfigurationStore>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationConfigurationStore>());
        services.AddSingleton<AuthorizationConfigurationStateReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationConfigurationStore>());
        services.AddSingleton<IAuthorizationDefinitionReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationConfigurationStore>());
        services.AddSingleton<IAuthorizationDefinitionCatalogueReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationConfigurationStore>());
        services.AddSingleton<IAuthorizationDefinitionAtomReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationConfigurationStore>());
        services.AddSingleton<IHistoricalAuthorizationConfigurationReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationConfigurationStore>());
        services.AddSingleton<IGrantStore, NodeEfGrantStore>();
        services.AddSingleton<AuthorizationClosureReconciler>();
        services.AddSingleton<NodeEfAuthorizationClosureReader>();
        services.AddSingleton<IAuthorizationClosureReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationClosureReader>());
        services.AddSingleton<IAuthorizationClosureSnapshotReader>(sp =>
            sp.GetRequiredService<NodeEfAuthorizationClosureReader>());
        services.AddSingleton<IHistoricalAuthorizationResolver, HistoricalAuthorizationResolver>();
        services.AddAccessGrantModule();
        return services;
    }
}
