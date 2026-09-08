using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

public sealed class GrantFreshnessMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260624152259_DurableSubjectErasure";
    private const string FreshnessMigration = "20260714104253_GrantFreshnessRows";
    private const string PermissionMigration = "20260805090000_GrantPermissionSets";
    private string _directory = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"harborline-adm01a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* Best-effort test cleanup. */ }
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("PlanCard", "ADM-01A")]
    public async Task Fresh_migration_creates_owner_version_and_empty_principal_epoch_authority()
    {
        var path = Path.Combine(_directory, "fresh.db");
        await using var context = CreateContext(path);

        await context.Database.MigrateAsync();

        Assert.Contains(FreshnessMigration, await context.Database.GetAppliedMigrationsAsync());
        Assert.Contains(PermissionMigration, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("1", await ScalarAsync(path,
            "SELECT dflt_value FROM pragma_table_info('search_grants') WHERE name = 'owner_version';"));
        Assert.Equal("0", await ScalarAsync(path, "SELECT count(*) FROM search_grant_authorization_epochs;"));
    }

    [Fact]
    [Trait("PlanCard", "ADM-01A")]
    public async Task Authorization_clean_break_discards_existing_permission_grants()
    {
        var path = Path.Combine(_directory, "upgrade.db");
        await using var context = CreateContext(path);
        await context.Database.MigrateAsync(PreviousMigration);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO search_grants (
                grant_id, tenant_id, principal_id, roles_json, scope_json, residency,
                validity_from_unix_ms, validity_until_unix_ms, granted_by, granted_at_unix_ms,
                revoked_at_unix_ms, source_reference)
            VALUES ('11111111-1111-1111-1111-111111111111', 'tenant-a', 'principal-a', '[]',
                '{{"wholeTenant":true,"recordIds":[]}}', 0, 0, NULL, 'issuer-a', 0, NULL, NULL);
            """);

        await context.Database.MigrateAsync();

        Assert.Equal("0", await ScalarAsync(path, "SELECT count(*) FROM search_grants;"));
        // The clean break discards both the untruthful legacy grant and its now-orphaned freshness row.
        Assert.Equal("0", await ScalarAsync(path, "SELECT count(*) FROM search_grant_authorization_epochs;"));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3666")]
    public async Task Authorization_clean_break_does_not_fabricate_a_role_for_legacy_ship_rows()
    {
        var path = Path.Combine(_directory, "legacy-permissions.db");
        await using var context = CreateContext(path);
        await context.Database.MigrateAsync(PreviousMigration);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO search_grants (
                grant_id, tenant_id, principal_id, roles_json, scope_json, residency,
                validity_from_unix_ms, validity_until_unix_ms, granted_by, granted_at_unix_ms,
                revoked_at_unix_ms, source_reference)
            VALUES ('eeeeeeee-0000-0000-0000-000000000001', 'tenant-a', 'principal-a', '["Scribe"]',
                '{{"wholeTenant":true,"recordIds":[]}}', 0, 0, NULL, 'issuer-a', 0, NULL, NULL);
            """);

        await context.Database.MigrateAsync();

        Assert.Empty(await context.Grants.AsNoTracking().ToArrayAsync());
    }

    /// <summary>The authorization clean break removes every epoch whose legacy grant is discarded.</summary>
    [Fact]
    [Trait("PlanCard", "ADM-01A")]
    public async Task Authorization_clean_break_removes_epochs_for_every_discarded_principal()
    {
        var path = Path.Combine(_directory, "epoch-backfill.db");
        await using var context = CreateContext(path);
        await context.Database.MigrateAsync(PreviousMigration);
        // principal-a holds two grants (one live, one revoked); principal-b holds one live grant;
        // principal-c holds ONLY a revoked grant (no live grant to fall back on); principal-d holds only a
        // not-yet-valid grant. The backfill is per-PRINCIPAL and does not evaluate liveness, so that is four
        // epoch rows. principal-c is the load-bearing fixture for "a revoked grant still earns one, because
        // revocation advances the epoch rather than removing it" — with principal-a alone (which also holds
        // a live grant) that claim is unfalsifiable, since principal-a earns its epoch via the live row
        // regardless of the revoked one. principal-d is the fixture for the migration's other documented
        // claim, that not-yet-valid grants are included on purpose.
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("aaaaaaaa-0000-0000-0000-000000000001", "principal-a", revokedAtUnixMs: null));
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("aaaaaaaa-0000-0000-0000-000000000002", "principal-a", revokedAtUnixMs: 5));
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("bbbbbbbb-0000-0000-0000-000000000001", "principal-b", revokedAtUnixMs: null));
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("cccccccc-0000-0000-0000-000000000001", "principal-c", revokedAtUnixMs: 5));
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("dddddddd-0000-0000-0000-000000000001", "principal-d", revokedAtUnixMs: null,
                validityFromUnixMs: 4102444800000));

        await context.Database.MigrateAsync();

        Assert.Equal("0", await ScalarAsync(path, "SELECT count(*) FROM search_grant_authorization_epochs;"));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// The backfill must never disturb an epoch a real grant write already minted and advanced — it fills
    /// gaps only. It must also fill every gap independently: a second principal in the SAME tenant with no
    /// epoch row of its own must still get one. This is the load-bearing check that the <c>NOT EXISTS</c>
    /// guard correlates on principal_id as well as tenant_id — a tenant-only guard would see principal-a's
    /// pre-existing row, conclude the whole tenant is already backfilled, and permanently orphan principal-b
    /// (the failure is silent and unrecoverable, because EF marks the migration applied either way).
    /// </summary>
    [Fact]
    [Trait("PlanCard", "ADM-01A")]
    public async Task Authorization_clean_break_removes_preexisting_and_backfilled_epochs()
    {
        var path = Path.Combine(_directory, "epoch-preserve.db");
        await using var context = CreateContext(path);
        await context.Database.MigrateAsync(FreshnessMigration);
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("cccccccc-0000-0000-0000-000000000001", "principal-a", revokedAtUnixMs: null));
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO search_grant_authorization_epochs (tenant_id, principal_id, authorization_epoch)
            VALUES ('tenant-a', 'principal-a', 9);
            """);
        await context.Database.ExecuteSqlRawAsync(
            LegacyGrantInsert("cccccccc-0000-0000-0000-000000000002", "principal-b", revokedAtUnixMs: null));

        await context.Database.MigrateAsync();

        Assert.Equal("0", await ScalarAsync(path, "SELECT count(*) FROM search_grant_authorization_epochs;"));
    }

    /// <summary>
    /// The grant scope envelope, with its braces DOUBLED — <c>ExecuteSqlRaw</c> runs the statement through
    /// <c>string.Format</c>, so a single brace is read as a format placeholder and throws.
    /// </summary>
    private const string ScopeJsonForExecuteSqlRaw = "{{\"wholeTenant\":true,\"recordIds\":[]}}";

    private static string LegacyGrantInsert(
        string grantId,
        string principalId,
        long? revokedAtUnixMs,
        long validityFromUnixMs = 0) =>
        $$"""
        INSERT INTO search_grants (
            grant_id, tenant_id, principal_id, roles_json, scope_json, residency,
            validity_from_unix_ms, validity_until_unix_ms, granted_by, granted_at_unix_ms,
            revoked_at_unix_ms, source_reference)
        VALUES ('{{grantId}}', 'tenant-a', '{{principalId}}', '[]',
            '{{ScopeJsonForExecuteSqlRaw}}', 0, {{validityFromUnixMs}}, NULL, 'issuer-a', 0,
            {{(revokedAtUnixMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL")}},
            NULL);
        """;

    [Fact]
    [Trait("PlanCard", "ADM-01A")]
    public async Task Freshness_rows_round_trip_after_context_restart()
    {
        var path = Path.Combine(_directory, "restart.db");
        await using (var first = CreateContext(path))
        {
            await first.Database.MigrateAsync();
            first.Grants.Add(NewGrantRow(ownerVersion: 7));
            first.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
            {
                TenantId = "tenant-a",
                PrincipalId = "principal-a",
                AuthorizationEpoch = 11,
            });
            await first.SaveChangesAsync();
        }

        await using var restarted = CreateContext(path);
        var grant = await restarted.Grants.AsNoTracking().SingleAsync();
        var epoch = await restarted.GrantAuthorizationEpochs.AsNoTracking().SingleAsync();

        Assert.Equal(7, grant.OwnerVersion);
        Assert.Equal(11, epoch.AuthorizationEpoch);
        Assert.Equal(("tenant-a", "principal-a"), (epoch.TenantId, epoch.PrincipalId));
    }

    private static GrantRow NewGrantRow(long ownerVersion) => new()
    {
        GrantId = "22222222-2222-2222-2222-222222222222",
        TenantId = "tenant-a",
        SubjectId = "principal-a",
        RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
        RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
        ScopeValue = "/",
        Residency = 0,
        ValidityFromUnixMs = 0,
        GrantedBy = "issuer-a",
        GrantedAtUnixMs = 0,
        GranterKind = (int)GranterKind.Person,
        Source = (int)GrantSourceKind.Manual,
        Approver = "issuer-a",
        LastReviewedAtUnixMs = 0,
        OwnerVersion = ownerVersion,
    };

    private static NodeLocalSearchDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NodeLocalSearchDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalSearchDbContext.MigrationsHistoryTableName))
            .Options;
        return new NodeLocalSearchDbContext(options);
    }

    private static async Task<string> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
