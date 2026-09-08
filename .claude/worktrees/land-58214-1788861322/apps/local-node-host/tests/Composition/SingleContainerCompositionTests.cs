using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Composition;

public sealed class SingleContainerCompositionTests
{
    [Fact]
    public async Task Shared_Web_App_Uses_The_Composition_Root_Service_Provider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(new NodeCallerSessionToken(configuredToken: null));
        await using var app = builder.Build();

        await using var listener = new SharedHostedWebApp(
            app,
            Options.Create(new LocalNodeOptions()),
            new LocalNodeExecutableEndpointRegistry(),
            NullLogger<SharedHostedWebApp>.Instance,
            app.Services.GetRequiredService<TimeProvider>());

        Assert.Same(app.Services, listener.Services);
    }

    [Fact]
    public void Data_Surface_Exports_No_Container_Bridge_Accessors()
    {
        var accessorTypes = typeof(LocalNodeOptions).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith(
                "Harborline.Api.LocalNodeHost.Data",
                StringComparison.Ordinal) is true)
            .Where(type => type.Name.EndsWith("Accessor", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(accessorTypes);
    }

    [Fact]
    public void Endpoint_Mappers_Are_Not_Hosted_Service_Registrations()
    {
        Assert.Equal(0, LocalNodeHostedComponentCatalog.PinnedEndpointRegistrarCount);
        Assert.Empty(LocalNodeHostedComponentCatalog.EndpointRegistrars);
        Assert.DoesNotContain(
            LocalNodeHostedComponentCatalog.Operational,
            component => component.ComponentType == typeof(SharedHostedWebApp));
    }

    [Fact]
    public void Host_Surface_Exports_No_Container_Bridge_Accessors()
    {
        var accessorTypes = typeof(LocalNodeOptions).Assembly
            .GetExportedTypes()
            .Where(type => type.Name.EndsWith("Accessor", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(accessorTypes);
    }
}
