using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.OrgBranding;
using Harborline.Api.LocalNodeHost.OrgBranding;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OrgBranding;

/// <summary>
/// Tenant-branding slice T1 gate — the durable EF org-branding store round-trips on the REAL keyed
/// (SQLCipher-encrypted) SQLite store, exercised through the same SC-1 registration path the host uses
/// (<see cref="LocalNodeSqlCipherRegistration.AddSqlCipherLocalNodeDbContext"/>). Proves the migration applies
/// to a FRESH db AND an EXISTING db (idempotent on restart), the profile survives a context reopen, and a
/// foreign tenant is cross-tenant isolated. Mirrors <c>NodeEfCalendarStoreTests</c>.
/// </summary>
public sealed class NodeEfOrgBrandingStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    private readonly string _dir;
    private readonly string _dbPath;

    public NodeEfOrgBrandingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-brand-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");
    }

    [Fact(DisplayName = "fresh db: migration creates the table; profile round-trips")]
    public async Task Fresh_Migrates_And_RoundTrips()
    {
        var seed = FreshRootSeed();
        using var host = await StartKeyedHostAsync(seed);
        var store = Store(host);

        Assert.Null(await store.GetAsync(TenantA)); // fresh — no profile

        var profile = Sample(TenantA.Value);
        await store.UpsertAsync(profile);

        var loaded = await store.GetAsync(TenantA);
        Assert.NotNull(loaded);
        Assert.Equal("Acme Property Co.", loaded!.DisplayName);
        Assert.Equal("bafyLight", loaded.LogoRef);
        Assert.Equal("bafyDark", loaded.LogoDarkRef);
        Assert.Equal("#06489C", loaded.AccentColor);
        Assert.Equal("#FFFFFF", loaded.AccentForeground);
        Assert.Equal("local-operator", loaded.UpdatedBy);
    }

    [Fact(DisplayName = "existing db: migration is idempotent on restart; profile survives")]
    public async Task ExistingDb_MigrationIdempotent_ProfileSurvivesRestart()
    {
        var seed = FreshRootSeed();

        // First boot — fresh migration + write.
        using (var host1 = await StartKeyedHostAsync(seed))
        {
            await Store(host1).UpsertAsync(Sample(TenantA.Value) with { DisplayName = "First Co." });
        }

        SqliteConnection.ClearAllPools(); // release the file handle before reopening (ClearAllPools race)

        // Second boot on the SAME db file — the guard re-runs MigrateAsync against an EXISTING db (idempotent),
        // and the previously-written profile is still there.
        using var host2 = await StartKeyedHostAsync(seed);
        var loaded = await Store(host2).GetAsync(TenantA);

        Assert.NotNull(loaded);
        Assert.Equal("First Co.", loaded!.DisplayName);
    }

    [Fact(DisplayName = "cross-tenant isolated: a foreign tenant reads nothing")]
    public async Task CrossTenant_Isolated()
    {
        var seed = FreshRootSeed();
        using var host = await StartKeyedHostAsync(seed);
        var store = Store(host);

        await store.UpsertAsync(Sample(TenantA.Value));

        Assert.NotNull(await store.GetAsync(TenantA));
        Assert.Null(await store.GetAsync(TenantB)); // TenantB never wrote — isolated by the tenant key
    }

    [Fact(DisplayName = "upsert replaces the existing row (one profile per tenant)")]
    public async Task Upsert_Replaces()
    {
        var seed = FreshRootSeed();
        using var host = await StartKeyedHostAsync(seed);
        var store = Store(host);

        await store.UpsertAsync(Sample(TenantA.Value) with { DisplayName = "Before", AccentColor = null });
        await store.UpsertAsync(Sample(TenantA.Value) with { DisplayName = "After", AccentColor = "#123456" });

        var loaded = await store.GetAsync(TenantA);
        Assert.Equal("After", loaded!.DisplayName);
        Assert.Equal("#123456", loaded.AccentColor);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] FreshRootSeed()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    private static IReadOnlyList<IHarborlineEntityModule> NoModules() => [];

    /// <summary>Boots a host with the SC-1 registration; the encryption guard has migrated the org-branding
    /// context (and every other node-local context) by the time StartAsync returns.</summary>
    private async Task<IHost> StartKeyedHostAsync(byte[] rootSeed)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        builder.Services.AddSqlCipherLocalNodeDbContext(
            rootSeed: rootSeed,
            databasePath: _dbPath,
            keyDerivation: new SqlCipherKeyDerivation());

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static NodeEfOrgBrandingStore Store(IHost host) =>
        new(host.Services.GetRequiredService<IDbContextFactory<NodeLocalOrgBrandingDbContext>>());

    private static OrgBrandingProfile Sample(string tenantId) => new(
        TenantId: tenantId,
        DisplayName: "Acme Property Co.",
        LogoRef: "bafyLight",
        LogoDarkRef: "bafyDark",
        AccentColor: "#06489C",
        AccentForeground: "#FFFFFF",
        UpdatedAt: DateTimeOffset.UtcNow,
        UpdatedBy: "local-operator");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup — a lingering handle on Windows CI must not fail the test.
        }
    }
}
