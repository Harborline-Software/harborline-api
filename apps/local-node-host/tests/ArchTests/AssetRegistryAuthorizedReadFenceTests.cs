using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 358 slice 1 — the gated asset-registry entity READS, discovered by symbol. The inventory is the
/// set of route handlers in <see cref="AssetRegistryRoutes"/> that resolve a
/// <see cref="RequestAuthorization.RefusalAsync"/> decision before reading
/// <see cref="IRegistryEntityRepository"/>: the entity list, the entity detail, and the detail's tree,
/// condition and submission reads. Removing any decision drops that handler out of the discovered set, a
/// handler that reads the repository without a decision is reported as ungated, and reading the repository
/// before deciding puts the read ahead of the guard — each turns this fence red.
/// </summary>
public sealed class AssetRegistryAuthorizedReadFenceTests
{
    [Fact]
    public void Entity_read_route_inventory_is_exact_and_every_read_reaches_the_gate_first()
    {
        // Class: authorized read. Reason: ADR 0060 — the typed-entity rows are projected only after the
        // records:read decision the route resolves for the record it addresses.
        var reads = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AssetRegistryRoutes).Assembly],
            target => target.DeclaringType == typeof(IRegistryEntityRepository)
                && target.Name is nameof(IRegistryEntityRepository.GetByIdAsync)
                    or nameof(IRegistryEntityRepository.ListByTenantAsync)
                    or nameof(IRegistryEntityRepository.ListByTypeAsync),
            type => IsNestedWithin(type, typeof(AssetRegistryRoutes)));
        var guards = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AssetRegistryRoutes).Assembly],
            target => target.DeclaringType == typeof(RequestAuthorization)
                && target.Name == nameof(RequestAuthorization.RefusalAsync),
            type => IsNestedWithin(type, typeof(AssetRegistryRoutes)));

        Assert.NotEmpty(reads);
        Assert.All(guards, guard => Assert.Equal("apps/local-node-host/Health/AssetRegistryRoutes.cs", guard.Path));

        // Every handler that reads the repository decides first: a new reader with no decision (a by-id read
        // above all, since that is how a record is addressed) is named here rather than silently ungated.
        var ungated = reads
            .Where(read => !guards.Any(guard => guard.Symbol == read.Symbol))
            .Select(read => $"{read.Symbol} -> {TargetName(read.Target)} at line {read.Line}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(ungated.Length == 0,
            "Registry entity reads with no records:read decision in the same handler:"
            + Environment.NewLine + string.Join(Environment.NewLine, ungated));
        var byIdReaders = reads
            .Where(read => TargetName(read.Target) == nameof(IRegistryEntityRepository.GetByIdAsync))
            .Select(read => read.Symbol)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, byIdReaders.Length);
        Assert.All(byIdReaders, symbol => Assert.Contains(guards, guard => guard.Symbol == symbol));

        // The gated handlers: exactly the five entity read routes, named by what each one reads — the
        // detail, tree, condition and submission reads address one record by id; the list reads the tenant.
        var gated = reads
            .Where(read => guards.Any(guard => guard.Symbol == read.Symbol))
            .GroupBy(read => read.Symbol, StringComparer.Ordinal)
            .Select(handler => handler
                .Select(read => TargetName(read.Target)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray())
            .OrderBy(names => string.Join(",", names), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(5, gated.Length);
        Assert.All(gated[..4], names => Assert.Equal([nameof(IRegistryEntityRepository.GetByIdAsync)], names));
        Assert.Equal(
            [
                nameof(IRegistryEntityRepository.ListByTenantAsync),
                nameof(IRegistryEntityRepository.ListByTypeAsync),
            ],
            gated[4]);

        // In every gated handler the decision is resolved BEFORE the repository is touched.
        foreach (var read in reads.Where(read => guards.Any(guard => guard.Symbol == read.Symbol)))
        {
            var first = guards.Where(guard => guard.Symbol == read.Symbol).Min(guard => guard.Line);
            Assert.True(first < read.Line, $"{read.Symbol}: read at line {read.Line} precedes its guard at {first}.");
        }
    }

    private static string TargetName(string target) =>
        target.Split('(', 2)[0].Split('.')[^1];

    private static bool IsNestedWithin(Type candidate, Type owner)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }
}
