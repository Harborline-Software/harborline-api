using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class AccessHoldersAuthorizedReadFenceTests
{
    [Fact]
    public void Grant_snapshot_route_inventory_is_exact_and_the_read_reaches_the_gate_first()
    {
        // Class: authorized read. Reason: live holdings are projected only after the members atom gate.
        // Discover every grant snapshot reader in the route namespace by symbol, including async owners.
        var reads = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AuthorizationAdminRoutes).Assembly],
            target => target.DeclaringType == typeof(IGrantStore) && target.Name == nameof(IGrantStore.SnapshotAsync),
            type => type.Namespace == typeof(AuthorizationAdminRoutes).Namespace);
        var read = Assert.Single(reads);
        Assert.Equal("apps/local-node-host/Health/AccessHoldersRead.cs", read.Path);
        Assert.Equal("Harborline.Api.LocalNodeHost.Health.AccessHoldersRead.ReadAsync(Microsoft.AspNetCore.Http.HttpContext,Harborline.Api.Foundation.Assets.Common.TenantId,System.TimeProvider,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Microsoft.AspNetCore.Http.IResult]", read.Symbol);
        Assert.Equal(33, read.Line);
        var guards = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AuthorizationAdminRoutes).Assembly],
            target => target.DeclaringType == typeof(RequestAuthorization) && target.Name == nameof(RequestAuthorization.RefusalAsync),
            type => type == typeof(AccessHoldersRead) || type.DeclaringType == typeof(AccessHoldersRead));
        var guard = Assert.Single(guards);
        Assert.Equal(read.Symbol, guard.Symbol);
        Assert.Equal(read.Path, guard.Path);
        Assert.True(guard.Line < read.Line);
    }
}
