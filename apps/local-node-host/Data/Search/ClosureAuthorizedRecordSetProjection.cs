using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// The sole search clip projection. It reconciles and reads grant-provenanced closure rows in the caller's
/// transaction, retaining only cache-resident derivations of <c>records:read</c>.
/// </summary>
public sealed class ClosureAuthorizedRecordSetProjection : IAuthorizedRecordSetProjection
{
    public async Task<AuthorizedRecordScope> ResolveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        ActorId principalId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await NodeEfAuthorizationClosureReader
            .ReconcileTenantAsync(connection, transaction, tenantId, ct)
            .ConfigureAwait(false);

        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT closure.scope_value
            FROM authorization_principal_atom_closure AS closure
            JOIN search_grants AS grant_row
              ON grant_row.tenant_id = closure.tenant_id
             AND grant_row.grant_id = closure.grant_id
            WHERE closure.tenant_id = $tenant
              AND closure.principal_id = $principal
              AND closure.operation = 'records:read'
              AND closure.valid_from_unix_ms <= $at
              AND (closure.valid_to_unix_ms IS NULL OR $at < closure.valid_to_unix_ms)
              AND grant_row.residency = $cache
              AND grant_row.status = 0
            ORDER BY closure.scope_value;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId.Value);
        command.Parameters.AddWithValue("$principal", principalId.Value);
        command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$cache", (int)GrantResidency.Cache);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var scope = reader.GetString(0);
            if (scope is "/" or "/records") return AuthorizedRecordScope.EntireTenant;
            const string prefix = "/records/";
            if (!scope.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = scope[prefix.Length..];
            var childSeparator = remainder.IndexOf('/', StringComparison.Ordinal);
            var recordId = childSeparator < 0 ? remainder : remainder[..childSeparator];
            if (recordId.Length > 0)
                recordIds.Add(recordId);
        }
        return AuthorizedRecordScope.ForRecordIds(recordIds);
    }
}
