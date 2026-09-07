using System.Collections.Immutable;
using System.Reflection;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// The startup owner that serially applies one node-exclusive context's compiled migrations.
/// </summary>
internal enum LocalNodeExclusiveMigrationOwner
{
    EncryptionGuard,
    Drafts,
}

/// <summary>
/// Code-owned, neutral inventory of every node-exclusive EF context that shares the encrypted
/// local-node SQLite file. The catalog describes compiled registration and migration ownership;
/// it does not claim that a migration has run or that tenant isolation is complete.
/// </summary>
internal static class LocalNodeExclusiveEfContextCatalog
{
    private const string DefaultHistoryTable = "__EFMigrationsHistory";
    private static readonly ImmutableArray<LocalNodeExclusiveEfContextDescriptor> s_all = CreateCatalog();

    internal static IReadOnlyList<LocalNodeExclusiveEfContextDescriptor> All => s_all;

    internal static LocalNodeExclusiveEfContextDescriptor For<TContext>()
        where TContext : DbContext =>
        For(typeof(TContext));

    internal static LocalNodeExclusiveEfContextDescriptor For(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        return s_all.SingleOrDefault(item => item.ContextType == contextType)
            ?? throw new InvalidOperationException(
                $"local-node.exclusive-ef.context_unexpected: context '{contextType.FullName}' is not catalog-owned.");
    }

    internal static IReadOnlyList<ILocalNodeExclusiveContextMigrator> SelectOwnedMigrators(
        IEnumerable<ILocalNodeExclusiveContextMigrator> migrators,
        LocalNodeExclusiveMigrationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(migrators);

        var expected = s_all
            .Where(item => item.Owner == owner)
            .OrderBy(item => item.ExecutionOrder)
            .ToArray();
        var observed = migrators
            .Where(item => item.Descriptor.Owner == owner)
            .OrderBy(item => item.Descriptor.ExecutionOrder)
            .ToArray();

        if (observed.Length != expected.Length ||
            observed.Select(item => item.Descriptor.ContextType).Distinct().Count() != observed.Length ||
            !observed.Select(item => item.Descriptor.ContextType)
                .SequenceEqual(expected.Select(item => item.ContextType)))
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.owner_coverage: owner '{owner}' does not cover its exact catalog set.");
        }

