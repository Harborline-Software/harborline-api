using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Composition;

public sealed class SingleContainerCompositionTests
{
    [Fact]
    public async Task Program_Composition_Provides_Isolated_Selected_Session_Request_Contexts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"selected-session-composition-{Guid.NewGuid():N}");
        IServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        "--environment=Production",
                        "--LocalNode:RootSeedHex=" + new string('7', 64),
                        "--LocalNode:WebClient:Enabled=true",
                        "--LocalNode:MultiTeam:Enabled=false",
                        "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                        "--Logging:EventLog:LogLevel:Default=None",
                    ],
                    sessionTokenOverride: "selected-session-composition-token",
                    dataDirectory: directory,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        throw new CompositionProbeCompleteException();
                    }));

            Assert.NotNull(provider);
            // The shipping listener uses this final Program provider, not its legacy inner builder.
            // Removing the concrete scoped registration must fail at the same lookup as middleware.
            await using var firstRequest = provider.CreateAsyncScope();
            var first = firstRequest.ServiceProvider.GetRequiredService<SelectedSessionTenantContext>();
            var principal = new SelectedSessionRequestPrincipal(
                accountId: "founder-account",
                tenantId: new TenantId("founder-team"),
                principalUserId: new PrincipalUserId("founder-principal"),
                canonicalParty: new CanonicalPartyReference("founder-party"),
                membershipId: "founder-membership",
                membershipOwnerVersion: 1,
                pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("founder-grant", 1)],
                authorizationEpoch: 1,
                sessionCorrelationId: "founder-session",
                coordinationCorrelationId: "founder-coordination");
            first.Bind(principal);

            Assert.Same(first, firstRequest.ServiceProvider.GetRequiredService<SelectedSessionTenantContext>());
            Assert.Equal(principal.TenantId, first.Tenant!.Id);
            Assert.Equal(principal.CanonicalParty.Value, first.UserId);

            await using var secondRequest = provider.CreateAsyncScope();
            var second = secondRequest.ServiceProvider.GetRequiredService<SelectedSessionTenantContext>();
            Assert.NotSame(first, second);
            Assert.Null(second.Tenant);
            Assert.Empty(second.UserId);
            Assert.Null(second.EffectivePermissions);
        }
        finally
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable) disposable.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CompositionProbeCompleteException : Exception;

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
