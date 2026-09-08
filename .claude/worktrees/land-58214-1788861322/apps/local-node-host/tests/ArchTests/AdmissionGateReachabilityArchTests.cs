using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class AdmissionGateReachabilityArchTests
{
    [Fact]
    public async Task EveryFixedGateImplementationResolvesFromRealHostComposition()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"s218-gate-composition-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    ["--LocalNode:RootSeedHex=" + new string('2', 64)],
                    sessionTokenOverride: "s218-gate-probe",
                    dataDirectory: dataDirectory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        var resolved = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        using var providerLifetime = Assert.IsAssignableFrom<IDisposable>(resolved);
                        var admission = resolved.GetRequiredService<IRoleGateAdmission>();
                        Assert.Same(
                            admission,
                            resolved.GetRequiredService<AuthorizedFormDefinitionLifecycle>().RoleGateAdmission);
                        Assert.Same(
                            admission,
                            resolved.GetRequiredService<AuthorizedWorkflowDefinitionLifecycle>().RoleGateAdmission);
                        Assert.Empty(UnreachableFixedGates(resolved, ProductionTypes()));
                        throw new CompositionProbeCompleteException();
                    },
                    installFootprintRootOverride: dataDirectory));
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuiltProviderDetectsAPlantedUnregisteredGate()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal([typeof(PlantedDeadGate)], UnreachableFixedGates(provider, [typeof(PlantedDeadGate)]));
    }

    private static Type[] UnreachableFixedGates(IServiceProvider provider, IEnumerable<Type> candidates) =>
        candidates
            .Where(type => type is { IsClass: true, IsAbstract: false, IsSealed: true })
            .Where(type => type.Name.EndsWith("Gate", StringComparison.Ordinal)
                || type.Name.EndsWith("Authorizer", StringComparison.Ordinal))
            .Where(type => GateInterfaces(type).Length > 0)
            .Where(type => GateInterfaces(type).Any(serviceType =>
                EffectiveImplementation(provider, serviceType) != type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    private static Type? EffectiveImplementation(IServiceProvider provider, Type serviceType)
    {
        try
        {
            return provider.GetRequiredService(serviceType).GetType();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Type[] GateInterfaces(Type type) => type.GetInterfaces()
        .Where(serviceType => serviceType.Name.EndsWith("Gate", StringComparison.Ordinal)
            || serviceType.Name.EndsWith("Authorizer", StringComparison.Ordinal))
        .ToArray();

    private static IEnumerable<Type> ProductionTypes()
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline.*.dll"))
        {
            if (Path.GetFileName(file).Contains(".Tests", StringComparison.Ordinal)) continue;
            var assembly = Assembly.LoadFrom(file);
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types) yield return type;
        }
    }

    private interface IPlantedGate;
    private sealed class PlantedDeadGate : IPlantedGate;
    private sealed class CompositionProbeCompleteException : Exception;
}
