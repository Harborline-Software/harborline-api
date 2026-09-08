using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Encryption;

/// <summary>
/// ADR 0115 SC-4 amendment (boundary Option A1) tests — the host keys the
/// relational store with the <b>injected Store DEK</b> (<c>LocalNode__StoreDekHex</c>)
/// when present, NOT the root-seed-derived key. This is what makes the store
/// recoverable independently of the Keychain seed.
/// </summary>
/// <remarks>
/// Exercises <see cref="LocalNodeSqlCipherRegistration.AddSqlCipherLocalNodeDbContextWithStoreDek"/>
/// through a real on-disk file via the encryption guard, then inspects the file
/// with raw SQLCipher opens. SC-1 fail-closed is preserved (the same interceptor
/// + guard run); only the key source changes.
/// </remarks>
public sealed class SqlCipherStoreDekTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqlCipherStoreDekTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-sc4-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");
    }

    private static byte[] FreshDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private static IReadOnlyList<IHarborlineEntityModule> NoModules() => [];

    /// <summary>
    /// Materializes the encrypted store keyed with an explicit injected Store DEK
    /// (the SC-4 A1 path), then stops the host so the file is flushed/closed.
    /// </summary>
    private async Task MaterializeWithStoreDekAsync(byte[] storeDek)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        builder.Services.AddSqlCipherLocalNodeDbContextWithStoreDek(
            storeDek: storeDek,
            databasePath: _dbPath);

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
        SqliteConnection.ClearAllPools();
    }

    private static long? CountSchemaUnderKey(string dbPath, byte[] key)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        conn.Open();
        var hex = Convert.ToHexString(key);
        using (var keyCmd = conn.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{hex}'\";";
            keyCmd.ExecuteNonQuery();
        }
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT count(*) FROM sqlite_schema;";
        return Convert.ToInt64(probe.ExecuteScalar());
    }

    [Fact(DisplayName = "SC-4(A1): a store created under the injected Store DEK opens with THAT DEK")]
    public async Task StoreCreatedWithInjectedDek_OpensWithThatDek()
    {
        var dek = FreshDek();
        await MaterializeWithStoreDekAsync(dek);

        Assert.True(File.Exists(_dbPath), "the encrypted store file should have been created");
        var count = CountSchemaUnderKey(_dbPath, dek);
        Assert.True(count >= 0, "the store should be readable under the injected Store DEK");
    }

    [Fact(DisplayName = "SC-4(A1): the injected-DEK store is NOT openable with the legacy HKDF(rootSeed) key")]
    public async Task StoreCreatedWithInjectedDek_RejectsRootSeedDerivedKey()
    {
        var dek = FreshDek();
        await MaterializeWithStoreDekAsync(dek);

        // A root seed + the legacy derivation produce a DIFFERENT key. The whole
        // point of SC-4 is that the store key is the random DEK, NOT HKDF(seed),
        // so the seed-derived key must NOT open it. (This is also why a Keychain
        // wipe no longer means total loss: the seed never keyed the store.)
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        var hkdfKey = new SqlCipherKeyDerivation().DeriveSqlCipherKey(
            seed, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);

        using var conn = new SqliteConnection($"Data Source={_dbPath};");
        conn.Open();
        var hex = Convert.ToHexString(hkdfKey);
        using (var keyCmd = conn.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{hex}'\";";
            keyCmd.ExecuteNonQuery();
        }
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT count(*) FROM sqlite_schema;";
        Assert.Throws<SqliteException>(() => probe.ExecuteScalar());
    }

    [Fact(DisplayName = "SC-4(A1): the injected-DEK store is encrypted at rest (no plaintext header)")]
    public async Task InjectedDekStore_IsEncryptedAtRest()
    {
        await MaterializeWithStoreDekAsync(FreshDek());

        var bytes = await File.ReadAllBytesAsync(_dbPath);
        Assert.True(bytes.Length > 0, "store file should be non-empty");
        var magic = "SQLite format 3\0"u8.ToArray();
        var headerMatches = bytes.Length >= magic.Length
            && bytes.AsSpan(0, magic.Length).SequenceEqual(magic);
        Assert.False(headerMatches,
            "injected-DEK store begins with the plaintext SQLite magic header — NOT encrypted (SC-1 violation)");
    }

    [Fact(DisplayName = "SC-4(A1): a wrong-length Store DEK fails closed at registration")]
    public void WrongLengthStoreDek_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        var shortDek = new byte[16];

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSqlCipherLocalNodeDbContextWithStoreDek(
                storeDek: shortDek,
                databasePath: _dbPath));
        Assert.Contains("32 bytes", ex.Message);
    }

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
        catch
        {
            // best-effort temp cleanup
        }
    }
}
