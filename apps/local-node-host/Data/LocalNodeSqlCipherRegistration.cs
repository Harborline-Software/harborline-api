using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Kernel.Security.Keys;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// The single sanctioned composition path for the SQLCipher-encrypted local-node relational store.
/// Both key sources converge on the same main-context and exclusive-context registration graph.
/// </summary>
public static class LocalNodeSqlCipherRegistration
{
    private static int s_providerInitialized;

    /// <summary>Domain-separation label for the install-level relational-store key.</summary>
    public const string RelationalStoreKeyId = "__sunfish-local-node-relational__";

    /// <summary>Registers the store with a key derived from the install root seed.</summary>
    public static IServiceCollection AddSqlCipherLocalNodeDbContext(
        this IServiceCollection services,
        ReadOnlySpan<byte> rootSeed,
        string databasePath,
        ISqlCipherKeyDerivation keyDerivation,
        bool pooling = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentNullException.ThrowIfNull(keyDerivation);
        if (rootSeed.Length != 32)
        {
            throw new ArgumentException(
                $"Root seed must be 32 bytes (was {rootSeed.Length}); the host must fail closed.",
                nameof(rootSeed));
        }

        var dek = keyDerivation.DeriveSqlCipherKey(rootSeed, RelationalStoreKeyId);
        return RegisterWithResolvedDek(services, dek, databasePath, pooling);
    }

    /// <summary>Registers the same store graph with a shell-resolved recoverable Store DEK.</summary>
    public static IServiceCollection AddSqlCipherLocalNodeDbContextWithStoreDek(
        this IServiceCollection services,
        ReadOnlySpan<byte> storeDek,
        string databasePath,
        bool pooling = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (storeDek.Length != 32)
        {
            throw new ArgumentException(
                $"Store DEK must be 32 bytes (was {storeDek.Length}); the host must fail closed.",
                nameof(storeDek));
        }

        return RegisterWithResolvedDek(services, storeDek.ToArray(), databasePath, pooling);
    }

    private static IServiceCollection RegisterWithResolvedDek(
        IServiceCollection services,
        byte[] dek,
        string databasePath,
        bool pooling)
    {
        try
        {
            // Microsoft.EntityFrameworkCore.Sqlite can bring a non-cipher provider into the graph.
            // Pin e_sqlcipher explicitly so PRAGMA key can never degrade into a silent no-op.
            if (Interlocked.Exchange(ref s_providerInitialized, 1) == 0)
            {
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
            }

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var interceptor = new SqlCipherConnectionInterceptor(dek);

            var connectionString = $"Data Source={databasePath};" + (pooling ? string.Empty : "Pooling=False;");
            services.AddLocalNodeSaveChangesEnlistment();
            services.AddDbContextFactory<LocalNodeDbContext>((provider, options) =>
            {
                options.UseSqlite(connectionString);
                options.AddInterceptors(interceptor);
                options.AddLocalNodeSaveChangesEnlistment(provider);
                options.ConfigureWarnings(warnings =>
                    warnings.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            });

            foreach (var descriptor in LocalNodeExclusiveEfContextCatalog.All)
            {
                descriptor.Register(services, connectionString, interceptor);
            }

            // This owner proves the main store key first, then migrates its exact catalog subset.
            services.AddHostedService<LocalNodeStoreEncryptionGuard>();
            return services;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}

/// <summary>
/// Proves the main local-node store opens under SQLCipher before applying any node-exclusive schema.
/// Migration failures abort startup; there is no plaintext or EnsureCreated fallback.
/// </summary>
internal sealed class LocalNodeStoreEncryptionGuard : IHostedService
{
    private readonly IDbContextFactory<LocalNodeDbContext> _factory;
    private readonly IReadOnlyList<ILocalNodeExclusiveContextMigrator> _migrators;
    private readonly ILogger<LocalNodeStoreEncryptionGuard> _logger;

    public LocalNodeStoreEncryptionGuard(
        IDbContextFactory<LocalNodeDbContext> factory,
        IEnumerable<ILocalNodeExclusiveContextMigrator> migrators,
        ILogger<LocalNodeStoreEncryptionGuard> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _migrators = LocalNodeExclusiveEfContextCatalog.SelectOwnedMigrators(
            migrators,
            LocalNodeExclusiveMigrationOwner.EncryptionGuard);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await _factory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Opening invokes the shared interceptor: PRAGMA key followed by a verification probe.
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidKeyException)
        {
            _logger.LogCritical(
                "SC-1 fail-closed: the local-node store is not openable with its SQLCipher key; " +
                "aborting startup without a plaintext fallback.");
            throw;
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        foreach (var migrator in _migrators)
        {
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "SC-1 verified and {ContextCount} catalog-owned exclusive context migrations applied serially.",
            _migrators.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
