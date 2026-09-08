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

public sealed class NodeEfAuthorizationConfigurationStore(
    IDbContextFactory<NodeLocalSearchDbContext> factory,
    IRoleVocabularyReader vocabulary)
    : AuthorizationConfigurationStateReader, IAuthorizationConfigurationStore, IAuthorizationDefinitionReader,
        IAuthorizationDefinitionCatalogueReader, IHistoricalAuthorizationConfigurationReader
{
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
                await EnsureRoleAsync(context, role, ct).ConfigureAwait(false);
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
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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

    private async Task EnsureRoleAsync(NodeLocalSearchDbContext context, RoleReference role, CancellationToken ct)
    {
        if (await context.AuthorizationRoles.AnyAsync(
            x => x.Vocabulary == role.Vocabulary && x.RoleName == role.Name, ct).ConfigureAwait(false)) return;
        var definition = await vocabulary.ResolveAsync(role, ct).ConfigureAwait(false)
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
