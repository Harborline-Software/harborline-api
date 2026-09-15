using Harborline.Api.Foundation.Localization;
using Harborline.Api.LocalNodeHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Localization;

[Collection("Harborline process environment")]
public sealed class ComposedHostLocalizationTests
{
    // The root seed override keeps the composed boot off the platform keystore, which Linux and
    // macOS runners do not provide (PlatformNotSupportedException: libsecret is Wave 2).
    private const string RootSeedHex =
        "3463463463463463463463463463463463463463463463463463463463463463";

    [Fact]
    public async Task Composed_host_resolves_and_formats_the_foundation_shared_localizer()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"t449-localization-{Guid.NewGuid():N}");
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);
            await LocalNodeHostRuntime.StartAsync("t449-localization", dataDirectory, deadline.Token);

            var services = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            var localizer = services.GetRequiredService<IHarborlineLocalizer<SharedResource>>();

            Assert.Equal("Info", localizer.Get("severity.info"));
            Assert.Equal("Info", localizer.Format("severity.info", new { }));
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(deadline.Token);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
