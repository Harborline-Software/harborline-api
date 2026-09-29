using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
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
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>The two legs of one atomic member-grant narrowing (ticket 362).</summary>
public sealed record AdmissionGrantNarrowing(AccessGrant Reissued, AccessGrant Revoked);

public sealed class NodeEfAuthorizationConfigurationStore(
    IDbContextFactory<NodeLocalSearchDbContext> factory,
    IRoleVocabularyReader vocabulary,
    IWritePipelineObserver? pipelineObserver = null)
    : AuthorizationConfigurationStateReader, IAuthorizationConfigurationStore, IAuthorizationDefinitionReader,
        IAuthorizationDefinitionCatalogueReader, IHistoricalAuthorizationConfigurationReader, IPackProjectionParticipant,
        IAuditingAuthorizationConfigurationStore
{
    private PackProjectionSqliteUnit? projectionUnit;

    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var unit = transaction.Durable(() => new PackProjectionSqliteUnit(factory.CreateDbContext()));
        transaction.Finally(() => projectionUnit = null);
        projectionUnit = unit;
        return static () => { };
    });

    private async Task<NodeLocalSearchDbContext> CreateContextAsync(CancellationToken ct)
    {
        var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return projectionUnit is null ? context : projectionUnit.Join(context);
    }

    private static Task InTransactionAsync(NodeLocalSearchDbContext context, Func<Task> action, CancellationToken ct) =>
        context.Database.CurrentTransaction is null
            ? HomeEpochFenceTransaction.RunAsync(context, action, ct)
            : action();

    /// <summary>
    /// Confer one LIVE admission's grant under one fence with its own commit. What it writes is a grant of the
    /// admission's OWN per-admission role carrying the permission set the admission signed, anchored to that
    /// admission. It is NOT the web-plane membership grant — <see cref="InitialGrantIssuanceService"/> writes that
    /// one, at invitation acceptance, under the same subject key and at the same scope — and it OUTLIVES it:
    /// revoking the membership grant leaves the admitted party's signed authority standing. The grant is keyed on
    /// the ROSTER PARTY ID, the actor id the roster plane's own reads use, which since ticket 294 slice 2a IS the
    /// canonical tenant principal id. Returns null when this admission's grant already exists; idempotent on
    /// (tenant, admitted party), so a second admission of the same party mints no rival. The live conferral does
    /// NOT advance the admitted principal's authorization epoch: that principal's web session has just had its
    /// pins verified by the admission itself, and bumping the epoch underneath them would refuse the second
    /// admission of the same party "grant_unavailable" instead of "already_member" (fail-closed in direction: a
    /// cached closure keeps the narrower pre-conferral set). The caller treats a throw as an admission failure:
    /// the roster write it guards must not be published.
    /// ck-10: the conferral runs the six ADR 0038 stages (<see cref="AuthorizationDefinitionWriter"/>), and its
    /// commit stage stages the definitions, the grant and the audit in this fence.
    /// </summary>
    internal async Task<AccessGrant?> ConferAdmissionGrantAsync(
        TenantId tenant,
        string admittedPartyId,
        string admittedByPartyId,
        PermissionSet permissions,
        DateTimeOffset at,
        AdmissionConferralAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedByPartyId);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(authority);
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var db = await CreateContextAsync(ct).ConfigureAwait(false);
        return await HomeEpochFenceTransaction.RunAsync(db, async () =>
        {
            var id = StableId(tenant.Value + ":admission:" + admittedPartyId);
            return await AuthorizationDefinitionWriter.ConferAdmissionAsync(
                new AdmissionConferral(tenant, id, admittedPartyId, admittedByPartyId, permissions, at, id,
                    "roster-admission:" + id.ToString("D")),
                authority, new ConferralUnit(this, db), pipelineObserver, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ck-10: the caller's open fence as the conferral pipeline sees it. Commit stages each sealed definition,
    /// the grant and the conferral's audit into that one context and saves them together, so the fence commits
    /// all of them or none.
    /// </summary>
    private sealed class ConferralUnit(NodeEfAuthorizationConfigurationStore store, NodeLocalSearchDbContext db)
        : IAdmissionConferralUnit
    {
        public async ValueTask<bool> GrantExistsAsync(GrantId grant, CancellationToken ct)
        {
            var key = grant.ToString();
            return await db.Grants.AsNoTracking().AnyAsync(row => row.GrantId == key, ct).ConfigureAwait(false);
        }

        public async ValueTask<long> DefinitionRevisionAsync(AuthorizationCapabilityDefinitionId definition, CancellationToken ct)
        {
            var key = definition.Value.ToString();
            return await db.AuthorizationDefinitions.Where(row => row.DefinitionId == key)
                .MaxAsync(row => (long?)row.Revision, ct).ConfigureAwait(false) ?? 0;
        }

        public async ValueTask CommitAsync(ValidatedAdmissionConferral conferral, CancellationToken ct)
        {
            var grant = conferral.Grant;
            await store.EnsureRoleAsync(db, grant.Role, ct, conferral.Roles).ConfigureAwait(false);
            foreach (var write in conferral.Definitions)
                await store.StageWriteAsync(db, write, ct, conferral.Roles).ConfigureAwait(false);
            db.Grants.Add(NodeEfGrantStore.ToRow(grant, conferral.SourceReference));
            var body = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["grantId"] = grant.GrantId.ToString(),
                ["admittedParty"] = grant.Subject.Value,
                ["permissions"] = string.Join(",", conferral.Definitions
                    .Select(write => write.Definition!.Operation.Value).Order(StringComparer.Ordinal)),
            };
            if (conferral.Decision is { } decision)
                NodeAuditOutbox.StageAuthorized(db, AdmissionGrantConferredEventType, decision, body);
            else
                NodeAuditOutbox.StageSystem(db, AdmissionGrantConferredEventType, grant.TenantId, grant.GrantedAt,
                    grant.GrantedBy, body);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ticket 362 — an administrator narrows a member's admission-conferred grant as ONE unit of work:
    /// the wider grant is revoked and a narrower one is appended on the SAME subject key, scope and role
    /// derivation (the conferral pipeline <see cref="ConferAdmissionGrantAsync"/> also runs, so nothing derives
    /// an admission grant twice), inside one BEGIN IMMEDIATE fence committed by a single SaveChanges. A failure
    /// anywhere rolls both legs back, so the member is never left holding both sets or neither. Returns null
    /// when the grant is not a live install-root grant of this tenant.
    /// </summary>
    internal async Task<AdmissionGrantNarrowing?> NarrowAdmissionGrantAsync(
        TenantId tenant,
        GrantId current,
        PermissionSet narrowed,
        GrantRevocation revocation,
        Guid correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(narrowed);
        ArgumentNullException.ThrowIfNull(revocation);
        var authority = AdmissionConferralAuthority.Narrowing(admittedDecision, current);
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var db = await CreateContextAsync(ct).ConfigureAwait(false);
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
                || !existing.Role.Name.StartsWith(AccessGrantAuthorizationSeed.AdmissionRolePrefix, StringComparison.Ordinal)) return;
            var at = revocation.RevokedAt;
            var id = StableId(tenant.Value + ":narrowed:" + key + ":" + correlationId.ToString("D"));
            var revoked = existing with { Status = GrantStatus.Revoked, Revocation = revocation };
            db.Entry(row).CurrentValues.SetValues(
                NodeEfGrantStore.ToRow(revoked, row.SourceReference, checked(row.OwnerVersion + 1)));
            // ONE epoch advance for the whole act (both legs name one principal), so every live session of
            // that member re-evaluates exactly once. An administrator's narrowing MUST invalidate the
            // member's pins — the opposite of the live admission conferral, which must not, because there
            // the pins being bumped are the ones that authorized the admission itself (293 s4 fix 4).
            await NodeEfGrantStore.AdvanceEpochAsync(db, tenant, existing.Subject, ct).ConfigureAwait(false);
            // The reissue's commit stage saves the revoked leg and the epoch with it, in this fence.
            var reissued = await AuthorizationDefinitionWriter.ConferAdmissionAsync(
                    new AdmissionConferral(tenant, id, existing.Subject.Value, revocation.RevokedBy.Value, narrowed,
                        at, correlationId, "member-narrowed:" + id.ToString("D")),
                    authority, new ConferralUnit(this, db), pipelineObserver, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The narrowed reissue already exists.");
            result = new AdmissionGrantNarrowing(reissued, revoked);
        }, ct).ConfigureAwait(false);
        return result;
    }

    private static Guid StableId(string value) => AdmissionConferral.StableId(value);

    public override async ValueTask<AuthorizationConfigurationState> ReadStateAsync(
        AuthorizationCapabilityDefinitionId definitionId, TenantId? tenantId = null, CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
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
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
        await InTransactionAsync(context, async () =>
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
        StageAudit(context, write);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// DES-0029 ck-6: every committed configuration write stages its audit entry in the same transaction. An
    /// ordinary write records the gate decision that authorized it; a carried pack, seed or bootstrap authority
    /// records a system entry attributed to its principal.
    /// </summary>
    private static void StageAudit(NodeLocalSearchDbContext context, ValidatedAuthorizationConfigurationWrite write)
    {
        var actor = write.Actor ?? throw new InvalidOperationException("A configuration write carries no attributed principal.");
        var tenant = write.AttributedTenant ?? throw new InvalidOperationException("A configuration write carries no attributed tenant.");
        var at = write.DefinitionEffectiveAt ?? write.BindingRevision?.ChangedAt
            ?? throw new InvalidOperationException("A configuration write carries no instant.");
        var (eventType, definitionId, revision) = write.BindingRevision is { } binding
            ? (BindingNarrowedEventType, binding.DefinitionId.Value.ToString("D"), binding.Revision)
            : (DefinitionWrittenEventType, write.Definition!.DefinitionId.Value.ToString("D"), write.Definition.Revision);
        var body = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["definitionId"] = definitionId,
            ["revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["kind"] = write.Kind.ToString(),
        };
        if (write.Decision is { } decision)
            NodeAuditOutbox.StageAuthorized(context, eventType, decision, body, write.AuditId);
        else
            NodeAuditOutbox.StageSystem(context, eventType, tenant, at, actor, body, write.AuditId);
    }

    /// <summary>The event type a narrowed capability binding is recorded under.</summary>
    public static readonly AuditEventType BindingNarrowedEventType = new("AuthorizationBindingNarrowed");

    /// <summary>The event type a live admission's conferred grant is recorded under.</summary>
    public static readonly AuditEventType AdmissionGrantConferredEventType = new("AuthorizationAdmissionGrantConferred");

    /// <summary>The event type an installed or replaced capability definition is recorded under.</summary>
    public static readonly AuditEventType DefinitionWrittenEventType = new("AuthorizationDefinitionWritten");

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
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
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
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
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
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
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
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
        return await FindAsync(context, tenantId, definitionId, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAtAsync(
        TenantId tenantId,
        RoleReference role,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(ct);
        await using var context = await CreateContextAsync(ct).ConfigureAwait(false);
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
