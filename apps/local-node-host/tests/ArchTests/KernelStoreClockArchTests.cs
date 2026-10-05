using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1057 (T-690 slice 2), DES-0029 <c>kernel-core-ck-9</c>: a store persists what the act hands it, so it holds no
/// clock and stamps every row with the instant its caller supplies. A second instant read inside a store is a
/// second "now" inside the act it serves.
/// <para>
/// The inventory is discovered by symbol (CONTRIBUTING: fences discover by symbol, never by name). A persistence
/// type is any production type that takes or holds a persistence handle: an EF <see cref="DbContext"/> or
/// <see cref="IDbContextFactory{TContext}"/>, a <see cref="DbConnection"/>, or one of the solution's storage
/// primitives (<see cref="IEncryptedStore"/>, <see cref="IBlobStore"/>, <see cref="IOfflineStore"/>,
/// <see cref="IKeyStore"/>). A persistence type is a store unless it is classified otherwise on its own symbol:
/// <see cref="ClockAuthorityAttribute"/> (it decides something and dates its own decision rows), or it is a hosted
/// service (it runs outside any act). A store fails if it holds a <see cref="TimeProvider"/> (a constructor
/// parameter or an instance field, its own or inherited) or reads the wall clock anywhere in its code, its state
/// machines and closures included. A new store that stamps fails here with no list edited.
/// </para>
/// <para>
/// Prior art and deviation: a Roslyn banned-API analyzer bans a symbol everywhere, and an ArchUnit layer rule bans a
/// dependency by layer; neither can say "a clock is allowed in an authority but not in a store" without a
/// classification, so this fence reads the classification from the type itself (an attribute, as an analyzer's
/// allow-attribute would). A request-scoped clock (NodaTime <c>IClock</c> per request) would make the store's clock
/// the act's at runtime; it is T-1057's DI-closure question and is not decided here.
/// </para>
/// </summary>
public sealed class KernelStoreClockArchTests
{
    /// <summary>
    /// Stores whose own-clock reads are owed to a later slice, each with its reason. Shorter than the
    /// KernelClockIntegrationTests theory it backs, and every row must still be a clocked store (no stale rows).
    /// </summary>
    private static readonly (string Type, string Reason)[] Owed = [];

    [Fact(DisplayName = "T-1057 ck-9: no store holds a clock or reads the wall clock; authorities say why they date their own rows")]
    public void StoresTakeTheirInstantFromTheAct()
    {
        var assemblies = ProductionAssemblies();
        Assert.True(Owed.Length < 8, "The owed rows must stay shorter than the theory they back.");
        Assert.All(Owed, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));

