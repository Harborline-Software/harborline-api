using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 358 slice 1 — the gated asset-registry entity READS, discovered by symbol. The inventory is the
/// set of route handlers in <see cref="AssetRegistryRoutes"/> that resolve a
/// <see cref="RequestAuthorization.RefusalAsync"/> decision before reading
/// <see cref="IRegistryEntityRepository"/>: the entity list and the entity detail. Removing either
/// decision drops that handler out of the discovered set, and reading the repository before deciding puts
/// the read ahead of the guard — both turn this fence red.
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
            target => target.DeclaringType == typeof(IRegistryEntityRepository),
            type => IsNestedWithin(type, typeof(AssetRegistryRoutes)));
        var guards = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AssetRegistryRoutes).Assembly],
            target => target.DeclaringType == typeof(RequestAuthorization)
                && target.Name == nameof(RequestAuthorization.RefusalAsync),
            type => IsNestedWithin(type, typeof(AssetRegistryRoutes)));

        Assert.NotEmpty(reads);
        Assert.All(guards, guard => Assert.Equal("apps/local-node-host/Health/AssetRegistryRoutes.cs", guard.Path));

        // The gated handlers: exactly the two entity read routes, named by what each one reads.
        var gated = reads
            .Where(read => guards.Any(guard => guard.Symbol == read.Symbol))
            .GroupBy(read => read.Symbol, StringComparer.Ordinal)
            .Select(handler => handler
                .Select(read => TargetName(read.Target)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray())
            .OrderBy(names => string.Join(",", names), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, gated.Length);
        Assert.Equal([nameof(IRegistryEntityRepository.GetByIdAsync)], gated[0]);
        Assert.Equal(
            [
                nameof(IRegistryEntityRepository.ListByTenantAsync),
                nameof(IRegistryEntityRepository.ListByTypeAsync),
            ],
            gated[1]);

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
