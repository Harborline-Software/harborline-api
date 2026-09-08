using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Forms.Submission;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.SubmissionBinding;

public sealed class FormSubmitOutboxRegistrationTests
{
    [Fact]
    public async Task Default_in_memory_outbox_is_allowed_in_development()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddFormSubmitProjections();

        await using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryFormSubmitOutbox>(provider.GetRequiredService<IFormSubmitOutbox>());

        var startupService = Assert.Single(provider.GetServices<IHostedService>());
        await startupService.StartAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Default_in_memory_outbox_fails_closed_outside_development(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddFormSubmitProjections();

        await using var provider = services.BuildServiceProvider();
        var startupService = Assert.Single(provider.GetServices<IHostedService>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            startupService.StartAsync(CancellationToken.None));
        Assert.Contains("in-memory form-submit outbox", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "submission-binding-tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
