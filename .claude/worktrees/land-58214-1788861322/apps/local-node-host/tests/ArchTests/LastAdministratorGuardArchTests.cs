using System.Reflection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ledger L619 — the <c>not_last_administrator()</c> invariant has exactly one implementation, and every
/// production grant-mutation path reaches it. A revoke, expiry edit or validity edit that bypasses the
/// shared boundary fails here.
/// </summary>
public sealed class LastAdministratorGuardArchTests
{
    /// <summary>The <see cref="IGrantStore"/> members that can take a grant out of force.</summary>
    private static readonly string[] MutationMembers =
    [
        nameof(IGrantStore.RevokeAsync),
        nameof(IGrantStore.ChangeValidityAsync),
        nameof(IGrantStore.HandoverAdministratorAsync),
    ];

    private static readonly MethodInfo Guard =
        typeof(LastAdministratorGuard).GetMethod(nameof(LastAdministratorGuard.EnsureNotLastAdministrator))!;

    private static IEnumerable<Type> ProductionGrantStores() =>
        new[] { typeof(NodeEfGrantStore).Assembly, typeof(InMemoryGrantStore).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IGrantStore).IsAssignableFrom(type))
            .Order(Comparer<Type>.Create((left, right) =>
                string.CompareOrdinal(left.FullName, right.FullName)));

    [Fact]
    public void BothProductionGrantStoresAreDiscovered()
    {
        Assert.Equal(
            new[] { typeof(InMemoryGrantStore).FullName, typeof(NodeEfGrantStore).FullName },
            ProductionGrantStores().Select(type => type.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryProductionGrantMutationPathReachesTheLastAdministratorGuard()
    {
        var bypassing = new List<string>();
        foreach (var store in ProductionGrantStores())
        {
            var map = store.GetInterfaceMap(typeof(IGrantStore));
            foreach (var member in MutationMembers)
            {
                var implementation = map.InterfaceMethods
                    .Select((method, index) => (method, target: map.TargetMethods[index]))
                    .Single(pair => pair.method.Name == member).target;
                var reaches = AuthorizationGateArchTests
                    .ReachableMethodsWithinType(implementation, store)
                    .SelectMany(AuthorizationGateArchTests.CalledMethods)
                    .Any(called => called.Module == Guard.Module && called.MetadataToken == Guard.MetadataToken);
                if (!reaches)
                    bypassing.Add($"{store.FullName}.{member}");
            }
        }

        Assert.True(
            bypassing.Count == 0,
            "These grant-mutation paths never reach LastAdministratorGuard.EnsureNotLastAdministrator "
            + $"(ledger L619): {string.Join(", ", bypassing)}");
    }

    /// <summary>The invariant has ONE implementation — a second copy is a second reading of "in force".</summary>
    [Fact]
    public void TheInvariantIsDeclaredExactlyOnce()
    {
        var declarations = new[] { typeof(NodeEfGrantStore).Assembly, typeof(InMemoryGrantStore).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.DeclaringType == method.ReflectedType
                && (method.Name.Contains("LastAdministrator", StringComparison.Ordinal)
                    || method.Name.Contains("AdministratorInForce", StringComparison.Ordinal)))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                $"{typeof(LastAdministratorGuard).FullName}.{nameof(LastAdministratorGuard.EnsureNotLastAdministrator)}",
                $"{typeof(LastAdministratorGuard).FullName}.{nameof(LastAdministratorGuard.IsAdministratorInForce)}",
            },
            declarations);
    }
}