        var holders = PersistenceTypes(assemblies);
        Assert.True(holders.Count > 100, $"Persistence discovery found only {holders.Count} types.");
        var clocked = ClockedStores(holders).Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();
        var owed = Owed.Select(row => row.Type).Order(StringComparer.Ordinal).ToArray();
        Assert.True(clocked.SequenceEqual(owed),
            "A store persists what the act hands it: take the instant from the caller (the act's admitted instant), "
            + "or classify the type [ClockAuthority(\"what it decides and why the instant is its own\")]. "
            + "Clocked stores: " + Environment.NewLine + string.Join(Environment.NewLine, clocked.Except(owed))
            + Environment.NewLine + "Stale owed rows: " + string.Join(", ", owed.Except(clocked)));
    }

    [Fact(DisplayName = "T-1057: every clock-authority classification gives a reason and still describes a clocked persistence type")]
    public void ClockAuthorityClassificationsAreLive()
    {
        var holders = PersistenceTypes(ProductionAssemblies());
        var classified = ProductionAssemblies().SelectMany(Types)
            .Where(type => type.GetCustomAttribute<ClockAuthorityAttribute>() is not null).ToArray();
        Assert.NotEmpty(classified);
        Assert.All(classified, type =>
        {
            Assert.False(string.IsNullOrWhiteSpace(type.GetCustomAttribute<ClockAuthorityAttribute>()!.Reason), type.FullName);
            Assert.True(holders.Contains(type) && IsClocked(type),
                $"{type.FullName} is classified a clock authority but holds no clock or no persistence handle: drop the classification.");
        });
    }

    [Fact(DisplayName = "T-1057: a planted store that stamps from its own clock is reported, with no list edited")]
    public void APlantedStampingStoreIsReported()
    {
        var holders = PersistenceTypes([typeof(KernelStoreClockArchTests).Assembly], type => IsWithin(type, typeof(Planted)));

        Assert.Equal(
            [typeof(Planted.InheritsAClock), typeof(Planted.ReadsTheWallClockWhenAwaited), typeof(Planted.StampsFromItsOwnClock),
             typeof(Planted.StoresBehindAnEncryptedStore)],
            ClockedStores(holders).OrderBy(type => type.Name, StringComparer.Ordinal).ToArray());
        Assert.Contains(typeof(Planted.TakesTheActsInstant), holders);
        Assert.Contains(typeof(Planted.DatesItsOwnDecisions), holders);
        Assert.Contains(typeof(Planted.SweepsOutsideAnyAct), holders);
        Assert.False(IsClocked(typeof(Planted.StaleAuthority)));
    }

    /// <summary>Every production type that takes or holds a persistence handle.</summary>
    internal static IReadOnlySet<Type> PersistenceTypes(IEnumerable<Assembly> assemblies, Func<Type, bool>? filter = null) =>
        assemblies.SelectMany(Types)
            .Where(type => !type.IsInterface && !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .Where(type => filter?.Invoke(type) ?? true)
            .Where(type => Inputs(type).Any(IsPersistenceHandle))
            .ToHashSet();

    /// <summary>The persistence types that are stores (not classified an authority, not hosted) and hold or read a clock.</summary>
    internal static IEnumerable<Type> ClockedStores(IEnumerable<Type> persistenceTypes) =>
        persistenceTypes
            .Where(type => type.GetCustomAttribute<ClockAuthorityAttribute>() is null)
            .Where(type => !typeof(IHostedService).IsAssignableFrom(type))
            .Where(IsClocked);

    private static bool IsClocked(Type type) =>
        Inputs(type).Any(input => typeof(TimeProvider).IsAssignableFrom(input))
        || Code(type).Any(method => RawMutationPortSymbolInventoryTests.CalledMethods(method).Any(call => IsWallClockRead(call.Target)));

    /// <summary>What a type takes or holds: every constructor parameter, and every instance field of it and its bases.</summary>
    private static IEnumerable<Type> Inputs(Type type)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var constructor in type.GetConstructors(Declared))
            foreach (var parameter in constructor.GetParameters())
                yield return parameter.ParameterType;
        for (var level = type; level is not null && level != typeof(object); level = level.BaseType)
            foreach (var field in level.GetFields(Declared))
                yield return field.FieldType;
    }

    private static bool IsPersistenceHandle(Type type) =>
        typeof(DbContext).IsAssignableFrom(type)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDbContextFactory<>))
        || typeof(DbConnection).IsAssignableFrom(type)
        || typeof(IEncryptedStore).IsAssignableFrom(type)
        || typeof(IBlobStore).IsAssignableFrom(type)
        || typeof(IOfflineStore).IsAssignableFrom(type)
        || typeof(IKeyStore).IsAssignableFrom(type);

    /// <summary>A static wall-clock read: <see cref="DateTime.UtcNow"/>, <see cref="DateTimeOffset.Now"/> and the like, or <see cref="TimeProvider.System"/>.</summary>
    private static bool IsWallClockRead(MethodBase target) =>
        target is MethodInfo { IsStatic: true, Name: "get_UtcNow" or "get_Now" or "get_Today" } wall
            && (wall.DeclaringType == typeof(DateTime) || wall.DeclaringType == typeof(DateTimeOffset))
        || target is MethodInfo { IsStatic: true, Name: "get_System" } system && system.DeclaringType == typeof(TimeProvider);

    /// <summary>A type's own code: its methods and constructors, and those of its compiler-generated state machines and closures.</summary>
    private static IEnumerable<MethodBase> Code(Type type)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            yield return method;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(nested => nested.IsDefined(typeof(CompilerGeneratedAttribute), false)))
            foreach (var method in Code(nested))
                yield return method;
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }

    private static bool IsWithin(Type candidate, Type owner)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }

    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(Assembly.LoadFrom)
            .ToArray();

    // ── planted shapes: the fence must classify these without anyone listing them ──────────────────────────────

    private static class Planted
    {
        internal sealed class StampsFromItsOwnClock(IDbContextFactory<DbContext> factory, TimeProvider clock)
        {
            internal DateTimeOffset Stamp() => factory is null ? default : clock.GetUtcNow();
        }

        internal sealed class TakesTheActsInstant(IDbContextFactory<DbContext> factory)
        {
            internal DateTimeOffset Stamp(DateTimeOffset at) => factory is null ? default : at;
        }

        internal sealed class ReadsTheWallClockWhenAwaited(DbConnection connection)
        {
            internal async Task<DateTimeOffset> StampAsync()
            {
                await Task.Yield();
                return connection is null ? default : DateTimeOffset.UtcNow;
            }
        }

        internal sealed class StoresBehindAnEncryptedStore(IEncryptedStore store, TimeProvider clock)
        {
            internal DateTimeOffset Stamp() => store is null ? default : clock.GetUtcNow();
        }

        internal abstract class HoldsAClock(TimeProvider clock)
        {
            protected readonly TimeProvider Clock = clock;
        }

        internal sealed class InheritsAClock(IDbContextFactory<DbContext> factory, TimeProvider clock) : HoldsAClock(clock)
        {
            internal DateTimeOffset Stamp() => factory is null ? default : Clock.GetUtcNow();
        }

        [ClockAuthority("Planted: decides admission and dates its own decision rows.")]
        internal sealed class DatesItsOwnDecisions(IDbContextFactory<DbContext> factory, TimeProvider clock)
        {
            internal DateTimeOffset Decide() => factory is null ? default : clock.GetUtcNow();
        }

        internal sealed class SweepsOutsideAnyAct(IDbContextFactory<DbContext> factory, TimeProvider clock) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
                factory is null ? Task.CompletedTask : Task.Delay(clock.GetUtcNow().Millisecond, stoppingToken);
        }

        [ClockAuthority("Planted: classified, but holds no clock.")]
        internal sealed class StaleAuthority(IDbContextFactory<DbContext> factory)
        {
            internal bool Has() => factory is not null;
        }
    }
}
