using Harborline.Api.Foundation.Localization;
using Harborline.Api.LocalNodeHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Localization;

[Collection("Harborline process environment")]
public sealed class ComposedHostLocalizationTests
{
    [Fact]
    public async Task Composed_host_resolves_and_formats_the_foundation_shared_localizer()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"t449-localization-{Guid.NewGuid():N}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            await LocalNodeHostRuntime.StartAsync("t449-localization", dataDirectory, deadline.Token);

            var services = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            var localizer = services.GetRequiredService<IHarborlineLocalizer<SharedResource>>();

            Assert.Equal("Info", localizer.Get("severity.info"));
            Assert.Equal("Info", localizer.Format("severity.info", new { }));
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(deadline.Token);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
