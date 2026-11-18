using Harborline.Blocks.Calendar.Booking;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Calendar;

public sealed class CalendarCompositionTests
{
    [Fact]
    public async Task Development_graph_validates_without_a_booking_interpreter()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"calendar-composition-{Guid.NewGuid():N}");
        IServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    ["--environment=Development", "--LocalNode:RootSeedHex=" + new string('7', 64),
                     "--LocalNode:Diagnostics:CommsDiagnosticLogging=false", "--Logging:EventLog:LogLevel:Default=None"],
                    sessionTokenOverride: "calendar-composition-probe", dataDirectory: directory,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        Assert.Null(provider.GetService<IBookingInterpreter>());
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