        return observed;
    }

    /// <summary>
    /// Registers one catalog-owned context with the shared SQLCipher interceptor and its one
    /// catalog-backed serial migration step.
    /// </summary>
    internal static IServiceCollection AddLocalNodeExclusiveSqlCipherContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        SqlCipherConnectionInterceptor interceptor)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(interceptor);

        var descriptor = For<TContext>();
        services.AddLocalNodeSaveChangesEnlistment();
        services.AddDbContextFactory<TContext>((provider, options) =>
        {
            options.UseSqlite(connectionString, sqlite =>
                sqlite.MigrationsHistoryTable(descriptor.MigrationsHistoryTable));
            options.AddInterceptors(interceptor);
            options.AddLocalNodeSaveChangesEnlistment(provider);
            options.ConfigureWarnings(warnings =>
                warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(ILocalNodeExclusiveContextMigrator),
            typeof(LocalNodeExclusiveContextMigrator<TContext>)));

        return services;
    }

    /// <summary>
    /// Closes the final production service graph without constructing a provider or invoking an
    /// options delegate. Missing, duplicate, opaque, non-singleton, or out-of-catalog factories,
    /// migration steps, and startup owners refuse host construction before <c>Build()</c>.
    /// </summary>
    internal static void ValidateLocalNodeExclusiveEfContexts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ValidateFactories(services);
        ValidateMigrators(services);
        ValidateOwners(services);
    }

    private static ImmutableArray<LocalNodeExclusiveEfContextDescriptor> CreateCatalog()
    {
        var catalog = ImmutableArray.Create(
            Describe<Maintenance.NodeLocalMaintenanceDbContext,
                Maintenance.DesignTimeNodeLocalMaintenanceDbContextFactory,
                Maintenance.Migrations.NodeLocalMaintenanceDbContextModelSnapshot>(
                "local-node.ef.maintenance", 10,
                Maintenance.NodeLocalMaintenanceDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Harborline.Api.LocalNodeHost.Data.Properties.NodeLocalPropertyDbContext,
                Harborline.Api.LocalNodeHost.Data.Properties.DesignTimeNodeLocalPropertyDbContextFactory,
                Harborline.Api.LocalNodeHost.Data.Properties.Migrations.NodeLocalPropertyDbContextModelSnapshot>(
                "local-node.ef.properties", 20,
                Harborline.Api.LocalNodeHost.Data.Properties.NodeLocalPropertyDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Harborline.Api.LocalNodeHost.Data.Leases.NodeLocalLeaseDbContext,
                Harborline.Api.LocalNodeHost.Data.Leases.DesignTimeNodeLocalLeaseDbContextFactory,
                Harborline.Api.LocalNodeHost.Data.Leases.Migrations.NodeLocalLeaseDbContextModelSnapshot>(
                "local-node.ef.leases", 30,
                Harborline.Api.LocalNodeHost.Data.Leases.NodeLocalLeaseDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Banking.NodeLocalBankFeedDbContext,
                Banking.DesignTimeNodeLocalBankFeedDbContextFactory,
                Banking.Migrations.NodeLocalBankFeedDbContextModelSnapshot>(
                "local-node.ef.bank-feed", 40,
                Banking.NodeLocalBankFeedDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Payroll.NodeLocalPayrollDbContext,
                Payroll.DesignTimeNodeLocalPayrollDbContextFactory,
                Payroll.Migrations.NodeLocalPayrollDbContextModelSnapshot>(
                "local-node.ef.payroll", 50,
                Payroll.NodeLocalPayrollDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Comms.NodeLocalCommsDbContext,
                Comms.DesignTimeNodeLocalCommsDbContextFactory,
                Comms.Migrations.NodeLocalCommsDbContextModelSnapshot>(
                "local-node.ef.comms", 60,
                Comms.NodeLocalCommsDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Roster.NodeLocalRosterDbContext,
                Roster.DesignTimeNodeLocalRosterDbContextFactory,
                Roster.Migrations.NodeLocalRosterDbContextModelSnapshot>(
                "local-node.ef.roster", 80,
                Roster.NodeLocalRosterDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Admission.NodeLocalAdmissionDbContext,
                Admission.DesignTimeNodeLocalAdmissionDbContextFactory,
                Admission.Migrations.NodeLocalAdmissionDbContextModelSnapshot>(
                "local-node.ef.admission", 90,
                Admission.NodeLocalAdmissionDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Search.NodeLocalSearchDbContext,
                Search.DesignTimeNodeLocalSearchDbContextFactory,
                Search.Migrations.NodeLocalSearchDbContextModelSnapshot>(
                "local-node.ef.search", 100,
                Search.NodeLocalSearchDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Calendar.NodeLocalCalendarDbContext,
                Calendar.DesignTimeNodeLocalCalendarDbContextFactory,
                Calendar.Migrations.NodeLocalCalendarDbContextModelSnapshot>(
                "local-node.ef.calendar", 110,
                Calendar.NodeLocalCalendarDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Packs.NodeLocalPacksDbContext,
                Packs.DesignTimeNodeLocalPacksDbContextFactory,
                Packs.Migrations.NodeLocalPacksDbContextModelSnapshot>(
                "local-node.ef.packs", 120,
                Packs.NodeLocalPacksDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<OrgBranding.NodeLocalOrgBrandingDbContext,
                OrgBranding.DesignTimeNodeLocalOrgBrandingDbContextFactory,
                OrgBranding.Migrations.NodeLocalOrgBrandingDbContextModelSnapshot>(
                "local-node.ef.org-branding", 130,
                OrgBranding.NodeLocalOrgBrandingDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Scheduling.NodeLocalSchedulingDbContext,
                Scheduling.DesignTimeNodeLocalSchedulingDbContextFactory,
                Scheduling.Migrations.NodeLocalSchedulingDbContextModelSnapshot>(
                "local-node.ef.scheduling", 140,
                Scheduling.NodeLocalSchedulingDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Identity.NodeLocalInstallationIdentityDbContext,
                Identity.DesignTimeNodeLocalInstallationIdentityDbContextFactory,
                Identity.Migrations.NodeLocalInstallationIdentityDbContextModelSnapshot>(
                "local-node.ef.installation-identity", 150,
                Identity.NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Identity.NodeLocalWebSessionDbContext,
                Identity.DesignTimeNodeLocalWebSessionDbContextFactory,
                Identity.WebSessionMigrations.NodeLocalWebSessionDbContextModelSnapshot>(
                "local-node.ef.web-session", 160,
                Identity.NodeLocalWebSessionDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.EncryptionGuard),
            Describe<Drafts.NodeLocalDraftsDbContext,
                Drafts.DesignTimeNodeLocalDraftsDbContextFactory,
                Migrations.Drafts.NodeLocalDraftsDbContextModelSnapshot>(
                "local-node.ef.drafts", 170,
                Drafts.NodeLocalDraftsDbContext.MigrationsHistoryTableName,
                LocalNodeExclusiveMigrationOwner.Drafts));

        if (catalog.Length != 16 ||
            catalog.Select(item => item.ContextKey).Distinct(StringComparer.Ordinal).Count() != catalog.Length ||
            catalog.Select(item => item.ExecutionOrder).Distinct().Count() != catalog.Length ||
            !catalog.Select(item => item.ExecutionOrder)
                .SequenceEqual(catalog.Select(item => item.ExecutionOrder).Order()) ||
            catalog.Select(item => item.ContextType).Distinct().Count() != catalog.Length ||
            catalog.Select(item => item.MigrationsHistoryTable)
                .Distinct(StringComparer.Ordinal).Count() != catalog.Length)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.catalog_invalid: expected 16 unique context entries.");
        }

        if (catalog.Any(item =>
                string.IsNullOrWhiteSpace(item.MigrationsHistoryTable) ||
                string.Equals(item.MigrationsHistoryTable, DefaultHistoryTable, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.history_invalid: every context requires a unique non-default history table.");
        }

        if (catalog.Count(item => item.Owner == LocalNodeExclusiveMigrationOwner.EncryptionGuard) != 15 ||
            catalog.Count(item => item.Owner == LocalNodeExclusiveMigrationOwner.Drafts) != 1)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.owner_coverage: expected the reviewed 15+1 startup-owner split.");
        }

        return catalog;
    }

    private static LocalNodeExclusiveEfContextDescriptor Describe<TContext, TDesignTimeFactory, TSnapshot>(
        string contextKey,
        int executionOrder,
        string migrationsHistoryTable,
        LocalNodeExclusiveMigrationOwner owner)
        where TContext : DbContext
        where TDesignTimeFactory : IDesignTimeDbContextFactory<TContext>
        where TSnapshot : ModelSnapshot
    {
        if (string.IsNullOrWhiteSpace(contextKey) ||
            !string.Equals(contextKey, contextKey.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.catalog_invalid: context '{typeof(TContext).FullName}' has an invalid key.");
        }

        var snapshotContext = ReadAttributeArgument<Type>(typeof(TSnapshot), typeof(DbContextAttribute));
        if (snapshotContext != typeof(TContext))
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.catalog_invalid: snapshot '{typeof(TSnapshot).FullName}' is not bound to " +
                $"context '{typeof(TContext).FullName}'.");
        }

        var migrations = typeof(TContext).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .Select(type => new
            {
                Type = type,
                Context = ReadAttributeArgument<Type>(type, typeof(DbContextAttribute)),
                Id = ReadAttributeArgument<string>(type, typeof(MigrationAttribute)),
            })
            .Where(item => item.Context == typeof(TContext))
            .Select(item => new LocalNodeCompiledMigrationDescriptor(
                item.Id ?? throw new InvalidOperationException(
                    $"local-node.exclusive-ef.migration_unattributed: migration '{item.Type.FullName}' has no id."),
                item.Type))
            .OrderBy(item => item.MigrationId, StringComparer.Ordinal)
            .ToImmutableArray();

        if (migrations.IsEmpty)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.migration_missing: context '{typeof(TContext).FullName}' has no compiled migration.");
        }

        if (migrations.Select(item => item.MigrationId).Distinct(StringComparer.Ordinal).Count() != migrations.Length)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.migration_duplicate: context '{typeof(TContext).FullName}' has duplicate ids.");
        }

        return new LocalNodeExclusiveEfContextDescriptor(
            contextKey,
            executionOrder,
            typeof(TContext),
            typeof(TDesignTimeFactory),
            typeof(TSnapshot),
            migrationsHistoryTable,
            owner,
            migrations,
            (services, connectionString, interceptor) =>
                services.AddLocalNodeExclusiveSqlCipherContext<TContext>(connectionString, interceptor));
    }

    private static T? ReadAttributeArgument<T>(Type declaringType, Type attributeType)
    {
        var attribute = declaringType.CustomAttributes.SingleOrDefault(item => item.AttributeType == attributeType);
        if (attribute is null || attribute.ConstructorArguments.Count == 0)
        {
            return default;
        }

        return attribute.ConstructorArguments[0].Value is T value ? value : default;
    }

    private static void ValidateFactories(IServiceCollection services)
    {
        var expected = s_all.ToDictionary(
            item => typeof(IDbContextFactory<>).MakeGenericType(item.ContextType),
            item => item);
        var registrations = services
            .Where(item => item.ServiceType.IsGenericType &&
                item.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>))
            .ToArray();

        var unexpected = registrations.FirstOrDefault(item =>
        {
            var context = item.ServiceType.GenericTypeArguments[0];
            return context != typeof(LocalNodeDbContext) && !expected.ContainsKey(item.ServiceType);
        });
        if (unexpected is not null)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.context_unexpected: factory context " +
                $"'{unexpected.ServiceType.GenericTypeArguments[0].FullName}' is not catalog-owned.");
        }

        ValidateOneFactory(
            registrations,
            typeof(IDbContextFactory<LocalNodeDbContext>),
            typeof(LocalNodeDbContext));

        foreach (var pair in expected.OrderBy(item => item.Value.ExecutionOrder))
        {
            ValidateOneFactory(registrations, pair.Key, pair.Value.ContextType);
        }
    }

    private static void ValidateOneFactory(
        IReadOnlyList<ServiceDescriptor> registrations,
        Type serviceType,
        Type contextType)
    {
        var observed = registrations.Where(item => item.ServiceType == serviceType).ToArray();
        if (observed.Length == 0)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.factory_missing: '{contextType.FullName}'.");
        }

        if (observed.Length != 1)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.factory_duplicate: '{contextType.FullName}'.");
        }

        if (observed[0].ImplementationType is null)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.factory_opaque: '{contextType.FullName}'.");
        }

        if (observed[0].Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.factory_lifetime: '{contextType.FullName}'.");
        }
    }

    private static void ValidateMigrators(IServiceCollection services)
    {
        var expected = s_all.ToDictionary(
            item => typeof(LocalNodeExclusiveContextMigrator<>).MakeGenericType(item.ContextType),
            item => item);
        var registrations = services
            .Where(item => item.ServiceType == typeof(ILocalNodeExclusiveContextMigrator))
            .ToArray();

        var opaque = registrations.FirstOrDefault(item => item.ImplementationType is null);
        if (opaque is not null)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.owner_opaque: migration-step factories and instances are not admitted.");
        }

        var unexpected = registrations.FirstOrDefault(item => !expected.ContainsKey(item.ImplementationType!));
        if (unexpected is not null)
        {
            throw new InvalidOperationException(
                $"local-node.exclusive-ef.context_unexpected: migration step " +
                $"'{unexpected.ImplementationType!.FullName}' is not catalog-owned.");
        }

        foreach (var pair in expected.OrderBy(item => item.Value.ExecutionOrder))
        {
            var observed = registrations.Where(item => item.ImplementationType == pair.Key).ToArray();
            if (observed.Length == 0)
            {
                throw new InvalidOperationException(
                    $"local-node.exclusive-ef.owner_missing: migration step for '{pair.Value.ContextType.FullName}'.");
            }

            if (observed.Length != 1)
            {
                throw new InvalidOperationException(
                    $"local-node.exclusive-ef.owner_duplicate: migration step for '{pair.Value.ContextType.FullName}'.");
            }

            if (observed[0].Lifetime != ServiceLifetime.Singleton)
            {
                throw new InvalidOperationException(
                    $"local-node.exclusive-ef.factory_lifetime: migration step for '{pair.Value.ContextType.FullName}'.");
            }
        }
    }

    private static void ValidateOwners(IServiceCollection services)
    {
        var guard = FindOwner(services, typeof(LocalNodeStoreEncryptionGuard));
        var drafts = FindOwner(services, typeof(Drafts.NodeDraftsMigrator));

        if (guard.Count == 0 || drafts.Count == 0)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.owner_missing: both reviewed startup owners must be registered.");
        }

        if (guard.Count != 1 || drafts.Count != 1)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.owner_duplicate: each reviewed startup owner must be registered once.");
        }

        if (guard.Descriptor!.Lifetime != ServiceLifetime.Singleton ||
            drafts.Descriptor!.Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.factory_lifetime: startup owners must be singleton hosted services.");
        }

        if (guard.Index >= drafts.Index)
        {
            throw new InvalidOperationException(
                "local-node.exclusive-ef.owner_coverage: SQLCipher verification owner must precede Drafts.");
        }
    }

    private static (int Count, int Index, ServiceDescriptor? Descriptor) FindOwner(
        IServiceCollection services,
        Type ownerType)
    {
        var matches = services
            .Select((descriptor, index) => new { descriptor, index })
            .Where(item => item.descriptor.ServiceType == typeof(IHostedService) &&
                item.descriptor.ImplementationType == ownerType)
            .ToArray();
        return (
            matches.Length,
            matches.Length == 0 ? -1 : matches[0].index,
            matches.Length == 0 ? null : matches[0].descriptor);
    }
}

/// <summary>Neutral assembly-binding evidence for one compiled exclusive-context migration.</summary>
internal sealed record LocalNodeCompiledMigrationDescriptor(string MigrationId, Type MigrationType);

/// <summary>Neutral registration and compiled-migration evidence for one exclusive context.</summary>
internal sealed record LocalNodeExclusiveEfContextDescriptor(
    string ContextKey,
    int ExecutionOrder,
    Type ContextType,
    Type DesignTimeFactoryType,
    Type ModelSnapshotType,
    string MigrationsHistoryTable,
    LocalNodeExclusiveMigrationOwner Owner,
    ImmutableArray<LocalNodeCompiledMigrationDescriptor> CompiledMigrations,
    Action<IServiceCollection, string, SqlCipherConnectionInterceptor> Register);

/// <summary>A catalog-owned serial migration step. It is executed by exactly one startup owner.</summary>
internal interface ILocalNodeExclusiveContextMigrator
{
    LocalNodeExclusiveEfContextDescriptor Descriptor { get; }

    Task MigrateAsync(CancellationToken cancellationToken);
}

internal sealed class LocalNodeExclusiveContextMigrator<TContext>(IDbContextFactory<TContext> factory)
    : ILocalNodeExclusiveContextMigrator
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    public LocalNodeExclusiveEfContextDescriptor Descriptor =>
        LocalNodeExclusiveEfContextCatalog.For<TContext>();

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await _factory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
