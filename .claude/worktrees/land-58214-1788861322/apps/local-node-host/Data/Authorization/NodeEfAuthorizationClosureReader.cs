using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>Reads and lazily reconciles the rebuildable principal-to-scoped-atom authorization index.</summary>
public sealed class NodeEfAuthorizationClosureReader(
    IDbContextFactory<NodeLocalSearchDbContext> contextFactory,
    AuthorizationClosureReconciler reconciler) : IAuthorizationClosureReader, IAuthorizationClosureSnapshotReader
{
    public NodeEfAuthorizationClosureReader(IDbContextFactory<NodeLocalSearchDbContext> contextFactory)
        : this(contextFactory, new AuthorizationClosureReconciler())
    {
    }

    public ValueTask<PermissionAtomSet> UserPermissionsAsync(
        TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default) =>
        new(RunFencedAsync(tenantId, (connection, transaction) =>
            ReadUserPermissionsAsync(connection, transaction, tenantId, principal, at, ct), ct));

    public ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(
        TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default) =>
        new(RunFencedAsync(tenantId, (connection, transaction) =>
            ReadAssignedUsersAsync(connection, transaction, tenantId, required, at, ct), ct));

    public ValueTask<PermissionAtomSet> RolePermissionsAsync(
        TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
        new(RunFencedAsync(tenantId, (connection, transaction) =>
            ReadRolePermissionsAsync(connection, transaction, tenantId, role, ct), ct));

    public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
        AuthorizationGateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(RunFencedAsync(request.Tenant, (connection, transaction) =>
            ReadSnapshotAsync(connection, transaction, request, ct), ct));
    }

    private async Task<T> RunFencedAsync<T>(
        TenantId tenantId,
        Func<SqliteConnection, SqliteTransaction, Task<T>> read,
        CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        T? result = default;
        await HomeEpochFenceTransaction.RunAsync(context, async () =>
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            var transaction = (SqliteTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
            await reconciler.ReconcileTenantAsync(connection, transaction, tenantId, ct).ConfigureAwait(false);
            result = await read(connection, transaction).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result!;
    }

    internal static Task ReconcileTenantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        CancellationToken ct) =>
        ReconcileTenantAsync(
            connection,
            transaction,
            tenantId,
            static () => Task.CompletedTask,
            static (_, _) => Task.CompletedTask,
            ct);

    internal static async Task ReconcileTenantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        Func<Task> afterVersionsObserved,
        Func<SqliteConnection, SqliteTransaction, Task> afterRowsInserted,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var (catalogVersion, tenantVersion, builtCatalogVersion, builtTenantVersion) =
            await ReadVersionsAsync(connection, transaction, tenantId, ct).ConfigureAwait(false);
        if (catalogVersion == builtCatalogVersion && tenantVersion == builtTenantVersion)
            return;

        await afterVersionsObserved().ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM authorization_principal_atom_closure WHERE tenant_id = $tenant;";
            delete.Parameters.AddWithValue("$tenant", tenantId.Value);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                INSERT INTO authorization_principal_atom_closure
                    (tenant_id, principal_id, operation, scope_type, scope_value,
                     role_vocabulary, role_name, grant_id, definition_id,
                     grant_owner_version, valid_from_unix_ms, valid_to_unix_ms)
                SELECT g.tenant_id,
                       g.subject_id,
                       d.operation,
                       d.scope_type,
                       CASE
                           WHEN d.scope_value = '/' THEN g.scope_value
                           WHEN g.scope_value = '/' THEN d.scope_value
                           WHEN g.scope_value = d.scope_value THEN g.scope_value
                           WHEN substr(g.scope_value, 1, length(d.scope_value) + 1) = d.scope_value || '/' THEN g.scope_value
                           ELSE d.scope_value
                       END,
                       g.role_vocabulary,
                       g.role_name,
                       g.grant_id,
                       d.definition_id,
                       g.owner_version,
                       CASE
                           WHEN g.granted_at_unix_ms > g.validity_from_unix_ms THEN g.granted_at_unix_ms
                           ELSE g.validity_from_unix_ms
                       END,
                       -- Ticket 212 slice 3: a revoked grant is INDEXED, with its window truncated at the
                       -- revocation instant, instead of being dropped. That is exactly AccessGrant.IsActiveAt
                       -- ("at < RevokedAt"), so every window-filtered read of this index keeps refusing it at
                       -- and after the revocation, and the snapshot read can name GrantRevoked as the reason
                       -- rather than reporting an absence. Dropping the row is what made the revocation
                       -- counterfactual silent on the production path.
                       CASE
                           WHEN g.revoked_at_unix_ms IS NOT NULL
                            AND (g.validity_until_unix_ms IS NULL
                                 OR g.revoked_at_unix_ms < g.validity_until_unix_ms)
                               THEN g.revoked_at_unix_ms
                           ELSE g.validity_until_unix_ms
                       END
                FROM search_grants AS g
                JOIN authorization_capability_definitions AS d
                  ON d.revision = (
                      SELECT MAX(current_definition.revision)
                      FROM authorization_capability_definitions AS current_definition
                      WHERE current_definition.definition_id = d.definition_id)
                JOIN authorization_capability_offered_roles AS offered
                  ON offered.definition_id = d.definition_id
                 AND offered.revision = d.revision
                 AND offered.vocabulary = g.role_vocabulary
                 AND offered.role_name = g.role_name
                WHERE g.tenant_id = $tenant
                  AND (
                       NOT EXISTS (
                           SELECT 1
                           FROM authorization_binding_revisions AS binding
                           WHERE binding.tenant_id = g.tenant_id
                             AND binding.definition_id = d.definition_id)
                       OR EXISTS (
                           SELECT 1
                           FROM authorization_binding_roles AS selected
                           WHERE selected.tenant_id = g.tenant_id
                             AND selected.definition_id = d.definition_id
                             AND selected.revision = (
                                 SELECT MAX(current_binding.revision)
                                 FROM authorization_binding_revisions AS current_binding
                                 WHERE current_binding.tenant_id = g.tenant_id
                                   AND current_binding.definition_id = d.definition_id)
                             AND selected.vocabulary = g.role_vocabulary
                             AND selected.role_name = g.role_name))
                  AND d.scope_type = g.scope_type
                  AND (
                       d.scope_value = '/'
                       OR g.scope_value = '/'
                       OR d.scope_value = g.scope_value
                       OR substr(g.scope_value, 1, length(d.scope_value) + 1) = d.scope_value || '/'
                       OR substr(d.scope_value, 1, length(g.scope_value) + 1) = g.scope_value || '/');
                """;
            rebuild.Parameters.AddWithValue("$tenant", tenantId.Value);
            await rebuild.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await afterRowsInserted(connection, transaction).ConfigureAwait(false);

        await using var stamp = connection.CreateCommand();
        stamp.Transaction = transaction;
        stamp.CommandText = """
            INSERT INTO authorization_closure_state
                (tenant_id, built_catalog_version, built_tenant_version)
            VALUES ($tenant, $catalog, $tenantVersion)
            ON CONFLICT(tenant_id) DO UPDATE SET
                built_catalog_version = excluded.built_catalog_version,
                built_tenant_version = excluded.built_tenant_version;
            """;
        stamp.Parameters.AddWithValue("$tenant", tenantId.Value);
        stamp.Parameters.AddWithValue("$catalog", catalogVersion);
        stamp.Parameters.AddWithValue("$tenantVersion", tenantVersion);
        await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<PermissionAtomSet> ReadUserPermissionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        ActorId principal,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var atoms = new List<PermissionAtom>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT operation, scope_value
            FROM authorization_principal_atom_closure
            WHERE tenant_id = $tenant
              AND principal_id = $principal
              AND valid_from_unix_ms <= $at
              AND (valid_to_unix_ms IS NULL OR $at < valid_to_unix_ms)
            ORDER BY operation, scope_value;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId.Value);
        command.Parameters.AddWithValue("$principal", principal.Value);
        command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var operation = AuthorizationOperation.Parse(reader.GetString(0));
            atoms.Add(new PermissionAtom(operation, ScopeExpression.Parse(reader.GetString(1))));
        }
        return PermissionAtomSet.From(atoms);
    }

    private static async Task<AuthorizationClosureSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorizationGateRequest request,
        CancellationToken ct)
    {
        var derivations = new List<AuthorizationAtomDerivation>();
        var excluded = new List<AuthorizationExcludedBinding>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Ticket 212 slice 2: the validity and revocation predicates that used to live in this WHERE now run
        // in C# below, so a binding they reject is RECORDED as an exclusion instead of vanishing. The set of
        // derivations handed to the gate is unchanged — the same conditions, evaluated in the same order.
        command.CommandText = """
            SELECT c.operation,
                   c.scope_value,
                   c.role_vocabulary,
                   c.role_name,
                   c.grant_id,
                   g.owner_version,
                   c.definition_id,
                   g.scope_value,
                   c.valid_from_unix_ms,
                   c.valid_to_unix_ms,
                   g.status,
                   g.revoked_at_unix_ms,
                   g.granted_at_unix_ms,
                   g.validity_from_unix_ms,
                   g.validity_until_unix_ms
            FROM authorization_principal_atom_closure AS c
            JOIN search_grants AS g
              ON g.tenant_id = c.tenant_id
             AND g.grant_id = c.grant_id
             AND g.subject_id = c.principal_id
            WHERE c.tenant_id = $tenant
              AND c.principal_id = $principal
              AND c.scope_type = $scopeType
              AND (c.scope_value = '/' OR c.scope_value = $scope
                   OR substr($scope, 1, length(c.scope_value) + 1) = c.scope_value || '/')
              AND c.grant_owner_version = g.owner_version
            ORDER BY c.role_vocabulary, c.role_name, c.operation, c.scope_value,
                     c.grant_id, c.definition_id;
            """;
        command.Parameters.AddWithValue("$tenant", request.Tenant.Value);
        command.Parameters.AddWithValue("$principal", request.Principal.Value);
        command.Parameters.AddWithValue("$scopeType", (int)request.Target.Scope.Type);
        command.Parameters.AddWithValue("$scope", request.Target.Scope.Value);
        command.Parameters.AddWithValue("$at", request.At.ToUnixTimeMilliseconds());

        var at = request.At.ToUnixTimeMilliseconds();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var operation = AuthorizationOperation.Parse(reader.GetString(0));
            var reason = ClassifyExclusion(
                closureValidFrom: reader.GetInt64(8),
                closureValidTo: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                status: reader.GetInt64(10),
                revokedAt: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                grantedAt: reader.GetInt64(12),
                validityFrom: reader.GetInt64(13),
                validityUntil: reader.IsDBNull(14) ? null : reader.GetInt64(14),
                at: at);
            var derivation = new AuthorizationAtomDerivation(
                new PermissionAtom(operation, ScopeExpression.Parse(reader.GetString(1))),
                new RoleReference(reader.GetString(2), reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                ScopeExpression.Parse(reader.GetString(7)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                // The in-force fact this reader itself computed, from the same classification: excluded rows
                // carry false. Nothing downstream re-derives the window.
                InForce: reason is null);
            if (reason is { } excludedFor)
                excluded.Add(new AuthorizationExcludedBinding(derivation, excludedFor));
            else
                derivations.Add(derivation);
        }

        return new AuthorizationClosureSnapshot(derivations, excluded);
    }

    /// <summary>
    /// Returns why this row is not an effective binding at <paramref name="at"/>, or null when it is one.
    /// The conditions are exactly the ones the read predicate used to carry (ticket 212 slice 2); recording
    /// the reason is the only new behaviour.
    /// </summary>
    private static AuthorizationExclusionReason? ClassifyExclusion(
        long closureValidFrom,
        long? closureValidTo,
        long status,
        long? revokedAt,
        long grantedAt,
        long validityFrom,
        long? validityUntil,
        long at)
    {
        // Revocation is a temporal fact, read the way AccessGrant.IsActiveAt reads it: the grant was in
        // force until the instant it was revoked. A revoked status with no instant recorded is not a shape
        // the store admits, and it fails closed here rather than being read as in force.
        if (revokedAt is { } revoked ? at >= revoked : status != 0)
            return AuthorizationExclusionReason.GrantRevoked;
        if (closureValidFrom > at || grantedAt > at || validityFrom > at)
            return AuthorizationExclusionReason.NotYetValid;
        if (closureValidTo is { } closureEnd && at >= closureEnd)
            return AuthorizationExclusionReason.ValidityLapsed;
        if (validityUntil is { } grantEnd && at >= grantEnd)
            return AuthorizationExclusionReason.ValidityLapsed;
        return null;
    }

    private static async Task<IReadOnlyList<ActorId>> ReadAssignedUsersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        PermissionAtom required,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var users = new List<ActorId>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT principal_id
            FROM authorization_principal_atom_closure
            WHERE tenant_id = $tenant
              AND operation = $operation
              AND scope_type = $scopeType
              AND (scope_value = '/' OR scope_value = $scope
                   OR substr($scope, 1, length(scope_value) + 1) = scope_value || '/')
              AND valid_from_unix_ms <= $at
              AND (valid_to_unix_ms IS NULL OR $at < valid_to_unix_ms)
            ORDER BY principal_id;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId.Value);
        command.Parameters.AddWithValue("$operation", required.Operation.Value);
        command.Parameters.AddWithValue("$scopeType", (int)required.Scope.Type);
        command.Parameters.AddWithValue("$scope", required.Scope.Value);
        command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) users.Add(new ActorId(reader.GetString(0)));
        return users;
    }

    private static async Task<PermissionAtomSet> ReadRolePermissionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        RoleReference role,
        CancellationToken ct)
    {
        var atoms = new List<PermissionAtom>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT d.operation, d.scope_value
            FROM authorization_capability_definitions AS d
            JOIN authorization_capability_offered_roles AS offered
              ON offered.definition_id = d.definition_id
             AND offered.revision = d.revision
             AND offered.vocabulary = $vocabulary
             AND offered.role_name = $role
            WHERE d.revision = (
                    SELECT MAX(current_definition.revision)
                    FROM authorization_capability_definitions AS current_definition
                    WHERE current_definition.definition_id = d.definition_id)
              AND (
                   NOT EXISTS (
                       SELECT 1 FROM authorization_binding_revisions AS binding
                       WHERE binding.tenant_id = $tenant
                         AND binding.definition_id = d.definition_id)
                   OR EXISTS (
                       SELECT 1 FROM authorization_binding_roles AS selected
                       WHERE selected.tenant_id = $tenant
                         AND selected.definition_id = d.definition_id
                         AND selected.revision = (
                             SELECT MAX(current_binding.revision)
                             FROM authorization_binding_revisions AS current_binding
                             WHERE current_binding.tenant_id = $tenant
                               AND current_binding.definition_id = d.definition_id)
                         AND selected.vocabulary = $vocabulary
                         AND selected.role_name = $role))
            ORDER BY d.operation, d.scope_value;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId.Value);
        command.Parameters.AddWithValue("$vocabulary", role.Vocabulary);
        command.Parameters.AddWithValue("$role", role.Name);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var operation = AuthorizationOperation.Parse(reader.GetString(0));
            atoms.Add(new PermissionAtom(operation, ScopeExpression.Parse(reader.GetString(1))));
        }
        return PermissionAtomSet.From(atoms);
    }

    private static async Task<(long Catalog, long Tenant, long BuiltCatalog, long BuiltTenant)> ReadVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COALESCE((SELECT version FROM authorization_catalog_version WHERE id = 1), 0),
                COALESCE((SELECT version FROM authorization_tenant_versions WHERE tenant_id = $tenant), 0),
                COALESCE((SELECT built_catalog_version FROM authorization_closure_state WHERE tenant_id = $tenant), -1),
                COALESCE((SELECT built_tenant_version FROM authorization_closure_state WHERE tenant_id = $tenant), -1);
            """;
        command.Parameters.AddWithValue("$tenant", tenantId.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }
}

/// <summary>The fixed reconciliation gate for publishing a fresh tenant closure inside its read transaction.</summary>
public sealed class AuthorizationClosureReconciler
{
    private readonly Func<Task> _afterVersionsObserved;
    private readonly Func<SqliteConnection, SqliteTransaction, Task> _afterRowsInserted;

    public AuthorizationClosureReconciler()
        : this(
            static () => Task.CompletedTask,
            static (_, _) => Task.CompletedTask)
    {
    }

    internal AuthorizationClosureReconciler(
        Func<Task> afterVersionsObserved,
        Func<SqliteConnection, SqliteTransaction, Task> afterRowsInserted)
    {
        _afterVersionsObserved = afterVersionsObserved;
        _afterRowsInserted = afterRowsInserted;
    }

    /// <summary>Rebuilds a stale tenant under the caller's already-held BEGIN IMMEDIATE transaction.</summary>
    internal Task ReconcileTenantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        CancellationToken ct) =>
        NodeEfAuthorizationClosureReader.ReconcileTenantAsync(
            connection,
            transaction,
            tenantId,
            _afterVersionsObserved,
            _afterRowsInserted,
            ct);
}
