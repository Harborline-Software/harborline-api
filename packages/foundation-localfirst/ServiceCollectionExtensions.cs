using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.LocalFirst.Export;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Foundation.LocalFirst.Quarantine;
using Harborline.Api.Kernel.Events;

namespace Harborline.Api.Foundation.LocalFirst;

/// <summary>DI conveniences for <see cref="Harborline.Api.Foundation.LocalFirst"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the one durable identity and filesystem footprint shared by an install.</summary>
    /// <param name="services">Service collection to add the install services to.</param>
    /// <param name="productDataDirectory">
    /// Current user's Harborline data root. Defaults to
    /// <see cref="InstallFootprintPaths.GetDefaultProductDataDirectory"/>.
    /// </param>
    /// <param name="installIdentityFilePath">
    /// Installer-owned identity record retained across upgrades. Defaults to
    /// <see cref="InstallIdentityPaths.GetDefaultIdentityFilePath(string?)"/>.
    /// </param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddHarborlineInstallFootprint(
        this IServiceCollection services,
        string? productDataDirectory = null,
        string? installIdentityFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IInstallIdentityProvider>(_ =>
            new FileInstallIdentityProvider(
                installIdentityFilePath ?? InstallIdentityPaths.GetDefaultIdentityFilePath()));
        services.TryAddSingleton<IInstallFootprintProvider>(sp =>
        {
            var usesPlatformDefaults = productDataDirectory is null;
            return new FileInstallFootprintProvider(
                sp.GetRequiredService<IInstallIdentityProvider>(),
                productDataDirectory ?? InstallFootprintPaths.GetDefaultProductDataDirectory(),
                usesPlatformDefaults ? InstallFootprintPaths.GetLegacyDataDirectory() : null,
                usesPlatformDefaults ? InstallFootprintPaths.GetLegacyDatabasePath() : null,
                usesPlatformDefaults ? InstallFootprintPaths.GetLegacyKeystoreDirectory() : null);
        });
        return services;
    }

    /// <summary>
    /// Registers the in-memory <see cref="IOfflineStore"/> and the default
    /// <see cref="CausalConflictResolver"/> (ADR 0053: causal dominance decides,
    /// identical payloads resolve, and everything else is returned as a
    /// <see cref="ConflictResolution.Ask"/> — never picked by timestamp; engines
    /// must populate <see cref="SyncConflict.LocalClock"/> /
    /// <see cref="SyncConflict.RemoteClock"/>), and the JSON
    /// tenant-portability export stack.
    /// Sync engine and import services are bundle / accelerator concerns and are not registered
    /// here. The paper §11.2 Layer 4 quarantine queue is registered separately via
    /// <see cref="AddHarborlineQuarantineQueue"/>.
    /// </summary>
    public static IServiceCollection AddHarborlineLocalFirst(this IServiceCollection services)
    {
        services.AddSingleton<IOfflineStore, InMemoryOfflineStore>();
        services.AddSingleton<ISyncConflictResolver, CausalConflictResolver>();
        services.AddSingleton<IExportContributor, OfflineStoreExportContributor>();
        services.AddSingleton<IDataExportService, JsonPortabilityDataExportService>();
        return services;
    }

    /// <summary>
    /// Registers the paper §11.2 Layer 4 quarantine queue, persisted to an
    /// <see cref="IEventLog"/>. Offline writes that fail validation land here pending review.
    /// Requires an <see cref="IEventLog"/> to already be registered.
    /// </summary>
    public static IServiceCollection AddHarborlineQuarantineQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IQuarantineQueue>(sp =>
            new EventLogBackedQuarantineQueue(
                sp.GetRequiredService<IEventLog>(),
                sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Registers the paper §11.2 Layer 1 encrypted local store stack:
    /// <see cref="IEncryptedStore"/> (SQLCipher), <see cref="IKeyDerivation"/>
    /// (Argon2id), and the platform-appropriate <see cref="IKeystore"/>
    /// (Windows DPAPI; macOS/Linux stubs). All are singletons.
    /// </summary>
    /// <remarks>
    /// The encrypted store itself is returned <em>unopened</em>. Applications
    /// are responsible for calling <see cref="IEncryptedStore.OpenAsync"/> with
    /// the derived key once during startup (typically after the user has
    /// authenticated and the key is available from the keystore).
    /// </remarks>
    public static IServiceCollection AddHarborlineEncryptedStore(
        this IServiceCollection services,
        Action<EncryptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddHarborlineInstallFootprint();
            services.AddOptions<EncryptionOptions>()
                .Configure<IInstallFootprintProvider>((options, footprints) =>
                {
                    options.DatabasePath = footprints
                        .GetInstallFootprintAsync(CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult()
                        .DatabasePath;
                });
        }

        services.AddSingleton<IKeyDerivation>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;
            return new Argon2idKeyDerivation(options.Argon2Options);
        });

        services.AddSingleton<IKeystore>(sp =>
        {
            var footprint = sp.GetService<IInstallFootprintProvider>()?
                .GetInstallFootprintAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return Keystore.CreateForCurrentPlatform(footprint?.KeystoreDirectory);
        });
        services.AddSingleton<IEncryptedStore>(_ => new SqlCipherEncryptedStore());

        return services;
    }
}
