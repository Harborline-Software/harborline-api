using System.Reflection;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 395: check's compiled planning path cannot reach a pack write port.</summary>
public sealed class PackCheckNoWriteFenceTests
{
    [Fact]
    public void Check_cannot_reach_mutation_audit_or_projection_ports()
    {
        Type[] writePorts =
        [
            typeof(IPackInstallMutationStore),
            typeof(IPackProjectionAdmissionStore),
            typeof(IPackInstallAudit),
            typeof(IPackProjectionDispatcher),
        ];
        var pending = new Stack<MethodBase>();
        var visited = new HashSet<MethodBase>();
        pending.Push(typeof(PackInstaller).GetMethod(nameof(PackInstaller.Check))!);

        while (pending.TryPop(out var caller))
        {
            if (!visited.Add(caller)) continue;
            foreach (var call in RawMutationPortSymbolInventoryTests.CalledMethods(caller))
            {
                Assert.True(call.Target.DeclaringType is not { } declaring || !writePorts.Any(port => port.IsAssignableFrom(declaring)),
                    $"Check can reach a write port through {caller} -> {call.Target} at IL_{call.Offset:x4}.");
                if (call.Target.Module.Assembly == typeof(PackInstaller).Assembly)
                    pending.Push(call.Target);
            }
        }

        Assert.Contains(visited, method => method.Name == "BuildPlan");
    }
}
