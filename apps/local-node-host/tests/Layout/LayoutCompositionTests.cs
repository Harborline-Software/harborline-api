using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Layout;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>Composition-root proof that Layout's host boundary and health probe are shipping services.</summary>
public sealed class LayoutCompositionTests
{
    [Fact(DisplayName = "T-735: the final local-node composition resolves the Layout surface host and denial health check")]
    public async Task The_final_local_node_composition_resolves_the_layout_services()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"layout-composition-{Guid.NewGuid():N}");
        IServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        "--environment=Production",
                        "--LocalNode:RootSeedHex=" + new string('7', 64),
                        "--LocalNode:MultiTeam:Enabled=false",
                        "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                        "--Logging:EventLog:LogLevel:Default=None",
                    ],
                    sessionTokenOverride: "layout-composition-probe",
                    dataDirectory: directory,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        _ = provider.GetRequiredService<LayoutSurfaceHost>();
                        _ = provider.GetRequiredService<LayoutDenialHealthCheck>();
                        throw new CompositionProbeCompleteException();
                    }));
        }
        finally
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable) disposable.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CompositionProbeCompleteException : Exception;
}
